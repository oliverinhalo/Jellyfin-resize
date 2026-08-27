using System;
using System.Collections.Generic;
using System.Linq;
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
    // Bits per pixel per frame at each codec's reference CRF, from typical live-action encodes.
    // Deliberately coarse: the result is always presented as a range, never a precise number.
    private static readonly Dictionary<string, (int RefCrf, double Bpp)> CodecBaselines =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["h264"] = (23, 0.090d),
            ["hevc"] = (28, 0.045d),
            ["av1"] = (32, 0.035d),
            ["vp9"] = (31, 0.042d)
        };

    // Each CRF step changes bitrate by roughly this factor.
    private const double CrfStepFactor = 1.12d;

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
            return result;
        }

        var videoBits = EstimateVideoBitrate(analysis, request) * duration;
        var audioBits = EstimateAudioBitrate(analysis, request) * duration;

        // Container overhead is around 1% for both MKV and MP4 at these bitrates.
        var totalBytes = (long)((videoBits + audioBits) / 8d * 1.01d);
        totalBytes = Math.Max(totalBytes, 1024);

        result.EstimatedSizeBytes = totalBytes;

        // A stream-copy of everything is predictable; a re-encode is not.
        var uncertainty = request.Video == VideoAction.Encode ? 0.25d : 0.03d;
        result.EstimatedSizeLowBytes = (long)(totalBytes * (1d - uncertainty));
        result.EstimatedSizeHighBytes = (long)(totalBytes * (1d + uncertainty));

        if (result.CurrentSizeBytes > 0)
        {
            result.SavingFraction = 1d - ((double)totalBytes / result.CurrentSizeBytes);
        }

        result.EstimatedSeconds = EstimateEncodeSeconds(analysis, request, duration);

        return result;
    }

    /// <summary>
    /// Predicts the video bitrate the encoder will land on. Constant-quality modes have no
    /// knowable output size in advance, so this is a bits-per-pixel model, not a measurement.
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

        if (request.RateControl == RateControlMode.TargetSize
            && request.TargetSizeBytes is > 0
            && analysis.DurationSeconds is > 0)
        {
            return request.TargetSizeBytes.Value * 8d / analysis.DurationSeconds.Value;
        }

        if (request.RateControl == RateControlMode.Lossless)
        {
            // Lossless re-encoding only makes sense from a lossless source, where roughly
            // half the size is typical for FFV1/x265-lossless over uncompressed or FFV1 input.
            return sourceBitrate * 0.5d;
        }

        var width = video.Width ?? 1920;
        var height = video.Height ?? 1080;
        var targetHeight = EncodePlanner.ResolveTargetHeight(width, height, request.TargetHeight);
        if (targetHeight is not null && height > 0)
        {
            var scale = (double)targetHeight.Value / height;
            width = (int)(width * scale);
            height = targetHeight.Value;
        }

        var fps = video.FrameRate ?? 24f;
        var codec = CodecFamilyOf(request.VideoCodec);
        if (!CodecBaselines.TryGetValue(codec, out var baseline))
        {
            baseline = CodecBaselines["h264"];
        }

        var crf = request.Quality ?? baseline.RefCrf;
        var bpp = baseline.Bpp * Math.Pow(CrfStepFactor, baseline.RefCrf - crf);

        // 10-bit encoding costs a little more bitrate at the same CRF, but compresses
        // slightly better on gradients; treat it as roughly neutral with a small penalty.
        if ((request.BitDepth ?? video.BitDepth ?? 8) >= 10)
        {
            bpp *= 1.05d;
        }

        var estimate = bpp * width * height * fps;

        // Hardware encoders need meaningfully more bitrate for the same perceived quality.
        if (request.UseHardware)
        {
            estimate *= 1.5d;
        }

        // A re-encode that lands above the source bitrate would be pointless; the encoder
        // will not invent detail that is not there, so cap at the source rate when known.
        if (sourceBitrate > 0d && estimate > sourceBitrate && targetHeight is null)
        {
            estimate = sourceBitrate;
        }

        return estimate;
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

            if (req.BitrateBps is > 0)
            {
                total += req.BitrateBps.Value;
                continue;
            }

            total += Math.Min(sourceRate, (track.Channels ?? 2) * 64_000d);
        }

        return total;
    }

    private static string CodecFamilyOf(string? encoder)
    {
        if (string.IsNullOrEmpty(encoder))
        {
            return "h264";
        }

        if (encoder.Contains("265", StringComparison.OrdinalIgnoreCase)
            || encoder.Contains("hevc", StringComparison.OrdinalIgnoreCase))
        {
            return "hevc";
        }

        if (encoder.Contains("av1", StringComparison.OrdinalIgnoreCase))
        {
            return "av1";
        }

        if (encoder.Contains("vp9", StringComparison.OrdinalIgnoreCase))
        {
            return "vp9";
        }

        return "h264";
    }

    private static double? EstimateEncodeSeconds(FileAnalysis analysis, EncodeRequest request, double duration)
    {
        if (request.Video != VideoAction.Encode)
        {
            // Stream copy is I/O bound; roughly a hundredth of realtime on any modern disk.
            return duration * 0.01d;
        }

        if (request.UseHardware)
        {
            return duration / 8d;
        }

        // Very rough CPU speed multipliers relative to realtime at 1080p.
        var codec = CodecFamilyOf(request.VideoCodec);
        var speed = codec switch
        {
            "hevc" => 0.6d,
            "av1" => 0.35d,
            "vp9" => 0.4d,
            _ => 1.5d
        };

        var height = EncodePlanner.ResolveTargetHeight(
            analysis.Video?.Width,
            analysis.Video?.Height,
            request.TargetHeight) ?? analysis.Video?.Height ?? 1080;

        // Cost scales with pixel count against the 1080p baseline.
        var pixelFactor = Math.Pow((double)height / 1080d, 2d);
        if (pixelFactor > 0d)
        {
            speed /= pixelFactor;
        }

        return speed > 0d ? duration / speed : null;
    }
}
