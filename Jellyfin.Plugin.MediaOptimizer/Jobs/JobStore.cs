using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.MediaOptimizer.Core;
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

    /// <summary>Takes the next queued job that the caller is willing to start, marking it as claimed.</summary>
    /// <param name="canStart">
    /// Whether a given job may start now, or null to take the first one regardless. A job the
    /// predicate turns down is left queued and the next is offered, so a small job can run while
    /// a large one waits for room.
    /// </param>
    /// <returns>The next job, or null when the queue is empty, paused, or nothing fits.</returns>
    EncodeJob? TakeNextQueued(Predicate<EncodeJob>? canStart = null);

    /// <summary>Returns true when an item already has a queued or running job.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when a job is already active for the item.</returns>
    bool HasActiveJobForItem(Guid itemId);

    /// <summary>
    /// Records that a job should stop. A job that has not been claimed yet is cancelled outright;
    /// one already being worked on is flagged, for the worker to act on.
    /// </summary>
    /// <param name="id">Job id.</param>
    /// <returns>True when a job was found that could still be stopped.</returns>
    bool RequestCancel(Guid id);

    /// <summary>Removes a job from the store entirely.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>True when a job was removed.</returns>
    bool Remove(Guid id);

    /// <summary>
    /// Recovers jobs that were mid-flight when the server stopped. Called once at startup.
    /// </summary>
    /// <returns>The jobs that were recovered, with their new status already applied.</returns>
    IReadOnlyList<EncodeJob> ReconcileInterrupted();

    /// <summary>
    /// Gets or sets a value indicating whether the worker may claim new jobs. Persisted, so a
    /// queue an administrator paused is still paused after a restart rather than quietly
    /// encoding overnight.
    /// </summary>
    bool IsPaused { get; set; }
}

