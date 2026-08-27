using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Jobs;

/// <summary>Restart-safe storage for the job queue and its history.</summary>
public interface IJobStore
{
    /// <summary>Adds a job to the queue.</summary>
    /// <param name="job">The job.</param>
    void Add(EncodeJob job);

    /// <summary>Persists changes to a job that is already stored.</summary>
    /// <param name="job">The job.</param>
    void Update(EncodeJob job);

    /// <summary>Gets one job by id.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job, or null.</returns>
    EncodeJob? Get(Guid id);

    /// <summary>Gets every job, newest first.</summary>
    /// <returns>All jobs.</returns>
    IReadOnlyList<EncodeJob> GetAll();

    /// <summary>Gets the jobs that are still queued or running.</summary>
    /// <returns>Active jobs.</returns>
    IReadOnlyList<EncodeJob> GetActive();

    /// <summary>Takes the next queued job, marking it as claimed.</summary>
    /// <returns>The next job, or null when the queue is empty.</returns>
    EncodeJob? TakeNextQueued();

    /// <summary>Returns true when an item already has a queued or running job.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when a job is already active for the item.</returns>
    bool HasActiveJobForItem(Guid itemId);

    /// <summary>Removes a job from the store entirely.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>True when a job was removed.</returns>
    bool Remove(Guid id);

    /// <summary>
    /// Marks jobs that were mid-flight when the server stopped. Called once at startup.
    /// </summary>
    /// <returns>The jobs that were reset.</returns>
    IReadOnlyList<EncodeJob> ReconcileInterrupted();
}

/// <inheritdoc />
public class JobStore : IJobStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<JobStore> _logger;
    private readonly Lock _lock = new Lock();
    private readonly Dictionary<Guid, EncodeJob> _jobs = new Dictionary<Guid, EncodeJob>();

    /// <summary>Initializes a new instance of the <see cref="JobStore"/> class.</summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public JobStore(IApplicationPaths appPaths, ILogger<JobStore> logger)
    {
        _logger = logger;

        var dir = Path.Combine(appPaths.DataPath, "mediaoptimizer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "jobs.json");

        Load();
    }

    /// <inheritdoc />
    public void Add(EncodeJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;
            Trim();
            Save();
        }
    }

    /// <inheritdoc />
    public void Update(EncodeJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;
            Save();
        }
    }

    /// <inheritdoc />
    public EncodeJob? Get(Guid id)
    {
        lock (_lock)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EncodeJob> GetAll()
    {
        lock (_lock)
        {
            return _jobs.Values.OrderByDescending(j => j.QueuedAt).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EncodeJob> GetActive()
    {
        lock (_lock)
        {
            return _jobs.Values.Where(j => j.IsActive).OrderBy(j => j.QueuedAt).ToList();
        }
    }

    /// <inheritdoc />
    public EncodeJob? TakeNextQueued()
    {
        lock (_lock)
        {
            var next = _jobs.Values
                .Where(j => j.Status == JobStatus.Queued)
                .OrderBy(j => j.QueuedAt)
                .FirstOrDefault();

            if (next is null)
            {
                return null;
            }

            next.Status = JobStatus.Preflight;
            next.StartedAt = DateTime.UtcNow;
            Save();
            return next;
        }
    }

    /// <inheritdoc />
    public bool HasActiveJobForItem(Guid itemId)
    {
        lock (_lock)
        {
            return _jobs.Values.Any(j => j.ItemId == itemId && j.IsActive);
        }
    }

    /// <inheritdoc />
    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var removed = _jobs.Remove(id);
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EncodeJob> ReconcileInterrupted()
    {
        lock (_lock)
        {
            var stranded = _jobs.Values
                .Where(j => j.Status is JobStatus.Preflight or JobStatus.Encoding
                    or JobStatus.Verifying or JobStatus.Applying)
                .ToList();

            foreach (var job in stranded)
            {
                // Applying is the only phase where the original may already have moved, so it is
                // flagged rather than silently requeued: a human needs to look at it.
                job.Status = JobStatus.Interrupted;
                job.Error = job.Status == JobStatus.Applying
                    ? "The server stopped while the output was being moved into place. Check the file before requeueing."
                    : "The server stopped while this job was running. The original file was not modified.";
                job.FinishedAt = DateTime.UtcNow;
            }

            if (stranded.Count > 0)
            {
                Save();
                _logger.LogWarning("[MediaOptimizer] Marked {Count} interrupted job(s) after restart", stranded.Count);
            }

            return stranded;
        }
    }

    private void Trim()
    {
        var limit = Plugin.Instance?.Configuration.JobHistoryLimit ?? 200;
        if (limit <= 0)
        {
            return;
        }

        var finished = _jobs.Values
            .Where(j => !j.IsActive)
            .OrderByDescending(j => j.FinishedAt ?? j.QueuedAt)
            .Skip(limit)
            .ToList();

        foreach (var job in finished)
        {
            // Never drop a job whose original is still recoverable; that record is the only
            // thing pointing at the quarantined file.
            if (job.CanRevert)
            {
                continue;
            }

            _jobs.Remove(job.Id);
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var jobs = JsonSerializer.Deserialize<List<EncodeJob>>(json, SerializerOptions);
            if (jobs is null)
            {
                return;
            }

            foreach (var job in jobs)
            {
                _jobs[job.Id] = job;
            }

            _logger.LogInformation("[MediaOptimizer] Loaded {Count} job(s) from disk", _jobs.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not read the job store at {Path}", _path);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_jobs.Values.ToList(), SerializerOptions);

            // Write to a temporary file and rename, so a crash mid-write cannot corrupt the queue.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not persist the job store to {Path}", _path);
        }
    }
}
