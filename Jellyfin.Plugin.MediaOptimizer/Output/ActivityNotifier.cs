using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Output;

/// <summary>Reports finished conversions where a Jellyfin administrator will actually see them.</summary>
public interface IJobNotifier
{
    /// <summary>Records that a conversion finished.</summary>
    /// <param name="job">The finished job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task NotifyCompletedAsync(EncodeJob job, CancellationToken cancellationToken);

    /// <summary>Records that a conversion failed.</summary>
    /// <param name="job">The failed job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task NotifyFailedAsync(EncodeJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Writes to Jellyfin's own activity feed.
/// <para>
/// The plugin's dashboard page already shows every job, but nobody keeps it open. The activity
/// feed is where a Jellyfin administrator looks for "what has this server been doing", it is what
/// the notification plugins watch, and it survives the plugin's own history being trimmed — so a
/// conversion that replaced a file leaves a trace somewhere a person will find later.
/// </para>
/// </summary>
public class ActivityNotifier : IJobNotifier
{
    private readonly IActivityManager _activity;
    private readonly ILogger<ActivityNotifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="ActivityNotifier"/> class.</summary>
    /// <param name="activity">Jellyfin's activity manager.</param>
    /// <param name="logger">Logger.</param>
    public ActivityNotifier(IActivityManager activity, ILogger<ActivityNotifier> logger)
    {
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyCompletedAsync(EncodeJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!(Plugin.Instance?.Configuration.NotifyOnCompletion ?? true))
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            FormattableString.Invariant($"Optimised {job.ItemName}"),
            DescribeCompletion(job),
            "MediaOptimizerCompleted",
            LogLevel.Information,
            job);
    }

    /// <inheritdoc />
    public Task NotifyFailedAsync(EncodeJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!(Plugin.Instance?.Configuration.NotifyOnFailure ?? true))
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            FormattableString.Invariant($"Could not optimise {job.ItemName}"),
            DescribeFailure(job),
            "MediaOptimizerFailed",
            LogLevel.Error,
            job);
    }

    /// <summary>
    /// The one-line summary of a finished conversion. It leads with what was actually reclaimed,
    /// because that is the only number anyone reads the feed for.
    /// </summary>
    /// <param name="job">The finished job.</param>
    /// <returns>A sentence.</returns>
    internal static string DescribeCompletion(EncodeJob job)
    {
        var from = job.SourceSizeBytes;
        var to = job.OutputSizeBytes;

        if (from is not > 0 || to is not > 0)
        {
            return FormattableString.Invariant($"Finished. Output policy: {job.OutputPolicy}.");
        }

        var saved = from.Value - to.Value;
        var percent = saved * 100d / from.Value;

        // Only a policy that took the original away freed anything. A sidecar or an alternate
        // version keeps both files, so the disk went up by the size of the new one -- saying it
        // "freed 12 GiB" would be the opposite of what happened.
        var replaced = job.OutputPolicy is Configuration.OutputPolicy.Replace
            or Configuration.OutputPolicy.ReplaceAndDelete;

        string verdict;
        if (!replaced)
        {
            verdict = string.Format(
                CultureInfo.InvariantCulture,
                "{0} → {1}, written alongside the original, so this uses {2} more disk.",
                Size(from.Value),
                Size(to.Value),
                Size(to.Value));
        }
        else if (saved >= 0)
        {
            verdict = string.Format(
                CultureInfo.InvariantCulture,
                "{0} → {1}, freeing {2} ({3:F0}%).",
                Size(from.Value),
                Size(to.Value),
                Size(saved),
                percent);
        }
        else
        {
            verdict = string.Format(
                CultureInfo.InvariantCulture,
                "{0} → {1}, which is {2} larger.",
                Size(from.Value),
                Size(to.Value),
                Size(-saved));
        }

        if (job.LosslessVerified == true)
        {
            verdict += " Bit-exactness verified by hash.";
        }
        else if (job.QualityMetric is not null && job.QualityScore is { } score)
        {
            // What it came out looking like, measured against the original after the fact. The
            // feed entry is where somebody finds this weeks later, when the working files are
            // long gone and the only remaining question is whether the conversion was worth it.
            verdict += FormattableString.Invariant(
                $" Picture quality {job.QualityMetric} {Core.QualityProbe.FormatScore(job.QualityMetric, score)} at its worst against the original: {Core.QualityProbe.Describe(job.QualityMetric, score)}.");
        }

        if (job.QuarantinePath is not null)
        {
            verdict += FormattableString.Invariant(
                $" The original is kept until {job.QuarantineExpiresAt:yyyy-MM-dd} and can be restored from the Media Optimizer dashboard.");
        }

        return verdict;
    }

    /// <summary>The one-line summary of a failure, cut to something a feed entry can hold.</summary>
    /// <param name="job">The failed job.</param>
    /// <returns>A sentence.</returns>
    internal static string DescribeFailure(EncodeJob job)
    {
        var error = (job.Error ?? "No reason was recorded.").Trim();

        // The feed shows one line. The full output stays on the job in the dashboard.
        var firstLine = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var summary = firstLine.Length > 0 ? firstLine[0] : error;

        if (summary.Length > 240)
        {
            summary = summary[..240] + "…";
        }

        return summary + " The original file was not modified.";
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:F1} GiB", bytes / 1024d / 1024d / 1024d),
        >= 1024L * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:F0} MiB", bytes / 1024d / 1024d),
        _ => string.Format(CultureInfo.InvariantCulture, "{0} bytes", bytes)
    };

    private async Task WriteAsync(string name, string overview, string type, LogLevel level, EncodeJob job)
    {
        try
        {
            await _activity.CreateAsync(new ActivityLog(name, type, Guid.Empty)
            {
                Overview = overview,
                ShortOverview = overview.Length > 120 ? overview[..120] + "…" : overview,
                ItemId = job.ItemId.ToString("N", CultureInfo.InvariantCulture),
                LogSeverity = level
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A conversion that worked must not be reported as failed because a log line could not be written.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not write an activity entry for job {JobId}", job.Id);
        }
    }
}