/// <summary>
/// A crash-safe job store: every change is appended to a journal and flushed to disk before the
/// call returns, and the journal is periodically folded into a snapshot.
/// <para>
/// This is deliberately not SQLite. Jellyfin loads a SQLite provider for its own database, but it
/// is not part of the plugin dependency graph, so binding to it would risk the whole plugin
/// failing to load on any server whose version differed. A journal gives the same guarantee that
/// matters here — nothing acknowledged is ever lost, and a half-written record is discarded on
/// read — without adding a dependency that could take the plugin down with it.
/// </para>
/// </summary>
public class JobStore : IJobStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Fold the journal into a snapshot once it passes this many records.</summary>
    private const int CompactionThreshold = 400;

    private readonly string _directory;
    private readonly string _snapshotPath;
    private readonly string _journalPath;
    private readonly string _pausedPath;
    private readonly ILogger<JobStore> _logger;
    private readonly Lock _lock = new Lock();
    private readonly Dictionary<Guid, EncodeJob> _jobs = new Dictionary<Guid, EncodeJob>();

    private int _journalRecords;
    private bool _paused;

    /// <summary>Initializes a new instance of the <see cref="JobStore"/> class.</summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public JobStore(IApplicationPaths appPaths, ILogger<JobStore> logger)
        : this(Path.Combine(appPaths.DataPath, "mediaoptimizer"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JobStore"/> class in a given directory.</summary>
    /// <param name="directory">Where the snapshot and journal live.</param>
    /// <param name="logger">Logger.</param>
    internal JobStore(string directory, ILogger<JobStore> logger)
    {
        _logger = logger;
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _snapshotPath = Path.Combine(_directory, "queue.snapshot.json");
        _journalPath = Path.Combine(_directory, "queue.journal.jsonl");
        _pausedPath = Path.Combine(_directory, "queue.paused");

        _paused = File.Exists(_pausedPath);

        Load();
    }

    /// <inheritdoc />
    public bool IsPaused
    {
        get => _paused;

        set
        {
            lock (_lock)
            {
                _paused = value;
                try
                {
                    // A marker file rather than a plugin setting: the queue state is operational
                    // rather than configuration, and this way pausing cannot rewrite -- or be lost
                    // by -- a configuration save happening at the same moment.
                    if (value)
                    {
                        File.WriteAllText(_pausedPath, DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
                    }
                    else if (File.Exists(_pausedPath))
                    {
                        File.Delete(_pausedPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "[MediaOptimizer] Could not record the paused state; it will not survive a restart");
                }
            }
        }
    }

    /// <inheritdoc />
    public void Add(EncodeJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;
            AppendJournal("put", job);
            Trim();
        }
    }

    /// <inheritdoc />
    public void Update(EncodeJob job)
    {
        lock (_lock)
        {
            _jobs[job.Id] = job;
            AppendJournal("put", job);
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
            return _jobs.Values.Where(j => j.IsActive).OrderBy(j => j.Priority).ThenBy(j => j.QueuedAt).ToList();
        }
    }

    /// <inheritdoc />
    public EncodeJob? TakeNextQueued(Predicate<EncodeJob>? canStart = null)
    {
        if (_paused)
        {
            return null;
        }

        lock (_lock)
        {
            // Lower Priority numbers run first; ties fall back to arrival order.
            var ordered = _jobs.Values
                .Where(j => j.Status == JobStatus.Queued)
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.QueuedAt)
                .ToList();

            // Which of those can start now is the caller's business, and the rules about passing
            // one over for another live in ConcurrencyPolicy, where they can be tested on their
            // own: a job may only be overtaken by one at least as urgent, and not indefinitely.
            var next = canStart is null
                ? ordered.FirstOrDefault()
                : ConcurrencyPolicy.Choose(
                    ordered,
                    job => canStart(job),
                    job => job.Priority,
                    job => job.QueuedAt,
                    DateTime.UtcNow);

            if (next is null)
            {
                return null;
            }

            next.Status = JobStatus.Preflight;
            next.StartedAt = DateTime.UtcNow;
            AppendJournal("put", next);
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
    public bool RequestCancel(Guid id)
    {
        lock (_lock)
        {
            var job = _jobs.GetValueOrDefault(id);
            if (job is null || !job.IsActive)
            {
                return false;
            }

            job.CancellationRequested = true;

            // Nothing has picked it up, so it can simply be finished here. Anything further along
            // is the worker's to stop: it may have an ffmpeg process running and a temporary file
            // to remove, and only it knows that.
            if (job.Status == JobStatus.Queued)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTime.UtcNow;
                job.Error = "Cancelled before it started. The original file was not modified.";
            }

            AppendJournal("put", job);
            return true;
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

            AppendJournal("del", new EncodeJob { Id = id });
            return true;
        }
    }

    /// <summary>
    /// How many times a job is started again after the server stopped mid-encode before it is
    /// held for a person to look at. Three is generous for bad luck — a power cut, a container
    /// restart, an upgrade — and short of the point where the job is plainly the cause.
    /// </summary>
    internal const int MaxAutomaticResumes = 3;

    /// <inheritdoc />
    public IReadOnlyList<EncodeJob> ReconcileInterrupted()
    {
        lock (_lock)
        {
            var resume = Plugin.Instance?.Configuration.ResumeJobsAfterRestart ?? true;
            var stranded = _jobs.Values
                .Where(j => j.Status is JobStatus.Preflight or JobStatus.Encoding or JobStatus.Verifying or JobStatus.Applying)
                .ToList();

            foreach (var job in stranded)
            {
                if (job.Status == JobStatus.Applying)
                {
                    // The only phase where the original may already have moved. A human needs to
                    // look before anything else touches those files.
                    job.Status = JobStatus.Interrupted;
                    job.Error = "The server stopped while the finished file was being moved into place. "
                        + "Check the file and the quarantine folder before requeueing.";
                    job.FinishedAt = DateTime.UtcNow;
                }
                else if (resume && job.ResumeCount >= MaxAutomaticResumes)
                {
                    // Resuming is right up to the point where this job is what stopped the
                    // server. Something about this file or these settings is taking the machine
                    // down, and requeueing it on every boot makes the plugin the cause of a
                    // reboot loop rather than the victim of one. A person can still retry it.
                    job.Status = JobStatus.Interrupted;
                    job.Error = FormattableString.Invariant(
                        $"This job has been interrupted {job.ResumeCount} times, so it is being held rather than started again. Something about this file or these settings is stopping the server mid-encode: try a different codec or preset, or switch off hardware encoding, before running it again. The original file was not modified.");
                    job.FinishedAt = DateTime.UtcNow;
                }
                else if (resume)
                {
                    // Encoding writes only to a temp file, so restarting the job is always safe.
                    job.Status = JobStatus.Queued;
                    job.ProgressPercent = 0;
                    job.Speed = null;
                    job.EtaSeconds = null;
                    job.StartedAt = null;
                    job.ResumeCount++;
                    job.Error = null;
                    job.CancellationRequested = false;
                }
                else
                {
                    job.Status = JobStatus.Interrupted;
                    job.Error = "The server stopped while this job was running. The original file was not modified.";
                    job.FinishedAt = DateTime.UtcNow;
                }

                AppendJournal("put", job);
            }

            if (stranded.Count > 0)
            {
                var resumed = stranded.Count(j => j.Status == JobStatus.Queued);
                _logger.LogInformation(
                    "[MediaOptimizer] Recovered {Count} interrupted job(s) after restart: {Resumed} requeued, {Held} held for review",
                    stranded.Count,
                    resumed,
                    stranded.Count - resumed);
            }

            return stranded;
        }
    }

    /// <summary>Rebuilds in-memory state from a snapshot plus the journal written after it.</summary>
    /// <param name="snapshotJson">Snapshot contents, or null when there is none.</param>
    /// <param name="journalLines">Journal lines in the order they were written.</param>
    /// <returns>The recovered jobs, keyed by id.</returns>
    internal static Dictionary<Guid, EncodeJob> Replay(string? snapshotJson, IEnumerable<string> journalLines)
    {
        var jobs = new Dictionary<Guid, EncodeJob>();

        if (!string.IsNullOrWhiteSpace(snapshotJson))
        {
            try
            {
                foreach (var job in JsonSerializer.Deserialize<List<EncodeJob>>(snapshotJson, SerializerOptions) ?? [])
                {
                    jobs[job.Id] = job;
                }
            }
            catch (JsonException)
            {
                // A corrupt snapshot is not fatal: the journal alone can rebuild recent state.
            }
        }

        foreach (var line in journalLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                // A torn final line is exactly what a crash mid-append looks like. Everything
                // before it is intact, so stop here rather than discarding the whole journal.
                break;
            }

            if (record?.Job is null)
            {
                continue;
            }

            if (string.Equals(record.Op, "del", StringComparison.Ordinal))
            {
                jobs.Remove(record.Job.Id);
            }
            else
            {
                jobs[record.Job.Id] = record.Job;
            }
        }

        return jobs;
    }

    private void Load()
    {
        try
        {
            var snapshot = File.Exists(_snapshotPath) ? File.ReadAllText(_snapshotPath) : null;
            var journal = File.Exists(_journalPath) ? File.ReadAllLines(_journalPath) : Array.Empty<string>();

            foreach (var pair in Replay(snapshot, journal))
            {
                _jobs[pair.Key] = pair.Value;
            }

            _journalRecords = journal.Length;

            // Older builds wrote a single jobs.json; fold it in once so nothing is lost on upgrade.
            var legacy = Path.Combine(_directory, "jobs.json");
            if (_jobs.Count == 0 && File.Exists(legacy))
            {
                foreach (var job in JsonSerializer.Deserialize<List<EncodeJob>>(File.ReadAllText(legacy), SerializerOptions) ?? [])
                {
                    _jobs[job.Id] = job;
                }

                Compact();
                File.Move(legacy, legacy + ".migrated", overwrite: true);
                _logger.LogInformation("[MediaOptimizer] Migrated {Count} job(s) from the previous store", _jobs.Count);
            }

            _logger.LogInformation(
                "[MediaOptimizer] Job store loaded: {Count} job(s), {Records} journal record(s)",
                _jobs.Count,
                _journalRecords);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not read the job store in {Directory}", _directory);
        }
    }

    /// <summary>Appends one record and flushes it to disk before returning.</summary>
    /// <param name="op">"put" or "del".</param>
    /// <param name="job">The job the record concerns.</param>
    private void AppendJournal(string op, EncodeJob job)
    {
        try
        {
            var line = JsonSerializer.Serialize(new JournalRecord { Op = op, Job = job }, SerializerOptions);

            using (var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(line);
                writer.Flush();
                // Without this the record lives only in the OS page cache, which is precisely
                // what a power cut takes with it.
                stream.Flush(flushToDisk: true);
            }

            _journalRecords++;

            if (_journalRecords >= CompactionThreshold)
            {
                Compact();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not append to the job journal");
        }
    }

    /// <summary>Writes a fresh snapshot and truncates the journal.</summary>
    private void Compact()
    {
        try
        {
            var temp = _snapshotPath + ".tmp";
            var json = JsonSerializer.Serialize(_jobs.Values.ToList(), SerializerOptions);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Rename is atomic, so a reader sees either the old snapshot or the new one, never half.
            File.Move(temp, _snapshotPath, overwrite: true);
            File.WriteAllText(_journalPath, string.Empty);
            _journalRecords = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not compact the job store");
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
            AppendJournal("del", new EncodeJob { Id = job.Id });
        }
    }

    /// <summary>One line of the write-ahead journal.</summary>
    internal sealed class JournalRecord
    {
        /// <summary>Gets or sets the operation: "put" or "del".</summary>
        [JsonPropertyName("op")]
        public string Op { get; set; } = "put";

        /// <summary>Gets or sets the job the record concerns.</summary>
        [JsonPropertyName("job")]
        public EncodeJob? Job { get; set; }
    }
}
