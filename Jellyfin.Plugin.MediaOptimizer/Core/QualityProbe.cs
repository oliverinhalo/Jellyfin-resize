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

/// <summary>Compares an encoded segment against the source it was made from.</summary>
public interface IQualityProbe
{
    /// <summary>Scores one encoded segment against the matching stretch of the source.</summary>
    /// <param name="referencePath">The original file.</param>
    /// <param name="startSeconds">Where in the original the segment came from.</param>
    /// <param name="seconds">How long the segment is.</param>
    /// <param name="encodedPath">The encoded segment.</param>
    /// <param name="referenceWidth">The source's width, so a downscaled encode can be compared at the size it will be watched.</param>
    /// <param name="referenceHeight">The source's height.</param>
    /// <param name="metric">"VMAF" or "SSIM".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measurement, or a reason none could be made.</returns>
    Task<QualityMeasurement> CompareAsync(
        string referencePath,
        double startSeconds,
        double seconds,
        string encodedPath,
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
    /// Turns a score into a sentence. The number is the honest part; the words are what makes it
    /// usable by somebody who has never heard of VMAF.
    /// </summary>
    /// <param name="metric">Which metric produced the score.</param>
    /// <param name="score">The score.</param>
    /// <returns>A short verdict.</returns>
    public static string Describe(string metric, double score)
    {
        // Thresholds are the ones in common use: VMAF 95+ is the point at which a difference stops
        // being findable in a side-by-side, and 90+ is the usual target for an archive re-encode.
        if (string.Equals(metric, Vmaf, StringComparison.OrdinalIgnoreCase))
        {
            return score switch
            {
                >= 97d => "indistinguishable from the source",
                >= 93d => "very hard to tell apart from the source",
                >= 88d => "slightly softer; visible only side by side",
                >= 80d => "noticeably softer on detailed scenes",
                >= 70d => "visibly worse",
                _ => "clearly degraded"
            };
        }

        return score switch
        {
            >= 0.99d => "indistinguishable from the source",
            >= 0.98d => "very hard to tell apart from the source",
            >= 0.96d => "slightly softer; visible only side by side",
            >= 0.93d => "noticeably softer on detailed scenes",
            >= 0.88d => "visibly worse",
            _ => "clearly degraded"
        };
    }

    /// <inheritdoc />
    public async Task<QualityMeasurement> CompareAsync(
        string referencePath,
        double startSeconds,
        double seconds,
        string encodedPath,
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
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", seconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", referencePath,

            // Both inputs are cut to the same length. A comparison of streams that end at
            // different moments is where these filters get into trouble.
            "-t", seconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", encodedPath,

            "-lavfi", BuildFilter(metric, referenceWidth, referenceHeight),
            "-f", "null",
            "-"
        ];

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
