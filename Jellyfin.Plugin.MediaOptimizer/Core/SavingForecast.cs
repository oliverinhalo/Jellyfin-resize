using System;
using Jellyfin.Plugin.MediaOptimizer.Models;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>A rough answer to "is this file worth converting at all?".</summary>
public class Forecast
{
    /// <summary>Gets or sets the bytes a conversion would plausibly reclaim, or null when none would.</summary>
    public long? SavingBytes { get; set; }

    /// <summary>Gets or sets the change that would produce it, in words.</summary>
    public string? Basis { get; set; }

    /// <summary>Gets or sets the strategy that change corresponds to.</summary>
    public OptimizationStrategy Strategy { get; set; } = OptimizationStrategy.Standard;
}

/// <summary>
/// Ranks a library by how much each file has to gain, from the facts already in the item list —
/// size, resolution and codec — without probing anything.
/// <para>
/// This exists so the dashboard can answer "where should I start?" across a whole library in one
/// page load. It is deliberately cruder than the dialog's estimate, which reads the file's real
/// stream bitrates: this one is for ordering a list, and the number it shows is marked as an
/// approximation everywhere it appears. Ordering by size alone is worse than useless, because the
/// biggest file in a library is often a remux that is already efficiently encoded.
/// </para>
/// </summary>
public static class SavingForecast
{
    /// <summary>
    /// A conversion has fixed costs — a generation of quality, hours of CPU, a rewritten file —
    /// so a saving smaller than this is not worth putting on a worklist at all.
    /// </summary>
    private const double WorthShowingFraction = 0.08d;

    /// <summary>Forecasts what the recommended conversion would save on one file.</summary>
    /// <param name="sizeBytes">The file's size on disk.</param>
    /// <param name="height">The video height, when known.</param>
    /// <param name="codec">The video codec, when known.</param>
    /// <returns>The forecast; its saving is null when there is nothing worth doing.</returns>
    public static Forecast For(long? sizeBytes, int? height, string? codec)
    {
        var forecast = new Forecast();

        if (sizeBytes is not > 0)
        {
            return forecast;
        }

        var sourceEfficiency = StrategyResolver.EfficiencyOf(codec);
        var targetEfficiency = StrategyResolver.EfficiencyOf("hevc");

        // The same rule the dialog uses to preselect a strategy, so a file's place in this list
        // and the preset it opens on cannot disagree: a codec change worth having, or resolution.
        if (targetEfficiency < sourceEfficiency * 0.85d)
        {
            var factor = targetEfficiency / sourceEfficiency;
            var saving = (long)(sizeBytes.Value * (1d - factor));

            forecast.Strategy = OptimizationStrategy.Standard;
            forecast.Basis = FormattableString.Invariant(
                $"HEVC instead of {Describe(codec)}, same resolution");
            forecast.SavingBytes = Worthwhile(saving, sizeBytes.Value);
            return forecast;
        }

        var step = StrategyResolver.OneStepDown(height);
        if (step is null || height is not > 0)
        {
            return forecast;
        }

        // Bitrate scales with area, not with height, and sub-linearly at that: the same exponent
        // the size estimator uses, so the two cannot drift apart.
        var resolutionFactor = Math.Pow((double)step.Value / height.Value, 2d * SizeEstimator.ResolutionExponent);
        var reduced = (long)(sizeBytes.Value * (1d - resolutionFactor));

        forecast.Strategy = OptimizationStrategy.Medium;
        forecast.Basis = FormattableString.Invariant($"{step.Value}p instead of {height.Value}p");
        forecast.SavingBytes = Worthwhile(reduced, sizeBytes.Value);
        return forecast;
    }

    private static long? Worthwhile(long saving, long currentBytes) =>
        saving > 0 && saving >= currentBytes * WorthShowingFraction ? saving : null;

    private static string Describe(string? codec) => string.IsNullOrEmpty(codec)
        ? "this codec"
        : codec.ToUpperInvariant() switch
        {
            "H264" => "H.264",
            "HEVC" or "H265" => "HEVC",
            "MPEG2VIDEO" => "MPEG-2",
            "MSMPEG4V3" => "MPEG-4 v3",
            "VC1" => "VC-1",
            var other => other
        };
}
