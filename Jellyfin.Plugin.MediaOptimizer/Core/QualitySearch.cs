using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>How close to the source the result has to look.</summary>
/// <remarks>
/// Deliberately three words rather than a number. "CRF 24" is a setting; "I want it to be very
/// hard to tell apart from the source" is what somebody actually wants, and since the plugin can
/// now measure the difference, it is the thing they should be asked for.
/// </remarks>
public enum QualityTarget
{
    /// <summary>Indistinguishable from the source.</summary>
    Indistinguishable = 0,

    /// <summary>Very hard to tell apart from the source.</summary>
    VeryClose = 1,

    /// <summary>Slightly softer; visible only side by side.</summary>
    SlightlySofter = 2
}

/// <summary>What the search settled on, and what it measured to get there.</summary>
public class QualitySearchResult
{
    /// <summary>Gets or sets the quality value that met the target.</summary>
    public int? Quality { get; set; }

    /// <summary>Gets or sets the metric the scores are in.</summary>
    public string? Metric { get; set; }

    /// <summary>Gets or sets the worst score across the confirming samples.</summary>
    public double? WorstScore { get; set; }

    /// <summary>Gets or sets the average score across the confirming samples.</summary>
    public double? AverageScore { get; set; }

    /// <summary>Gets or sets the worst score formatted in the metric's own units.</summary>
    public string? WorstScoreText { get; set; }

    /// <summary>Gets or sets the plain-language verdict for the worst score.</summary>
    public string? Verdict { get; set; }

    /// <summary>Gets or sets how many sample encodes this cost.</summary>
    public int Probes { get; set; }

    /// <summary>Gets or sets how many seconds of the source the final answer was confirmed on.</summary>
    public double SecondsConfirmed { get; set; }

    /// <summary>Gets or sets the measured size of the whole file at that setting.</summary>
    public long? EstimatedSizeBytes { get; set; }

    /// <summary>Gets or sets the measured saving as a fraction of the current size.</summary>
    public double? SavingFraction { get; set; }

    /// <summary>Gets or sets anything the answer has to be read with.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets why no answer could be found.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets a value indicating whether a setting was found.</summary>
    public bool Succeeded => FailureReason is null && Quality is not null;
}

