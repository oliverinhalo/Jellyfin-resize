using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Where a long-running request has got to.</summary>
public enum OperationStatus
{
    /// <summary>Still working.</summary>
    Running = 0,

    /// <summary>Finished, with a result.</summary>
    Completed = 1,

    /// <summary>Finished, with a reason instead.</summary>
    Failed = 2,

    /// <summary>Stopped because somebody asked it to.</summary>
    Cancelled = 3
}

/// <summary>One piece of work that outlives the request that started it.</summary>
public class OperationState
{
    /// <summary>Gets or sets the operation id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets what kind of work it is, for the log and the dashboard.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets where it has got to.</summary>
    public OperationStatus Status { get; set; }

    /// <summary>Gets or sets what it produced, once it has.</summary>
    public object? Result { get; set; }

    /// <summary>Gets or sets why it did not finish.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets when it started.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when it stopped.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets how long it has been going, in seconds.</summary>
    public double ElapsedSeconds => ((FinishedAt ?? DateTime.UtcNow) - StartedAt).TotalSeconds;
}

/// <summary>Runs work that takes minutes, so that nothing has to hold an HTTP request open for it.</summary>
public interface IOperationRegistry
{
    /// <summary>Starts a piece of work and returns immediately.</summary>
    /// <param name="kind">What kind of work it is.</param>
    /// <param name="work">The work, which must respect its cancellation token.</param>
    /// <returns>The id to ask about it with.</returns>
    Guid Start(string kind, Func<CancellationToken, Task<object>> work);

    /// <summary>Looks an operation up.</summary>
    /// <param name="id">The operation id.</param>
    /// <returns>Its state, or null when there is no such operation.</returns>
    OperationState? Get(Guid id);

    /// <summary>Asks an operation to stop.</summary>
    /// <param name="id">The operation id.</param>
    /// <returns>Whether there was a running operation to stop.</returns>
    bool Cancel(Guid id);
}

/// <summary>
/// Keeps long jobs out of the request that asked for them.
/// <para>
/// Measuring a conversion takes a minute and searching for a quality setting takes several, and a
/// request held open that long does not survive the trip: the default read timeout in nearly every
/// reverse proxy in front of a Jellyfin server is sixty seconds, and a dropped connection there
/// looks exactly like a broken feature while the server carries on encoding for another four
/// minutes with nobody left to tell. So the request starts the work and answers with an id, and
/// the browser asks how it is going — the same shape as the conversion queue, for the same reason.
/// </para>
/// <para>
/// Nothing here is persisted. An operation is an answer somebody is waiting for right now, and a
/// server that restarts has stopped the ffmpeg it was waiting on too, so remembering the question
/// would only produce an answer that never arrives.
/// </para>
/// </summary>
public class OperationRegistry : IOperationRegistry, IDisposable
{
    /// <summary>How long a finished operation stays readable before it is forgotten.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The most operations kept at once. Long past this, something is starting them in a loop, and
    /// a registry that grows without limit is a memory leak with a polite name.
    /// </summary>
    internal const int MaxOperations = 50;

    /// <summary>
    /// The longest any one operation may run.
    /// <para>
    /// It exists because the work now outlives the request that asked for it. A browser tab closed
    /// mid-search sends nothing — the dialog's own cancel never happens — and before this work was
    /// moved off the request, a dropped connection was what stopped the encoding. An hour is long
    /// enough for the slowest server to finish a quality search on a long film, and short enough
    /// that a forgotten one cannot hold an ffmpeg for ever.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ILogger<OperationRegistry> _logger;
    private readonly TimeSpan _limit;

    /// <summary>Initializes a new instance of the <see cref="OperationRegistry"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public OperationRegistry(ILogger<OperationRegistry> logger)
        : this(logger, TimeLimit)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationRegistry"/> class with an explicit
    /// time limit, so that the limit itself can be tested without waiting an hour.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="limit">The longest any one operation may run.</param>
    internal OperationRegistry(ILogger<OperationRegistry> logger, TimeSpan limit)
    {
        _logger = logger;
        _limit = limit;
    }

