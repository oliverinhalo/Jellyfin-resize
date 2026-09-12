using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>How close an encoded segment looked to the source it came from.</summary>
public class QualityMeasurement
{
    /// <summary>Gets or sets which metric produced the score: VMAF or SSIM.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>Gets or sets the score in the metric's own units — VMAF 0-100, SSIM 0-1.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets why no comparison could be made.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets a value indicating whether a score was obtained.</summary>
    public bool Succeeded => FailureReason is null && !string.IsNullOrEmpty(Metric);
}

/// <summary>
/// How close a result looked, in terms that do not depend on which metric measured it.
/// <para>
/// The point of naming the bands is that a number cannot be compared across metrics but a verdict
/// can: "very hard to tell apart" means the same thing whether it came from VMAF 94 or SSIM 0.984,
/// and it is the form a person can actually hold an outcome to.
/// </para>
/// </summary>
public enum QualityVerdict
{
    /// <summary>Worse than "visibly worse".</summary>
    ClearlyDegraded = 0,

    /// <summary>Plainly worse than the source without needing a comparison.</summary>
    VisiblyWorse = 1,

    /// <summary>Softer on detailed scenes, noticeable while watching.</summary>
    NoticeablySofter = 2,

    /// <summary>Slightly softer; findable side by side.</summary>
    SlightlySofter = 3,

    /// <summary>Very hard to tell apart from the source.</summary>
    VeryClose = 4,

    /// <summary>Indistinguishable from the source.</summary>
    Indistinguishable = 5
}

/// <summary>Compares an encoded segment against the source it was made from.</summary>
public interface IQualityProbe
{
    /// <summary>Scores one stretch of an encoded file against the matching stretch of the source.</summary>
    /// <param name="referencePath">The original file.</param>
    /// <param name="referenceStartSeconds">Where in the original the comparison starts.</param>
    /// <param name="encodedPath">The encoded file — a short sample, or a whole conversion.</param>
    /// <param name="encodedStartSeconds">
    /// Where in the encoded file the same moment is. Zero for a sample, which begins at the
    /// moment being compared; the same offset as the reference for a finished conversion, which
    /// is the whole film.
    /// </param>
    /// <param name="seconds">How long a stretch to compare.</param>
    /// <param name="referenceWidth">The source's width, so a downscaled encode can be compared at the size it will be watched.</param>
    /// <param name="referenceHeight">The source's height.</param>
    /// <param name="metric">"VMAF" or "SSIM".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measurement, or a reason none could be made.</returns>
    Task<QualityMeasurement> CompareAsync(
        string referencePath,
        double referenceStartSeconds,
        string encodedPath,
        double encodedStartSeconds,
        double seconds,
        int? referenceWidth,
        int? referenceHeight,
        string metric,
        CancellationToken cancellationToken);
}

/// <summary>
/// Answers "how much worse will it look?" with a number instead of a preset name.
/// <para>
/// Size has always been measurable and quality never was, which is why every tool in this space
/// asks people to choose between words like "medium" and "high". FFmpeg can compare two video
/// streams frame by frame — VMAF where the build has it, SSIM everywhere else — so on the segments
/// that are being sample-encoded anyway, the comparison is nearly free and turns the question into
/// one with an answer.
/// </para>
/// <para>
/// The score is reported with the name of the metric that produced it, because they are not
/// interchangeable: SSIM 0.98 and VMAF 98 mean very different things, and a single unlabelled
/// "quality: 98" would invite exactly that confusion.
/// </para>
/// </summary>
public partial class QualityProbe : IQualityProbe
{
    /// <summary>The metric name for VMAF, as reported to the interface.</summary>
    public const string Vmaf = "VMAF";

    /// <summary>The metric name for SSIM.</summary>
    public const string Ssim = "SSIM";

