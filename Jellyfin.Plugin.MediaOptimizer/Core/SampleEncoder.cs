using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>What a short sample encode actually measured.</summary>
public class SampleMeasurement
{
    /// <summary>Gets or sets how many samples were encoded.</summary>
    public int Samples { get; set; }

    /// <summary>Gets or sets the total number of source seconds encoded.</summary>
    public double SampledSeconds { get; set; }

    /// <summary>Gets or sets the measured output rate in bytes per second of content.</summary>
    public double BytesPerSecond { get; set; }

    /// <summary>Gets or sets the lowest rate any single sample produced.</summary>
    public double LowBytesPerSecond { get; set; }

    /// <summary>Gets or sets the highest rate any single sample produced.</summary>
    public double HighBytesPerSecond { get; set; }

    /// <summary>Gets or sets how fast the encode ran, as a multiple of realtime.</summary>
    public double? SpeedFactor { get; set; }

    /// <summary>Gets or sets why no measurement could be made, when none could.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets a value indicating whether the measurement succeeded.</summary>
    public bool Succeeded => FailureReason is null && Samples > 0 && BytesPerSecond > 0d;
}

/// <summary>Measures an encode by actually running a little of it.</summary>
public interface ISampleEncoder
{
    /// <summary>
    /// Encodes a few short stretches of the source with the real settings and reports what they
    /// produced.
    /// </summary>
    /// <param name="analysis">The source analysis.</param>
    /// <param name="plan">The plan whose arguments should be sampled.</param>
    /// <param name="workDirectory">Where the throwaway samples are written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measurement, or a reason it could not be made.</returns>
    Task<SampleMeasurement> MeasureAsync(
        FileAnalysis analysis,
        PlanResult plan,
        string workDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// The honest answer to "how big will it actually be?".
/// <para>
/// Every other number this plugin shows before a conversion is modelled: anchored on the source's
/// own bitrate, which is far better than a bits-per-pixel table but still a prediction. This one
/// encodes three short stretches of the real file with the real settings and measures what came
/// out, which is the difference between "about 4 GB, probably" and "4.1 GB, measured".
/// </para>
/// <para>
/// It costs a minute or so, which is why it is something the user asks for rather than something
/// that happens on every keystroke. Its own limits are stated where it is shown: three samples
/// cannot know about the twenty minutes of dark, grainy footage at the end of the film, which is
/// why the spread between the samples is reported alongside the average rather than hidden in it.
/// </para>
/// </summary>
public class SampleEncoder : ISampleEncoder
{
    /// <summary>Where in the file to sample, as fractions of the duration.</summary>
    private static readonly double[] SamplePoints = [0.2d, 0.5d, 0.8d];

    /// <summary>How many seconds each sample covers.</summary>
    private const double SampleSeconds = 8d;

    /// <summary>Below this, the whole file is shorter than the samples would be.</summary>
    private const double MinimumSourceSeconds = 45d;

    private readonly IFfmpegRunner _runner;
    private readonly ILogger<SampleEncoder> _logger;

    /// <summary>Initializes a new instance of the <see cref="SampleEncoder"/> class.</summary>
    /// <param name="runner">FFmpeg runner.</param>
    /// <param name="logger">Logger.</param>
    public SampleEncoder(IFfmpegRunner runner, ILogger<SampleEncoder> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// Rewrites a full encode's arguments into one that encodes a short stretch starting at a
    /// given point, writing somewhere throwaway.
    /// </summary>
    /// <param name="arguments">The planned arguments, ending in <c>-y &lt;output&gt;</c>.</param>
    /// <param name="startSeconds">Where in the source to start.</param>
    /// <param name="seconds">How much source to encode.</param>
    /// <param name="outputPath">Where to write the sample.</param>
    /// <param name="format">
    /// The muxer to name explicitly, or null when the plan already names one. The sample is
    /// written to a deliberately unguessable extension so the library scanner cannot pick it up,
    /// which also means ffmpeg cannot infer the muxer from it.
    /// </param>
    /// <returns>Arguments for the sample encode.</returns>
    internal static IReadOnlyList<string> BuildSampleArguments(
        IReadOnlyList<string> arguments,
        double startSeconds,
        double seconds,
        string outputPath,
        string? format = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var sample = new List<string>(arguments.Count + 8);

        // -ss has to precede -i to seek the input rather than decoding everything up to that
        // point and throwing it away, which on a two-hour file is the whole cost of the exercise.
        sample.Add("-ss");
        sample.Add(startSeconds.ToString("F3", CultureInfo.InvariantCulture));

        // Everything except the trailing "-y <output>".
        for (var i = 0; i < arguments.Count - 2; i++)
        {
            sample.Add(arguments[i]);
        }

        sample.Add("-t");
        sample.Add(seconds.ToString("F3", CultureInfo.InvariantCulture));

        if (format is not null)
        {
            sample.Add("-f");
            sample.Add(format);
        }

        sample.Add("-y");
        sample.Add(outputPath);

        return sample;
    }

    /// <summary>Picks where to sample a file of a given length.</summary>
    /// <param name="durationSeconds">The source duration.</param>
    /// <returns>Start offsets in seconds, ordered.</returns>
    internal static IReadOnlyList<double> SampleOffsets(double durationSeconds)
    {
        var offsets = new List<double>();
        foreach (var point in SamplePoints)
        {
            var start = durationSeconds * point;

            // Never let a sample run off the end: a short final sample would measure fewer
            // seconds than it claims and inflate the predicted rate.
            if (start + SampleSeconds <= durationSeconds)
            {
                offsets.Add(Math.Round(start, 3));
            }
        }

        return offsets;
    }

    /// <inheritdoc />
    public async Task<SampleMeasurement> MeasureAsync(
        FileAnalysis analysis,
        PlanResult plan,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(plan);

        var result = new SampleMeasurement();

        if (!plan.IsRunnable)
        {
            result.FailureReason = "These settings cannot run, so there is nothing to measure.";
            return result;
        }

        if (analysis.DurationSeconds is not > MinimumSourceSeconds)
        {
            result.FailureReason = FormattableString.Invariant(
                $"This file is too short to sample; anything under {MinimumSourceSeconds:F0} seconds is quicker to convert than to measure.");
            return result;
        }

        var offsets = SampleOffsets(analysis.DurationSeconds.Value);
        if (offsets.Count == 0)
        {
            result.FailureReason = "No usable sample points in a file this short.";
            return result;
        }

        var rates = new List<double>();
        var startedAt = DateTime.UtcNow;
        var sampled = 0d;

        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var samplePath = Path.Combine(
                workDirectory,
                FormattableString.Invariant($".mo-sample-{Guid.NewGuid():N}.{plan.OutputExtension}.motmp"));

            try
            {
                var arguments = BuildSampleArguments(
                    plan.Arguments,
                    offset,
                    SampleSeconds,
                    samplePath,
                    plan.Arguments.Contains("-f", StringComparer.Ordinal)
                        ? null
                        : string.Equals(plan.OutputExtension, "mp4", StringComparison.OrdinalIgnoreCase)
                            ? "mp4"
                            : "matroska");
                var run = await _runner
                    .RunEncodeAsync(arguments, SampleSeconds, null, true, cancellationToken)
                    .ConfigureAwait(false);

                if (!run.Success || !File.Exists(samplePath))
                {
                    _logger.LogWarning(
                        "[MediaOptimizer] Sample encode at {Offset}s failed: {Error}",
                        offset,
                        run.StandardError);
                    continue;
                }

                var length = new FileInfo(samplePath).Length;
                if (length > 0)
                {
                    rates.Add(length / SampleSeconds);
                    sampled += SampleSeconds;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[MediaOptimizer] Could not write a sample encode");
            }
            finally
            {
                TryDelete(samplePath);
            }
        }

        if (rates.Count == 0)
        {
            result.FailureReason = "None of the sample encodes produced anything. The full conversion would fail too.";
            return result;
        }

        var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;

        result.Samples = rates.Count;
        result.SampledSeconds = sampled;
        result.BytesPerSecond = rates.Average();
        result.LowBytesPerSecond = rates.Min();
        result.HighBytesPerSecond = rates.Max();
        result.SpeedFactor = elapsed > 0.5d ? sampled / elapsed : null;

        _logger.LogInformation(
            "[MediaOptimizer] Measured {Count} sample(s) of {Name}: {Rate} bytes/second of content",
            rates.Count,
            analysis.Name,
            result.BytesPerSecond.ToString("F0", CultureInfo.InvariantCulture));

        return result;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not remove sample file {Path}", path);
        }
    }
}