    /// <inheritdoc />
    public Guid Start(string kind, Func<CancellationToken, Task<object>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Prune();

        var entry = new Entry
        {
            State = new OperationState { Id = Guid.NewGuid(), Kind = kind, Status = OperationStatus.Running },
            Cancellation = new CancellationTokenSource(_limit)
        };

        var id = entry.State.Id;
        _entries[id] = entry;

        // Deliberately not awaited: the point is that the request returns now. Everything the work
        // can throw is caught, because an unobserved exception on a background task is a process
        // that dies for a reason nobody can see.
        _ = Task.Run(async () =>
        {
            var finished = new OperationState
            {
                Id = id,
                Kind = kind,
                StartedAt = entry.State.StartedAt,
                FinishedAt = DateTime.UtcNow
            };

            try
            {
                finished.Result = await work(entry.Cancellation.Token).ConfigureAwait(false);
                finished.Status = OperationStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                finished.Status = OperationStatus.Cancelled;

                // Somebody closing the dialog and an operation running out of time both arrive
                // here, and they are not the same thing to whoever reads it. Which one it was is
                // recorded rather than inferred from the clock: the deadline's own timer can fire
                // a hair before the wall-clock time it was set for.
                var expired = !entry.StoppedDeliberately;
                finished.Error = expired
                    ? FormattableString.Invariant(
                        $"Stopped after {_limit.TotalMinutes:F0} minutes without finishing.")
                    : "Stopped.";

                if (expired)
                {
                    _logger.LogWarning(
                        "[MediaOptimizer] {Kind} ran out of time after {Minutes} minutes",
                        kind,
                        _limit.TotalMinutes);
                }
            }
#pragma warning disable CA1031 // A failure here is an answer to report, never an unhandled crash.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "[MediaOptimizer] {Kind} failed", kind);
                finished.Status = OperationStatus.Failed;
                finished.Error = ex.Message;
            }

            // Published in one go, and only once it is complete. Filling in a shared object field
            // by field would let a poll arriving in the middle of that see "completed" with no
            // result attached -- a measurement that took a minute, reported as nothing.
            finished.FinishedAt = DateTime.UtcNow;
            entry.Publish(finished);
            entry.Cancellation.Dispose();
        });

        return id;
    }

    /// <inheritdoc />
    public OperationState? Get(Guid id) => _entries.TryGetValue(id, out var entry) ? entry.State : null;

    /// <inheritdoc />
    public bool Cancel(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.State.Status != OperationStatus.Running)
        {
            return false;
        }

        try
        {
            entry.StoppedDeliberately = true;
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the lookup and the cancel, which is not a failure to cancel.
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            try
            {
                entry.StoppedDeliberately = true;
                entry.Cancellation.Cancel();
                entry.Cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        _entries.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Forgets operations nobody can still be waiting for: finished and older than the retention
    /// window, and — if something has gone badly wrong and they are piling up — the oldest
    /// finished ones regardless.
    /// </summary>
    private void Prune()
    {
        var now = DateTime.UtcNow;

        foreach (var pair in _entries)
        {
            var finished = pair.Value.State.FinishedAt;
            if (finished is not null && now - finished.Value > Retention)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        if (_entries.Count < MaxOperations)
        {
            return;
        }

        var surplus = _entries
            .Where(p => p.Value.State.FinishedAt is not null)
            .OrderBy(p => p.Value.State.FinishedAt)
            .Take(Math.Max(1, _entries.Count - MaxOperations + 1));

        foreach (var pair in surplus)
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>
    /// One operation and the handle that stops it. The state is swapped wholesale rather than
    /// edited, so a reader sees either the running operation or the finished one.
    /// </summary>
    private sealed class Entry
    {
        private OperationState _state = new OperationState();

        public OperationState State
        {
            get => Volatile.Read(ref _state);
            init => _state = value;
        }

        public CancellationTokenSource Cancellation { get; init; } = new CancellationTokenSource();

        /// <summary>Whether something asked this to stop, as opposed to it running out of time.</summary>
        public bool StoppedDeliberately { get; set; }

        public void Publish(OperationState next) => Volatile.Write(ref _state, next);
    }
}