    private readonly IFfmpegRunner _runner;
    private readonly ILogger<QualityProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="QualityProbe"/> class.</summary>
    /// <param name="runner">FFmpeg runner.</param>
    /// <param name="logger">Logger.</param>
    public QualityProbe(IFfmpegRunner runner, ILogger<QualityProbe> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// Builds the comparison. The encoded segment is scaled back to the source's resolution first,
    /// because that is the comparison a viewer actually makes: they watch the smaller file on the
    /// same screen, not at its own pixel size.
    /// </summary>
    /// <param name="metric">"VMAF" or "SSIM".</param>
    /// <returns>A filter graph for two inputs, reference first.</returns>
    internal static string BuildFilter(string metric, int? referenceWidth, int? referenceHeight)
    {
        // [0:v] is the reference (the source), [1:v] the encoded segment; both metrics take the
        // distorted stream first.
        //
        // scale2ref would size the encoded stream to the reference without being told the numbers,
        // and it is what this first used -- but on ffmpeg 7 that graph deadlocks outright often
        // enough to be seen in fifteen runs, hanging the whole process rather than failing. The
        // dimensions are known here anyway, so they are passed in and the scale is an ordinary
        // one. "shortest" is the other half of the same fix: without it, two inputs that end a
        // frame apart leave the comparison waiting for a frame that is never coming.
        var comparison = string.Equals(metric, Vmaf, StringComparison.OrdinalIgnoreCase)
            ? "libvmaf=n_threads=4:shortest=1"
            : "ssim=shortest=1";

        var scale = referenceWidth is > 0 && referenceHeight is > 0
            ? FormattableString.Invariant($"scale={referenceWidth.Value}:{referenceHeight.Value}:flags=bicubic,")
            : string.Empty;

        return "[1:v]" + scale + "settb=AVTB,setpts=PTS-STARTPTS[enc];"
            + "[0:v]settb=AVTB,setpts=PTS-STARTPTS[ref];"
            + "[enc][ref]" + comparison;
    }

    /// <summary>Reads the score out of ffmpeg's own report.</summary>
    /// <param name="metric">Which metric was asked for.</param>
    /// <param name="output">Everything ffmpeg printed.</param>
    /// <returns>The score, or null when the line is not there.</returns>
    internal static double? ParseScore(string metric, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = string.Equals(metric, Vmaf, StringComparison.OrdinalIgnoreCase)
            ? VmafRegex().Match(output)
            : SsimRegex().Match(output);

        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(
            match.Groups["score"].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var score)
            ? score
            : null;
    }

    /// <summary>
    /// Formats a score for display in the units it is actually in.
    /// <para>
    /// VMAF runs 0-100 and one decimal place is plenty. SSIM runs 0-1, where everything
    /// interesting happens in the third and fourth decimal — 0.982 and 0.995 are different
    /// answers, and both round to "1.0". Most builds of jellyfin-ffmpeg have no VMAF, so SSIM is
    /// the common case rather than the exotic one.
    /// </para>
    /// </summary>
    /// <param name="metric">Which metric produced the score.</param>
    /// <param name="score">The score.</param>
    /// <returns>The score as text.</returns>
    public static string FormatScore(string metric, double score) =>
        string.Equals(metric, Vmaf, StringComparison.OrdinalIgnoreCase)
            ? score.ToString("F1", CultureInfo.InvariantCulture)
            : score.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    /// The one table of thresholds, best band first. Both scales in one place on purpose: they are
    /// not convertible, and three copies of these numbers — a verdict, a search target and a floor
    /// a conversion has to clear — is three chances for the words on screen to stop meaning the
    /// number behind them. The values are the ones in common use: VMAF 97 is where a difference
    /// stops being findable side by side, and 88 is the usual floor for an archive re-encode.
    /// </summary>
    private static readonly (QualityVerdict Verdict, double Vmaf, double Ssim, string Words)[] Bands =
    [
        (QualityVerdict.Indistinguishable, 97d, 0.99d, "indistinguishable from the source"),
        (QualityVerdict.VeryClose, 93d, 0.98d, "very hard to tell apart from the source"),
        (QualityVerdict.SlightlySofter, 88d, 0.96d, "slightly softer; visible only side by side"),
        (QualityVerdict.NoticeablySofter, 80d, 0.93d, "noticeably softer on detailed scenes"),
        (QualityVerdict.VisiblyWorse, 70d, 0.88d, "visibly worse"),
        (QualityVerdict.ClearlyDegraded, double.MinValue, double.MinValue, "clearly degraded")
    ];

    /// <summary>Which band a score falls in.</summary>
    /// <param name="metric">Which metric produced the score.</param>
    /// <param name="score">The score, in that metric's units.</param>
    /// <returns>The verdict.</returns>
    public static QualityVerdict VerdictFor(string metric, double score)
    {
        var vmaf = string.Equals(metric, Vmaf, StringComparison.OrdinalIgnoreCase);

        foreach (var band in Bands)
        {
            if (score >= (vmaf ? band.Vmaf : band.Ssim))
            {
                return band.Verdict;
            }
        }

        return QualityVerdict.ClearlyDegraded;
    }

    /// <summary>The lowest score that still counts as a given verdict.</summary>
    /// <param name="metric">Which metric the score will be in.</param>
    /// <param name="verdict">The verdict to reach.</param>
    /// <returns>The score at which that band starts.</returns>
    public static double FloorFor(string metric, QualityVerdict verdict)
    {
        var vmaf = string.Equals(metric, Vmaf, StringComparison.OrdinalIgnoreCase);

        foreach (var band in Bands)
        {
            if (band.Verdict == verdict)
            {
                return vmaf ? band.Vmaf : band.Ssim;
            }
        }

        return vmaf ? 0d : 0d;
    }

    /// <summary>
    /// Turns a score into a sentence. The number is the honest part; the words are what makes it
    /// usable by somebody who has never heard of VMAF.
    /// </summary>
    /// <param name="metric">Which metric produced the score.</param>
    /// <param name="score">The score.</param>
    /// <returns>A short verdict.</returns>
    public static string Describe(string metric, double score) => Describe(VerdictFor(metric, score));

    /// <summary>The words for a verdict, so a target or a floor reads the same as a result.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>A short description.</returns>
    public static string Describe(QualityVerdict verdict)
    {
        foreach (var band in Bands)
        {
            if (band.Verdict == verdict)
            {
                return band.Words;
            }
        }

        return "clearly degraded";
    }

    /// <inheritdoc />
    public async Task<QualityMeasurement> CompareAsync(
        string referencePath,
        double referenceStartSeconds,
        string encodedPath,
        double encodedStartSeconds,
        double seconds,
        int? referenceWidth,
        int? referenceHeight,
        string metric,
        CancellationToken cancellationToken)
    {
        var result = new QualityMeasurement { Metric = metric };

        if (!File.Exists(encodedPath))
        {
            result.FailureReason = "The encoded sample is no longer there.";
            return result;
        }

        List<string> arguments =
        [
            "-nostdin",
            "-ss", referenceStartSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", seconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", referencePath,
        ];

        // A finished conversion is the whole film, so the same moment has to be sought out in it
        // as well; a sample file starts at that moment already. The seek goes before -i, where
        // ffmpeg can jump to the nearest keyframe instead of decoding everything up to it, which
        // on a two-hour file is the difference between seconds and minutes.
        if (encodedStartSeconds > 0d)
        {
            arguments.Add("-ss");
            arguments.Add(encodedStartSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        arguments.AddRange(
        [
            // Both inputs are cut to the same length. A comparison of streams that end at
            // different moments is where these filters get into trouble.
            "-t", seconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", encodedPath,

            "-lavfi", BuildFilter(metric, referenceWidth, referenceHeight),
            "-f", "null",
            "-"
        ]);

        // A comparison is a nicety; a hung one must never hold up the job it was measuring. This
        // is not theoretical -- an earlier version of this graph hung ffmpeg outright, and only a
        // timeout turns that into a missing number rather than a stuck queue.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(120d, seconds * 20d)));

        try
        {
            var run = await _runner
                .RunEncodeAsync(arguments, seconds, null, true, timeout.Token)
                .ConfigureAwait(false);

            // The score is printed by the filter, which writes to stderr at info level.
            var score = ParseScore(metric, run.StandardError);
            if (score is null)
            {
                _logger.LogWarning(
                    "[MediaOptimizer] {Metric} produced no score for {Path}",
                    metric,
                    encodedPath);

                // Carry a little of ffmpeg's own output: "no score" on its own tells whoever is
                // looking at the log nothing about why.
                var detail = (run.StandardError ?? string.Empty).Trim();
                result.FailureReason = detail.Length > 0
                    ? "The comparison produced no score: " + Tail(detail)
                    : "The comparison produced no score.";
                return result;
            }

            result.Score = score.Value;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[MediaOptimizer] The quality comparison for {Path} timed out", encodedPath);
            result.FailureReason = "The comparison took too long and was stopped.";
            return result;
        }
#pragma warning disable CA1031 // A quality number is a bonus; failing to get one must not fail the estimate.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Quality comparison failed for {Path}", encodedPath);
            result.FailureReason = "The comparison could not be run: " + ex.Message;
            return result;
        }
    }

    private static string Tail(string text) =>
        text.Length <= 300 ? text : "…" + text[^300..];

    [GeneratedRegex(@"VMAF score:\s*(?<score>[0-9]+(\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VmafRegex();

    [GeneratedRegex(@"SSIM.*?\bAll:\s*(?<score>[0-9]+(\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SsimRegex();
}
