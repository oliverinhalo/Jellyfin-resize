using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Output;

/// <summary>What the finished file measured against the original it came from.</summary>
public class OutputQuality
{
    /// <summary>Gets or sets which metric produced the scores: VMAF or SSIM.</summary>
    public string? Metric { get; set; }

    /// <summary>Gets or sets the worst score any of the compared stretches produced.</summary>
    public double? WorstScore { get; set; }

    /// <summary>Gets or sets the average across the compared stretches.</summary>
    public double? MeanScore { get; set; }

    /// <summary>Gets or sets how many stretches were compared.</summary>
    public int Samples { get; set; }

    /// <summary>Gets or sets why no measurement was made, when none was.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the conversion is known to be indistinguishable
    /// for a reason that makes measuring it pointless — the video was copied rather than
    /// re-encoded, or the whole conversion was verified bit-exact by hash.
    /// </summary>
    public bool IdenticalByConstruction { get; set; }

    /// <summary>Gets a value indicating whether a score was obtained.</summary>
    public bool Succeeded => FailureReason is null && Samples > 0 && WorstScore is not null;
}

/// <summary>Measures what a finished conversion actually looks like.</summary>
public interface IOutputQualityService
{
    /// <summary>Compares stretches of the finished file against the same moments of the original.</summary>
    /// <param name="analysis">The source, for its path, duration and video dimensions.</param>
    /// <param name="outputPath">The finished file, before anything has been done with it.</param>
    /// <param name="metric">The metric this FFmpeg build can produce — "VMAF" or "SSIM".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measurement, or a reason there is none.</returns>
    Task<OutputQuality> MeasureAsync(
        FileAnalysis analysis,
        string outputPath,
        string metric,
        CancellationToken cancellationToken);
}

/// <summary>
/// The other half of the promise.
/// <para>
/// Everything else in this plugin measures a conversion <em>before</em> it happens: the sampled
/// estimate, and the search that picks a quality setting by encoding short stretches and comparing
/// them. Both are honest about being samples — three eight-second windows cannot know about the
/// twenty minutes of dark, grainy footage at the end of the film. So the setting that was chosen
/// against three windows then gets applied to the whole film, and until now nothing ever went back
/// and checked what came out.
/// </para>
/// <para>
/// This does, on the finished file, in the moment when it still costs nothing to change one's
/// mind: the original is untouched, the output is a working file, and a conversion that came out
/// worse than asked for can simply be refused. The comparison is sampled too — a frame-by-frame
/// pass over a two-hour film would cost as much as the encode — but it is sampled from the real
/// output rather than from a stand-in for it, which is the difference between a prediction and a
/// result.
/// </para>
/// </summary>
public class OutputQualityService : IOutputQualityService
{
    /// <summary>Where to compare, as fractions of the duration.</summary>
    private static readonly double[] Points = [0.15d, 0.5d, 0.85d];

    /// <summary>How long each compared stretch is.</summary>
    internal const double SegmentSeconds = 6d;

    /// <summary>Below this the file is compared in one go instead, because it is cheap enough.</summary>
    internal const double ShortFileSeconds = 45d;

    /// <summary>The most of a short file to compare in one go.</summary>
    internal const double ShortFileLimitSeconds = 20d;

    private readonly IQualityProbe _probe;
    private readonly ILogger<OutputQualityService> _logger;

