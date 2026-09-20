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

namespace Jellyfin.Plugin.MediaOptimizer.Move;

/// <summary>Restart-safe storage for the move queue and its history.</summary>
public interface IMoveJobStore
{
    /// <summary>Adds a job to the queue.</summary>
    /// <param name="job">The job.</param>
    void Add(MoveJob job);

    /// <summary>Persists changes to a job that is already stored.</summary>
    /// <param name="job">The job.</param>
    void Update(MoveJob job);

    /// <summary>
    /// Records progress on a running job, writing to disk at most every few seconds. A copy
    /// reports several times a second and the numbers are worth nothing after a restart, so
    /// persisting every sample would rewrite the file for no benefit.
    /// </summary>
    /// <param name="job">The job.</param>
    void ReportProgress(MoveJob job);

    /// <summary>Gets one job by id.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job, or null.</returns>
    MoveJob? Get(Guid id);

    /// <summary>Gets every job, newest first.</summary>
    /// <returns>All jobs.</returns>
    IReadOnlyList<MoveJob> GetAll();

    /// <summary>Takes the next queued job, marking it as claimed.</summary>
    /// <returns>The next job, or null when the queue is empty.</returns>
    MoveJob? TakeNextQueued();

    /// <summary>Returns true when an item already has a queued or running move.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when a move is already active for the item.</returns>
    bool HasActiveJobForItem(Guid itemId);

    /// <summary>Returns true when a path is already the destination of a queued or running move.</summary>
    /// <param name="path">A destination path.</param>
    /// <returns>True when something else is already heading there.</returns>
    bool IsDestinationClaimed(string path);

    /// <summary>Removes a job from the store entirely.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>True when a job was removed.</returns>
    bool Remove(Guid id);

    /// <summary>
    /// Marks jobs that were mid-move when the server stopped. Called once at startup.
    /// </summary>
    /// <returns>The jobs that were recovered.</returns>
    IReadOnlyList<MoveJob> ReconcileInterrupted();
}

/// <summary>
/// A small JSON-backed store for move jobs.
/// <para>
/// Moves are far rarer than encodes and each one is a single file operation, so this keeps the
/// whole set in one snapshot written atomically — temp file, then rename — rather than carrying
/// the journal the encode queue needs. A torn write is impossible; the worst a crash can cost is
/// the progress percentage of a job that has to be restarted anyway.
/// </para>
/// </summary>
public class MoveJobStore : IMoveJobStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeSpan ProgressPersistInterval = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly ILogger<MoveJobStore> _logger;
    private readonly Lock _lock = new Lock();
    private readonly Dictionary<Guid, MoveJob> _jobs = new Dictionary<Guid, MoveJob>();

    private DateTime _lastProgressWrite = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="MoveJobStore"/> class.</summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public MoveJobStore(IApplicationPaths appPaths, ILogger<MoveJobStore> logger)
        : this(Path.Combine(appPaths.DataPath, "mediaoptimizer"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MoveJobStore"/> class in a given directory.</summary>
    /// <param name="directory">Where the snapshot lives.</param>
    /// <param name="logger">Logger.</param>
    internal MoveJobStore(string directory, ILogger<MoveJobStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "moves.json");
        Load();
    }

    /// <inheritdoc />
    public void Add(MoveJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;
            Trim();
            Persist();
        }
    }

    /// <inheritdoc />
    public void Update(MoveJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;
            Persist();
        }
    }

    /// <inheritdoc />
    public void ReportProgress(MoveJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;

            if (DateTime.UtcNow - _lastProgressWrite < ProgressPersistInterval)
            {
                return;
            }

            _lastProgressWrite = DateTime.UtcNow;
            Persist();
        }
    }

    /// <inheritdoc />
    public MoveJob? Get(Guid id)
    {
        lock (_lock)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MoveJob> GetAll()
    {
        lock (_lock)
        {
            return _jobs.Values.OrderByDescending(j => j.QueuedAt).ToList();
        }
    }

    /// <inheritdoc />
    public MoveJob? TakeNextQueued()
    {
        lock (_lock)
        {
            var next = _jobs.Values
                .Where(j => j.Status == MoveStatus.Queued)
                .OrderBy(j => j.QueuedAt)
                .FirstOrDefault();

            if (next is null)
            {
                return null;
            }

            next.Status = MoveStatus.Preflight;
            next.StartedAt = DateTime.UtcNow;
            Persist();
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
    public bool IsDestinationClaimed(string path)
    {
        lock (_lock)
        {
            return _jobs.Values.Any(j =>
                j.IsActive && string.Equals(j.DestinationPath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            if (!_jobs.Remove(id))
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MoveJob> ReconcileInterrupted()
    {
        lock (_lock)
        {
            var stranded = _jobs.Values.Where(j => j.IsActive && j.Status != MoveStatus.Queued).ToList();

            foreach (var job in stranded)
            {
                // A move only ever writes to a staging file until the very last rename, so the
                // original is still where it was. Requeueing is safe, but it is the operator's
                // call: the destination drive may be the reason the server went down.
                job.Status = MoveStatus.Interrupted;
                job.Error = "The server stopped while this file was being moved. The original was left in place; "
                    + "check the destination folder for a leftover .momoving file before trying again.";
                job.FinishedAt = DateTime.UtcNow;
            }

            if (stranded.Count > 0)
            {
                Persist();
                _logger.LogInformation(
                    "[MediaOptimizer] {Count} move(s) were interrupted by a restart",
                    stranded.Count);
            }

            return stranded;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            foreach (var job in JsonSerializer.Deserialize<List<MoveJob>>(File.ReadAllText(_path), SerializerOptions) ?? [])
            {
                _jobs[job.Id] = job;
            }

            _logger.LogInformation("[MediaOptimizer] Move store loaded: {Count} job(s)", _jobs.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not read the move store at {Path}", _path);
        }
    }

    private void Persist()
    {
        try
        {
            var temp = _path + ".tmp";
            var json = JsonSerializer.Serialize(_jobs.Values.ToList(), SerializerOptions);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not write the move store at {Path}", _path);
        }
    }

    private void Trim()
    {
        var limit = Plugin.Instance?.Configuration.JobHistoryLimit ?? 200;
        if (limit <= 0)
        {
            return;
        }

        foreach (var job in _jobs.Values
                     .Where(j => !j.IsActive)
                     .OrderByDescending(j => j.FinishedAt ?? j.QueuedAt)
                     .Skip(limit)
                     .ToList())
        {
            _jobs.Remove(job.Id);
        }
    }
}
