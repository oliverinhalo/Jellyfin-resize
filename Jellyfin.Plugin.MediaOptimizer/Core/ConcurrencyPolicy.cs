using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>
/// Decides how many conversions may run at once, by weight rather than by count.
/// <para>
/// A flat count is the wrong unit. Two 4K encodes are not two jobs, they are a server that has
/// stopped responding: at the same preset a 4K encode costs roughly four times the CPU of a 1080p
/// one, and the memory to match. An administrator who sets "2" means "use about twice one job's
/// worth of this machine", so that is what it is taken to mean — two 1080p jobs, or one 4K job.
/// </para>
/// <para>
/// The one inviolable rule is that something must always be able to start. A weighting that let a
/// 4K job be permanently too expensive would be a queue that silently stops.
/// </para>
/// </summary>
public static class ConcurrencyPolicy
{
    /// <summary>Anything at or above this height is treated as the expensive kind.</summary>
    private const int LargeHeight = 1440;

    /// <summary>What one job costs, in units of a 1080p encode.</summary>
    /// <param name="sourceHeight">The source height, or null when it is not known.</param>
    /// <returns>The cost.</returns>
    public static int CostOf(int? sourceHeight)
    {
        // An unknown height is treated as the expensive case. Guessing "cheap" and being wrong
        // costs the server; guessing "expensive" and being wrong costs a little throughput.
        if (sourceHeight is null)
        {
            return 2;
        }

        return sourceHeight >= LargeHeight ? 2 : 1;
    }

    /// <summary>
    /// How long a job at the front of the queue may be passed over before the queue starts holding
    /// room for it instead. Backfilling is worth having — a 720p episode should use the space a 4K
    /// film cannot — but only while it does not turn into "the big job never runs".
    /// </summary>
    public static readonly TimeSpan HeadOfQueueGrace = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Chooses which queued job to start, given what is already running.
    /// <para>
    /// Taking the first job that fits is the obvious rule and it silently inverts priority: a 4K
    /// job marked "run this next" costs two slots, so with two ordinary jobs arriving steadily it
    /// is passed over every single time. So: a job may only be passed over by one that is at least
    /// as urgent, and once it has been waiting longer than the grace period the queue holds room
    /// for it rather than filling the space again.
    /// </para>
    /// </summary>
    /// <param name="queued">The queued jobs, already in the order they should run.</param>
    /// <param name="fits">Whether a given job can start right now.</param>
    /// <param name="priorityOf">The job's priority; lower numbers run first.</param>
    /// <param name="queuedAtOf">When the job joined the queue.</param>
    /// <param name="now">The current time.</param>
    /// <typeparam name="T">The job type.</typeparam>
    /// <returns>The job to start, or null to wait.</returns>
    public static T? Choose<T>(
        IReadOnlyList<T> queued,
        Func<T, bool> fits,
        Func<T, int> priorityOf,
        Func<T, DateTime> queuedAtOf,
        DateTime now)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(queued);
        ArgumentNullException.ThrowIfNull(fits);
        ArgumentNullException.ThrowIfNull(priorityOf);
        ArgumentNullException.ThrowIfNull(queuedAtOf);

        if (queued.Count == 0)
        {
            return null;
        }

        var head = queued[0];
        if (fits(head))
        {
            return head;
        }

        // The job at the front has waited long enough. Stop filling the space in front of it.
        if (now - queuedAtOf(head) > HeadOfQueueGrace)
        {
            return null;
        }

        var headPriority = priorityOf(head);

        foreach (var candidate in queued)
        {
            // Only a job that is at least as urgent may go first. Anything less urgent jumping the
            // queue is the inversion this exists to prevent.
            if (priorityOf(candidate) <= headPriority && fits(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether another job may start alongside the ones already running.</summary>
    /// <param name="runningHeights">The source heights of the jobs currently running.</param>
    /// <param name="candidateHeight">The source height of the job being considered.</param>
    /// <param name="maxConcurrentJobs">The configured limit, in 1080p-equivalents.</param>
    /// <returns>Whether it may start now.</returns>
    public static bool CanStart(
        IReadOnlyList<int?> runningHeights,
        int? candidateHeight,
        int maxConcurrentJobs)
    {
        ArgumentNullException.ThrowIfNull(runningHeights);

        // An idle server always starts the next job, whatever it weighs. Anything else would be a
        // queue that refuses to run the very files it exists for.
        if (runningHeights.Count == 0)
        {
            return true;
        }

        var capacity = Math.Max(1, maxConcurrentJobs);
        var used = 0;
        foreach (var height in runningHeights)
        {
            used += CostOf(height);
        }

        return used + CostOf(candidateHeight) <= capacity;
    }
}