    /// <summary>Initializes a new instance of the <see cref="OutputQualityService"/> class.</summary>
    /// <param name="probe">The quality probe.</param>
    /// <param name="logger">Logger.</param>
    public OutputQualityService(IQualityProbe probe, ILogger<OutputQualityService> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// The score a conversion has to reach to clear a floor, or null when the floor is off.
    /// </summary>
    /// <param name="metric">Which metric will be measuring.</param>
    /// <param name="floor">The floor the user set.</param>
    /// <returns>The score, or null.</returns>
    internal static double? ThresholdFor(string metric, QualityFloor floor) => floor switch
    {
        QualityFloor.Off => null,
        QualityFloor.NoticeablySofter => QualityProbe.FloorFor(metric, QualityVerdict.NoticeablySofter),
        QualityFloor.SlightlySofter => QualityProbe.FloorFor(metric, QualityVerdict.SlightlySofter),
        _ => QualityProbe.FloorFor(metric, QualityVerdict.VeryClose)
    };

    /// <summary>The verdict a floor is asking for, in the same words a result is described in.</summary>
    /// <param name="floor">The floor.</param>
    /// <returns>The verdict.</returns>
    internal static QualityVerdict VerdictFor(QualityFloor floor) => floor switch
    {
        QualityFloor.NoticeablySofter => QualityVerdict.NoticeablySofter,
        QualityFloor.SlightlySofter => QualityVerdict.SlightlySofter,
        _ => QualityVerdict.VeryClose
    };

    /// <summary>
    /// One sentence describing what was measured, for the job, the dashboard and the activity feed.
    /// </summary>
    /// <param name="quality">The measurement.</param>
    /// <returns>The sentence, or null when there is nothing to say.</returns>
    public static string? Summarise(OutputQuality? quality)
    {
        if (quality is null)
        {
            return null;
        }

        if (quality.IdenticalByConstruction)
        {
            return quality.FailureReason;
        }

        if (!quality.Succeeded)
        {
            return quality.FailureReason;
        }

        var metric = quality.Metric!;
        var worst = quality.WorstScore!.Value;

        // The worst of the compared stretches leads, because it is the one that decides whether
        // the conversion was good enough — an average hides exactly the scene that went wrong.
        var text = FormattableString.Invariant(
            $"measured {metric} {QualityProbe.FormatScore(metric, worst)} at its worst across {quality.Samples} point(s) of the finished file: {QualityProbe.Describe(metric, worst)}");

        if (quality.MeanScore is { } mean && quality.Samples > 1)
        {
            text += FormattableString.Invariant(
                $" (average {QualityProbe.FormatScore(metric, mean)})");
        }

        return text;
    }

    /// <summary>
    /// Whether a measured conversion has to be refused, and what to say about it.
    /// <para>
    /// A floor that cannot be checked refuses the conversion, which is the uncomfortable half of
    /// this decision and the right one: the promise the setting makes is that nothing worse than
    /// X replaces an original, and "we could not tell" does not keep it. It fails safe — the
    /// original is untouched and the job can be retried — the message names the fix, and the floor
    /// is off unless somebody turns it on.
    /// </para>
    /// </summary>
    /// <param name="quality">What was measured, if anything.</param>
    /// <param name="floor">The floor the user set.</param>
    /// <returns>A refusal message, or null when the conversion may proceed.</returns>
    public static string? Refuse(OutputQuality? quality, QualityFloor floor)
    {
        if (floor == QualityFloor.Off)
        {
            return null;
        }

        // A copied video stream is the same pictures, and a hash-verified lossless conversion is
        // the same file: both clear every floor there is without measuring anything.
        if (quality is { IdenticalByConstruction: true })
        {
            return null;
        }

        var wanted = VerdictFor(floor);

        if (quality is null || !quality.Succeeded)
        {
            return "You asked for conversions that measure no worse than \""
                + QualityProbe.Describe(wanted)
                + "\" to replace an original, and this one could not be measured: "
                + (quality?.FailureReason ?? "no comparison was made")
                + ". The original has not been touched. Either install an FFmpeg build that can "
                + "compare video (any build with the SSIM filter, which is almost all of them), or "
                + "turn the quality floor off under Media Optimizer settings.";
        }

        var metric = quality.Metric!;
        var worst = quality.WorstScore!.Value;
        var threshold = ThresholdFor(metric, floor)!.Value;

        if (worst >= threshold)
        {
            return null;
        }

        return FormattableString.Invariant(
            $"The conversion came out worse than you allow: {metric} {QualityProbe.FormatScore(metric, worst)} at its worst ({QualityProbe.Describe(metric, worst)}), where the floor you set needs {QualityProbe.FormatScore(metric, threshold)} (\"{QualityProbe.Describe(wanted)}\"). The original has not been touched. A lower quality number — or one of the sharper presets — will get there; \"Find the setting\" in the conversion dialog will work out which.");
    }

    /// <summary>Which moments of a file to compare, given how long it is.</summary>
    /// <param name="durationSeconds">The source duration.</param>
    /// <returns>Start offset and length for each comparison.</returns>
    internal static IReadOnlyList<(double Start, double Seconds)> SegmentsFor(double durationSeconds)
    {
        if (durationSeconds <= 0d)
        {
            return [];
        }

        // A short file is cheaper to compare from the beginning than to sample: one pass over
        // twenty seconds beats three seeks into a two-minute clip, and it covers more of it.
        if (durationSeconds < ShortFileSeconds)
        {
            return [(0d, Math.Min(durationSeconds, ShortFileLimitSeconds))];
        }

        var segments = new List<(double Start, double Seconds)>(Points.Length);
        foreach (var point in Points)
        {
            var start = durationSeconds * point;

            // Never run off the end: a comparison whose reference window is shorter than its
            // encoded one is the case that used to hang the filter graph.
            if (start + SegmentSeconds > durationSeconds)
            {
                start = Math.Max(0d, durationSeconds - SegmentSeconds);
            }

            segments.Add((start, SegmentSeconds));
        }

        return segments;
    }

    /// <inheritdoc />
    public async Task<OutputQuality> MeasureAsync(
        FileAnalysis analysis,
        string outputPath,
        string metric,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var quality = new OutputQuality { Metric = metric };

        if (analysis.Video is null)
        {
            quality.FailureReason = "This file has no video to compare.";
            return quality;
        }

        if (!File.Exists(outputPath))
        {
            quality.FailureReason = "The converted file is no longer there.";
            return quality;
        }

        var duration = analysis.DurationSeconds ?? 0d;
        var segments = SegmentsFor(duration);
        if (segments.Count == 0)
        {
            quality.FailureReason = "This file does not report how long it is, so there is no way "
                + "to line the two up for a comparison.";
            return quality;
        }

        var scores = new List<double>(segments.Count);
        string? lastFailure = null;

        foreach (var (start, seconds) in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var measurement = await _probe.CompareAsync(
                analysis.Path,
                start,
                outputPath,

                // The output is the whole film, so the same moment has to be sought out in it too.
                // This is the one thing that makes measuring a finished conversion different from
                // measuring a sample, and getting it wrong would compare unrelated frames and
                // report every conversion as ruined.
                start,
                seconds,
                analysis.Video.Width,
                analysis.Video.Height,
                metric,
                cancellationToken).ConfigureAwait(false);

            if (measurement.Succeeded)
            {
                scores.Add(measurement.Score);
            }
            else
            {
                lastFailure = measurement.FailureReason;
                _logger.LogWarning(
                    "[MediaOptimizer] Could not measure {Path} at {Start}s: {Reason}",
                    outputPath,
                    start,
                    measurement.FailureReason);
            }
        }

        if (scores.Count == 0)
        {
            quality.FailureReason = lastFailure ?? "The comparison produced no score.";
            return quality;
        }

        quality.Samples = scores.Count;
        quality.WorstScore = scores.Min();
        quality.MeanScore = scores.Average();
        return quality;
    }
}