/// <summary>Finds the smallest file that still meets a stated quality target.</summary>
public interface IQualitySearch
{
    /// <summary>Searches for the highest quality number that still hits the target.</summary>
    /// <param name="analysis">The source analysis.</param>
    /// <param name="request">The settings to search within; its quality is what moves.</param>
    /// <param name="target">How close to the source the result has to look.</param>
    /// <param name="metric">The metric this server's FFmpeg can measure with, or null.</param>
    /// <param name="workDirectory">Where throwaway samples are written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The setting it found, or why it found none.</returns>
    Task<QualitySearchResult> SearchAsync(
        FileAnalysis analysis,
        EncodeRequest request,
        QualityTarget target,
        string? metric,
        string workDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Turns "how good do you want it to look?" into a setting, by measuring.
/// <para>
/// Choosing a quality number is the one decision this plugin could never help with. The advice in
/// every forum is a number somebody else's files liked, and the plugin's own presets are the same
/// thing with better manners: a grainy 1970s film and a flat animated series want CRF values four
/// or five apart, and no table knows which one it is looking at.
/// </para>
/// <para>
/// The quality measurement makes the question answerable on this file. The search walks the
/// quality scale by halving — five short encodes covers a range of twenty settings — and each step
/// is a real encode of a real stretch of this film, compared against the source frame by frame.
/// The setting it lands on is then confirmed across three points in the file, and it is the
/// <em>worst</em> of those three that has to meet the target, because an average is exactly how a
/// bad dark scene gets hidden.
/// </para>
/// </summary>
public class QualitySearch : IQualitySearch
{
    /// <summary>
    /// How many settings down from the found one the search will step when the confirmation
    /// across the whole file disagrees with the single sample the search used.
    /// </summary>
    private const int ConfirmationSteps = 2;

    private readonly IEncodePlanner _planner;
    private readonly ISampleEncoder _sampler;
    private readonly ISizeEstimator _estimator;
    private readonly ILogger<QualitySearch> _logger;

    /// <summary>Initializes a new instance of the <see cref="QualitySearch"/> class.</summary>
    /// <param name="planner">Encode planner.</param>
    /// <param name="sampler">Sample encoder.</param>
    /// <param name="estimator">Size estimator, for turning a measured rate into a file size.</param>
    /// <param name="logger">Logger.</param>
    public QualitySearch(
        IEncodePlanner planner,
        ISampleEncoder sampler,
        ISizeEstimator estimator,
        ILogger<QualitySearch> logger)
    {
        _planner = planner;
        _sampler = sampler;
        _estimator = estimator;
        _logger = logger;
    }

    /// <summary>
    /// The score a target demands, in the metric's own units.
    /// <para>
    /// These are the thresholds <see cref="QualityProbe.Describe"/> already uses, on purpose: the
    /// words offered in the picker and the words reported about the result come from one table, so
    /// a search for "very hard to tell apart" cannot come back describing its own answer as
    /// something else.
    /// </para>
    /// </summary>
    /// <param name="metric">The metric in use.</param>
    /// <param name="target">The requested target.</param>
    /// <returns>The minimum acceptable score.</returns>
    public static double ThresholdFor(string metric, QualityTarget target)
    {
        var vmaf = string.Equals(metric, QualityProbe.Vmaf, StringComparison.OrdinalIgnoreCase);

        return target switch
        {
            QualityTarget.Indistinguishable => vmaf ? 97d : 0.99d,
            QualityTarget.VeryClose => vmaf ? 93d : 0.98d,
            _ => vmaf ? 88d : 0.96d
        };
    }

    /// <summary>
    /// The range of quality values worth searching for a given encoder.
    /// <para>
    /// The scales are not comparable: CRF 32 is a reasonable AV1 encode and a badly damaged H.264
    /// one. Each range is centred on the value this plugin would have chosen anyway, so the search
    /// spends its encodes where the answer actually is rather than proving that CRF 4 looks fine.
    /// </para>
    /// </summary>
    /// <param name="encoder">The encoder name, such as "libx265" or "av1_qsv".</param>
    /// <returns>The lowest and highest quality values to consider.</returns>
    internal static (int Low, int High) RangeFor(string? encoder)
    {
        var name = (encoder ?? string.Empty).ToLowerInvariant();

        if (name.Contains("av1", StringComparison.Ordinal))
        {
            return (24, 44);
        }

        if (name.Contains("vp9", StringComparison.Ordinal) || name.Contains("vpx", StringComparison.Ordinal))
        {
            return (24, 42);
        }

        if (name.Contains("hevc", StringComparison.Ordinal) || name.Contains("265", StringComparison.Ordinal))
        {
            return (20, 36);
        }

        return (16, 32);
    }

    /// <inheritdoc />
    public async Task<QualitySearchResult> SearchAsync(
        FileAnalysis analysis,
        EncodeRequest request,
        QualityTarget target,
        string? metric,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(request);

        var result = new QualitySearchResult { Metric = metric };

        var refusal = Refuse(request, metric);
        if (refusal is not null)
        {
            result.FailureReason = refusal;
            return result;
        }

        var threshold = ThresholdFor(metric!, target);
        var (low, high) = RangeFor(request.VideoCodec);
        var probes = 0;

        int? best = null;

        // Halving the range rather than walking it: twenty settings in five encodes. It assumes a
        // higher quality number never looks better, which is what the number means -- and the
        // confirmation below is what catches it if a particular encode misbehaves anyway.
        var lowBound = low;
        var highBound = high;
        double? scoreAtLowest = null;

        while (lowBound <= highBound)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = (lowBound + highBound) / 2;
            var probe = await MeasureAtAsync(analysis, request, candidate, metric, workDirectory, 1, cancellationToken)
                .ConfigureAwait(false);
            probes++;

            if (!probe.Succeeded || probe.QualityScore is null)
            {
                result.Probes = probes;
                result.FailureReason = probe.FailureReason
                    ?? "A sample encode produced no quality score, so there is nothing to search on.";
                return result;
            }

            _logger.LogInformation(
                "[MediaOptimizer] Quality search: {Metric} {Score} at quality {Quality}",
                metric,
                probe.QualityScore.Value.ToString("F4", CultureInfo.InvariantCulture),
                candidate);

            if (candidate == low)
            {
                scoreAtLowest = probe.QualityScore;
            }

            if (probe.QualityScore >= threshold)
            {
                // Good enough: try a smaller file.
                best = candidate;
                lowBound = candidate + 1;
            }
            else
            {
                highBound = candidate - 1;
            }
        }

        if (best is null)
        {
            result.Probes = probes;
            result.FailureReason = scoreAtLowest is not null
                ? FormattableString.Invariant(
                    $"Even quality {low}, the highest this search covers, only reached {QualityProbe.FormatScore(metric!, scoreAtLowest.Value)}. This source cannot be re-encoded to that target — it is already close to what the encoder can hold on to.")
                : FormattableString.Invariant(
                    $"No setting between quality {low} and {high} reached the target. This source cannot be re-encoded to it.");
            return result;
        }

        // Confirm across the whole file. The search decided on eight seconds; a setting that only
        // holds up in the middle of the film is not an answer, and the worst sample is the one
        // that has to meet the target because an average is how a bad scene hides.
        var chosen = best.Value;
        SampleMeasurement? confirmation = null;

        for (var step = 0; step <= ConfirmationSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            confirmation = await MeasureAtAsync(analysis, request, chosen, metric, workDirectory, 0, cancellationToken)
                .ConfigureAwait(false);
            probes += confirmation.Samples;

            if (!confirmation.Succeeded || confirmation.WorstQualityScore is null)
            {
                result.Probes = probes;
                result.FailureReason = confirmation.FailureReason
                    ?? "The chosen setting could not be confirmed across the file.";
                return result;
            }

            if (confirmation.WorstQualityScore >= threshold || chosen <= low)
            {
                break;
            }

            // One sample said yes and the file as a whole says no. Step towards better quality and
            // ask again rather than reporting a target that was not actually met.
            chosen--;
        }

        var measurement = confirmation!;
        result.Quality = chosen;
        result.Probes = probes;
        result.SecondsConfirmed = measurement.SampledSeconds;
        result.WorstScore = measurement.WorstQualityScore;
        result.AverageScore = measurement.QualityScore;
        result.WorstScoreText = QualityProbe.FormatScore(metric!, measurement.WorstQualityScore!.Value);
        result.Verdict = QualityProbe.Describe(metric!, measurement.WorstQualityScore.Value);

        var finalRequest = With(request, chosen);
        var plan = await _planner
            .PlanAsync(analysis, finalRequest, "/dev/null", cancellationToken)
            .ConfigureAwait(false);

        var modelled = _estimator.Estimate(analysis, finalRequest, plan);
        var measured = SizeEstimator.FromMeasurement(analysis, measurement, modelled);
        result.EstimatedSizeBytes = measured.EstimatedSizeBytes;
        result.SavingFraction = measured.SavingFraction;

        if (measurement.WorstQualityScore < threshold)
        {
            result.Note = FormattableString.Invariant(
                $"Quality {chosen} is the closest this search could get: its worst sample still scored {result.WorstScoreText}, under the {QualityProbe.FormatScore(metric!, threshold)} this target asks for. Nothing between here and quality {low} did better on the whole file.");
        }

        return result;
    }

    /// <summary>Encodes and scores one candidate setting.</summary>
    /// <param name="samples">1 for a single sample in the middle of the file, 0 for all of them.</param>
    private async Task<SampleMeasurement> MeasureAtAsync(
        FileAnalysis analysis,
        EncodeRequest request,
        int quality,
        string? metric,
        string workDirectory,
        int samples,
        CancellationToken cancellationToken)
    {
        var candidate = With(request, quality);

        var plan = await _planner
            .PlanAsync(analysis, candidate, System.IO.Path.Combine(workDirectory, "search.motmp"), cancellationToken)
            .ConfigureAwait(false);

        if (!plan.IsRunnable)
        {
            return new SampleMeasurement
            {
                FailureReason = "These settings cannot run, so there is nothing to search."
            };
        }

        return await _sampler
            .MeasureAsync(analysis, plan, workDirectory, metric, samples, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Copies a request with a different quality value.</summary>
    /// <param name="request">The request to copy.</param>
    /// <param name="quality">The quality to set.</param>
    /// <returns>The copy.</returns>
    private static EncodeRequest With(EncodeRequest request, int quality)
    {
        var copy = request.Clone();
        copy.Quality = quality;
        copy.RateControl = RateControlMode.ConstantQuality;
        return copy;
    }

    /// <summary>
    /// Says no, with the reason, to the cases where there is nothing to search for. Each of these
    /// would otherwise cost somebody ten minutes of encoding to be told nothing.
    /// </summary>
    /// <param name="request">The proposed settings.</param>
    /// <param name="metric">The metric available on this server.</param>
    /// <returns>The reason, or null when the search can run.</returns>
    internal static string? Refuse(EncodeRequest request, string? metric)
    {
        if (string.IsNullOrEmpty(metric))
        {
            return "This server's FFmpeg cannot compare two videos — it has neither the libvmaf nor "
                + "the ssim filter — so there is no way to tell whether a setting met a target.";
        }

        if (request.Video != VideoAction.Encode)
        {
            return "The video is being kept exactly as it is, so there is no quality setting to search for.";
        }

        if (request.RateControl == RateControlMode.Lossless)
        {
            return "A lossless conversion is already identical to the source; there is nothing to search for.";
        }

        if (request.RateControl != RateControlMode.ConstantQuality)
        {
            return "This searches the constant-quality scale. Switch the rate control to constant "
                + "quality first — with a fixed bitrate or a target size, the quality is whatever is left over.";
        }

        if (string.IsNullOrEmpty(request.VideoCodec))
        {
            return "No video encoder has been chosen, so there is nothing to search with.";
        }

        return null;
    }
}
