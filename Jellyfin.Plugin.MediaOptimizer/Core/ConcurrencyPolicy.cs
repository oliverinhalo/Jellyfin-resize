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
