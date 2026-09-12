using System;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Predicts output size so the dialog can show a saving before anything runs.</summary>
public interface ISizeEstimator
{
    /// <summary>Estimates the result of a request.</summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <param name="plan">The validated plan, for its warnings and lossless flag.</param>
    /// <returns>The estimate.</returns>
    EstimateResult Estimate(FileAnalysis analysis, EncodeRequest request, PlanResult plan);
}

/// <inheritdoc />
public class SizeEstimator : ISizeEstimator
{
    // Each CRF step changes bitrate by roughly this factor, in both directions.
    private const double CrfStepFactor = 1.12d;

    /// <summary>
    /// Bitrate does not scale linearly with pixel count: half the pixels needs rather more than
    /// half the bitrate. This exponent is the usual rule of thumb, and is shared with
    /// <see cref="SavingForecast"/> so the library worklist and the per-file estimate cannot
    /// disagree about what a resolution change buys.
    /// </summary>
    internal const double ResolutionExponent = 0.75d;

    private readonly IJobStore _store;

    /// <summary>Initializes a new instance of the <see cref="SizeEstimator"/> class.</summary>
    /// <param name="store">Job store, used to learn this server's real encoding speed.</param>
    public SizeEstimator(IJobStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public EstimateResult Estimate(FileAnalysis analysis, EncodeRequest request, PlanResult plan)
    {
        var duration = analysis.DurationSeconds ?? 0d;
        var result = new EstimateResult
        {
            CurrentSizeBytes = analysis.SizeBytes ?? 0,
            IsLossless = plan.IsLossless,
            Warnings = plan.Warnings,
            Method = "heuristic"
        };

        if (duration <= 0d || analysis.SizeBytes is not > 0)
        {
            result.EstimatedSizeBytes = analysis.SizeBytes ?? 0;
            result.EstimatedSizeLowBytes = result.EstimatedSizeBytes;
            result.EstimatedSizeHighBytes = result.EstimatedSizeBytes;
            result.Confidence = EstimateConfidence.Unknown;
            return result;
        }

        var videoBits = EstimateVideoBitrate(analysis, request) * duration;
        var audioBits = EstimateAudioBitrate(analysis, request) * duration;

        // Container overhead is around 1% for both MKV and MP4 at these bitrates.
        var totalBytes = (long)Math.Max(1024d, (videoBits + audioBits) / 8d * 1.01d);
        result.EstimatedSizeBytes = totalBytes;

        // Defence in depth against a source whose reported stream bitrates do not add up: a
        // re-encode that asks for no more quality, no more pixels and no less efficient a codec
        // cannot legitimately produce a bigger file than it started from.
        if (IsNonExpanding(analysis, request) && totalBytes > result.CurrentSizeBytes)
        {
            totalBytes = result.CurrentSizeBytes;
            result.EstimatedSizeBytes = totalBytes;
        }

        var uncertainty = request.Video == VideoAction.Encode ? 0.25d : 0.03d;
        result.EstimatedSizeLowBytes = (long)(totalBytes * (1d - uncertainty));
        result.EstimatedSizeHighBytes = (long)(totalBytes * (1d + uncertainty));
        result.SavingFraction = 1d - ((double)totalBytes / result.CurrentSizeBytes);

        result.Confidence = request.Video switch
        {
            VideoAction.Copy => EstimateConfidence.High,
            VideoAction.Encode when analysis.Video?.Bitrate.Bps is > 0 => EstimateConfidence.Medium,
            _ => EstimateConfidence.Low
        };

        ApplyTimeEstimate(analysis, request, duration, result);
        AddSavingNote(analysis, request, result);

        return result;
    }

    /// <summary>
    /// True when nothing about the target asks for more bits than the source already spends.
    /// </summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <returns>Whether the output cannot legitimately be larger.</returns>
    internal static bool IsNonExpanding(FileAnalysis analysis, EncodeRequest request)
    {
        var video = analysis.Video;
        if (video is null || request.Video != VideoAction.Encode)
        {
            return request.Video == VideoAction.Drop;
        }

        if (request.RateControl != RateControlMode.ConstantQuality)
        {
            return false;
        }

        var targetFamily = StrategyResolver.CodecFamilyOf(request.VideoCodec);
        if (StrategyResolver.EfficiencyOf(targetFamily) > StrategyResolver.EfficiencyOf(video.Codec))
        {
            return false;
        }

        var targetHeight = EncodePlanner.ResolveTargetHeight(video.Width, video.Height, request.TargetHeight);
        if (targetHeight is not null && video.Height is > 0 && targetHeight > video.Height)
        {
            return false;
        }

        var reference = StrategyResolver.ReferenceCrfOf(targetFamily);
        if ((request.Quality ?? reference) < reference)
        {
            return false;
        }

        // Re-encoding lossy audio to a lossless codec genuinely can grow the file.
        foreach (var track in request.AudioTracks)
        {
            if (track.Action != AudioAction.Encode
                || !string.Equals(track.Codec, "flac", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = analysis.Audio.FirstOrDefault(a => a.Index == track.Index);
            if (source is not null && !source.IsLossless)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Predicts the video bitrate the encoder will land on.
    /// <para>
    /// Anchored on the source's own bitrate rather than an absolute bits-per-pixel table. That
    /// matters: an absolute model has no idea whether the source was already efficiently encoded,
    /// so it happily predicts that re-encoding a lean 4K HEVC file makes it bigger.
    /// </para>
    /// </summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <returns>Estimated bits per second.</returns>
    internal static double EstimateVideoBitrate(FileAnalysis analysis, EncodeRequest request)
    {
        var video = analysis.Video;
        if (video is null || request.Video == VideoAction.Drop)
        {
            return 0d;
        }

        var sourceBitrate = (double)(video.Bitrate.Bps ?? 0);

        if (request.Video == VideoAction.Copy)
        {
            return sourceBitrate;
        }

        if (request.RateControl == RateControlMode.AverageBitrate && request.VideoBitrateBps is > 0)
        {
            return request.VideoBitrateBps.Value;
        }

        if (request.RateControl == RateControlMode.TargetSize)
        {
            return EncodePlanner.ComputeTargetVideoBitrate(analysis, request) ?? sourceBitrate;
        }

        if (request.RateControl == RateControlMode.Lossless)
        {
            // Only reachable from a lossless source, where roughly half is typical.
            return sourceBitrate * 0.5d;
        }

        var sourceHeight = video.Height ?? 1080;
        var sourceWidth = video.Width ?? 1920;
        var targetHeight = EncodePlanner.ResolveTargetHeight(sourceWidth, sourceHeight, request.TargetHeight)
            ?? sourceHeight;

        var resolutionFactor = sourceHeight > 0
            ? Math.Pow((double)targetHeight / sourceHeight, 2d * ResolutionExponent)
            : 1d;

        var targetFamily = StrategyResolver.CodecFamilyOf(request.VideoCodec);
        var codecFactor = StrategyResolver.EfficiencyOf(targetFamily)
            / Math.Max(0.01d, StrategyResolver.EfficiencyOf(video.Codec));

        var reference = StrategyResolver.ReferenceCrfOf(targetFamily);
        var crf = request.Quality ?? reference;
        var qualityFactor = Math.Pow(CrfStepFactor, reference - crf);

        // Tuned hardware encoding (multi-pass, lookahead, B-frames, adaptive quantisation) lands
        // within about 15% of a CPU encode rather than the 50% a GPU on its defaults would cost.
        var hardwareFactor = request.UseHardware ? 1.15d : 1d;

        if (sourceBitrate <= 0d)
        {
            // No source bitrate to anchor on; fall back to a coarse absolute model.
            var fps = video.FrameRate ?? 24f;
            var targetWidth = sourceHeight > 0 ? sourceWidth * targetHeight / sourceHeight : sourceWidth;
            var bpp = 0.09d * StrategyResolver.EfficiencyOf(targetFamily);
            return bpp * targetWidth * targetHeight * fps * qualityFactor * hardwareFactor;
        }

        var estimate = sourceBitrate * codecFactor * resolutionFactor * qualityFactor * hardwareFactor;

        // A re-encode cannot recover detail the source never had. When nothing about the target
        // asks for more bits than the source already spends, refuse to predict growth — that was
        // the bug that made every default look like it inflated the file.
        if (codecFactor <= 1d && resolutionFactor <= 1d && qualityFactor <= 1d)
        {
            estimate = Math.Min(estimate, sourceBitrate);
        }

        return Math.Max(estimate, 50_000d);
    }

    /// <summary>Predicts the combined audio bitrate of the kept tracks.</summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <returns>Estimated bits per second.</returns>
    internal static double EstimateAudioBitrate(FileAnalysis analysis, EncodeRequest request)
    {
        var requests = request.AudioTracks.ToDictionary(a => a.Index);
        var total = 0d;

        foreach (var track in analysis.Audio)
        {
            if (!requests.TryGetValue(track.Index, out var req))
            {
                req = new AudioTrackRequest { Index = track.Index, Action = AudioAction.Copy };
            }

            if (req.Action == AudioAction.Drop)
            {
                continue;
            }

            var sourceRate = (double)(track.Bitrate.Bps ?? MediaProbeService.EstimateAudioBitrate(track));

            if (req.Action == AudioAction.Copy)
            {
                total += sourceRate;
                continue;
            }

            if (string.Equals(req.Codec, "flac", StringComparison.OrdinalIgnoreCase))
            {
                var fraction = LosslessAnalyzer.FlacSizeFraction(track.Codec, track.Profile);
                total += fraction is not null ? sourceRate * fraction.Value : sourceRate;
                continue;
            }

            total += req.BitrateBps is > 0
                ? req.BitrateBps.Value
                : Math.Min(sourceRate, (track.Channels ?? 2) * 64_000d);
        }

        return total;
    }

    /// <summary>
    /// Attaches an encode-time estimate only when this server has actually measured its own
    /// throughput. Guessing produced numbers like "11h 40m" for jobs that finished far sooner,
    /// so no number is offered until there is evidence for one.
    /// </summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <param name="duration">Source duration in seconds.</param>
    /// <param name="result">The estimate being built.</param>
    private void ApplyTimeEstimate(
        FileAnalysis analysis,
        EncodeRequest request,
        double duration,
        EstimateResult result)
    {
        if (request.Video != VideoAction.Encode)
        {
            // A stream copy is I/O bound and reliably quick; this one is safe to state.
            result.EstimatedSeconds = Math.Max(5d, duration * 0.02d);
            result.TimeBasis = "stream copy";
            return;
        }

        var measured = AverageThroughput(request.UseHardware);
        if (measured is not > 0)
        {
            result.EstimatedSeconds = null;
            result.TimeBasis = "unmeasured";
            return;
        }

        var video = analysis.Video;
        var sourceHeight = video?.Height ?? 1080;
        var sourceWidth = video?.Width ?? 1920;
        var targetHeight = EncodePlanner.ResolveTargetHeight(sourceWidth, sourceHeight, request.TargetHeight)
            ?? sourceHeight;
        var targetWidth = sourceHeight > 0 ? sourceWidth * targetHeight / sourceHeight : sourceWidth;
        var fps = video?.FrameRate ?? 24f;

        var pixels = (double)targetWidth * targetHeight * fps * duration;
        result.EstimatedSeconds = pixels / measured.Value;
        result.TimeBasis = "measured on this server";
    }

    /// <summary>Average encoded pixels per second across recent completed encodes on this server.</summary>
    /// <param name="hardware">Whether to look at hardware-encoded jobs.</param>
    /// <returns>Pixels per second, or null when nothing comparable has run yet.</returns>
    private double? AverageThroughput(bool hardware)
    {
        var samples = _store.GetAll()
            .Where(j => j.Status == JobStatus.Completed
                && j.PixelsPerSecond is > 0
                && j.Request.UseHardware == hardware)
            .OrderByDescending(j => j.FinishedAt ?? j.QueuedAt)
            .Take(10)
            .Select(j => j.PixelsPerSecond!.Value)
            .ToList();

        return samples.Count == 0 ? null : samples.Average();
    }

    /// <summary>
    /// Explains a disappointing prediction rather than leaving the user to wonder why a
    /// "reduce my library" preset saved nothing.
    /// </summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <param name="result">The estimate being built.</param>
    private static void AddSavingNote(FileAnalysis analysis, EncodeRequest request, EstimateResult result)
    {
        if (request.Video != VideoAction.Encode || result.SavingFraction >= 0.10d)
        {
            return;
        }

        var video = analysis.Video;
        if (video is null)
        {
            return;
        }

        var targetFamily = StrategyResolver.CodecFamilyOf(request.VideoCodec);
        var sameFamily = string.Equals(
            StrategyResolver.CodecFamilyOf(video.Codec),
            targetFamily,
            StringComparison.OrdinalIgnoreCase);

        result.SavingNote = sameFamily && request.TargetHeight is null
            ? "This file is already " + (video.Codec ?? "efficiently").ToUpperInvariant()
                + " at this resolution, so re-encoding it to the same codec and size saves very little. "
                + "Reducing the resolution is the only change that will meaningfully shrink it."
            : "This configuration saves very little on this file. Try a lower resolution or a higher CRF.";
    }
}
