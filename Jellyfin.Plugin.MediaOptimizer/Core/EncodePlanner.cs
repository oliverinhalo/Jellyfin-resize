using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Turns a request plus an analysis into a validated ffmpeg argument vector.</summary>
public interface IEncodePlanner
{
    /// <summary>Builds a plan.</summary>
    /// <param name="analysis">The source file analysis.</param>
    /// <param name="request">What the user asked for.</param>
    /// <param name="outputPath">Where ffmpeg should write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan, including any blockers.</returns>
    Task<PlanResult> PlanAsync(FileAnalysis analysis, EncodeRequest request, string outputPath, CancellationToken cancellationToken);
}

/// <inheritdoc />
public class EncodePlanner : IEncodePlanner
{
    private readonly ICapabilityService _capabilities;
    private readonly ILogger<EncodePlanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="EncodePlanner"/> class.</summary>
    /// <param name="capabilities">Capability service.</param>
    /// <param name="logger">Logger.</param>
    public EncodePlanner(ICapabilityService capabilities, ILogger<EncodePlanner> logger)
    {
        _capabilities = capabilities;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlanResult> PlanAsync(
        FileAnalysis analysis,
        EncodeRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var warnings = new List<PlanWarning>();
        var args = new List<string>();
        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!analysis.IsEligible)
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, "INELIGIBLE", analysis.IneligibleReason ?? "This item cannot be converted."));
            return new PlanResult { Warnings = warnings };
        }

        args.Add("-i");
        args.Add(analysis.Path);

        var losslessAudioIndexes = new List<int>();
        var everythingBitExact = true;

        // ---- video ----
        var video = analysis.Video;
        var videoAction = request.Video;

        if (video is null)
        {
            videoAction = VideoAction.Drop;
        }
        else if (videoAction == VideoAction.Encode)
        {
            PlanVideoEncode(analysis, request, caps, args, warnings, ref everythingBitExact);
        }

        if (videoAction == VideoAction.Copy && video is not null)
        {
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-c:v");
            args.Add("copy");

            if (request.StripFillerData)
            {
                // Removes padding NAL units. Bit-exact for the picture data: only filler is dropped.
                var filter = video.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                        || video.Codec.Equals("h265", StringComparison.OrdinalIgnoreCase)
                    ? "filter_units=remove_types=38"
                    : "filter_units=remove_types=12";
                args.Add("-bsf:v");
                args.Add(filter);
                warnings.Add(new PlanWarning(
                    WarningLevel.Info,
                    "FILLER_STRIP",
                    "Filler data will be removed. This is bit-exact, but only saves space on sources that pad to a constant bitrate — most files contain none."));
            }
        }
        else if (videoAction == VideoAction.Drop)
        {
            args.Add("-vn");
            if (video is not null)
            {
                warnings.Add(new PlanWarning(WarningLevel.Warning, "VIDEO_DROPPED", "The video stream will not be included in the output."));
            }
        }

        // ---- audio ----
        var audioRequests = request.AudioTracks.ToDictionary(a => a.Index);
        var keptAudio = 0;
        var audioOutIndex = 0;

        foreach (var track in analysis.Audio)
        {
            if (!audioRequests.TryGetValue(track.Index, out var req))
            {
                req = new AudioTrackRequest { Index = track.Index, Action = AudioAction.Copy };
            }

            if (req.Action == AudioAction.Drop)
            {
                continue;
            }

            args.Add("-map");
            args.Add(FormattableString.Invariant($"0:{track.Index}"));

            if (req.Action == AudioAction.Copy)
            {
                args.Add(FormattableString.Invariant($"-c:a:{audioOutIndex}"));
                args.Add("copy");
            }
            else
            {
                PlanAudioEncode(track, req, audioOutIndex, args, warnings, losslessAudioIndexes, ref everythingBitExact);
            }

            audioOutIndex++;
            keptAudio++;
        }

        var droppedAudio = analysis.Audio.Count - keptAudio;
        if (droppedAudio > 0)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Info,
                "AUDIO_DROPPED",
                FormattableString.Invariant($"{droppedAudio} audio track(s) will be removed. Removing tracks is bit-exact for everything you keep and is usually the single largest saving available.")));
        }

        if (keptAudio == 0 && analysis.Audio.Count > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, "NO_AUDIO", "The output will have no audio tracks at all."));
        }

        // ---- subtitles, attachments, chapters ----
        PlanPassthrough(analysis, request, args, warnings, ref everythingBitExact);

        // ---- container ----
        var extension = NormaliseContainer(request.Container);
        if (extension == "mp4")
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        var threads = Plugin.Instance?.Configuration.EncodingThreadCount ?? 0;
        if (threads > 0)
        {
            args.Add("-threads");
            args.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-y");
        args.Add(outputPath);

        // A plan is only "lossless" if literally every operation preserved the payload.
        var isLossless = everythingBitExact && videoAction != VideoAction.Drop;

        if (request.Strategy == OptimizationStrategy.LosslessOnly && !isLossless)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Blocker,
                "NOT_LOSSLESS",
                "Lossless mode was selected but this configuration includes a lossy operation. Adjust the settings or switch to a lossy strategy."));
        }

        return new PlanResult
        {
            Arguments = args,
            Warnings = warnings,
            OutputExtension = extension,
            IsLossless = isLossless,
            LosslessAudioIndexes = losslessAudioIndexes
        };
    }

    /// <summary>
    /// Clamps a requested height to the source. Upscaling only wastes space, so the planner
    /// silently refuses it rather than producing a larger "optimised" file.
    /// </summary>
    /// <param name="sourceWidth">Source width.</param>
    /// <param name="sourceHeight">Source height.</param>
    /// <param name="targetHeight">Requested height.</param>
    /// <returns>The height to actually encode at, or null to leave the resolution alone.</returns>
    internal static int? ResolveTargetHeight(int? sourceWidth, int? sourceHeight, int? targetHeight)
    {
        if (targetHeight is not > 0 || sourceHeight is not > 0)
        {
            return null;
        }

        if (targetHeight.Value >= sourceHeight.Value)
        {
            return null;
        }

        // Keep the encoded height even; -2 on the width axis keeps the aspect ratio and mod-2 width.
        return targetHeight.Value % 2 == 0 ? targetHeight.Value : targetHeight.Value - 1;
    }

    /// <summary>Maps a target bit depth onto an ffmpeg pixel format.</summary>
    /// <param name="bitDepth">8 or 10.</param>
    /// <param name="sourcePixelFormat">The source pixel format, used to preserve chroma subsampling.</param>
    /// <returns>A pixel format name.</returns>
    internal static string PixelFormatFor(int bitDepth, string? sourcePixelFormat)
    {
        var chroma = "420";
        if (!string.IsNullOrEmpty(sourcePixelFormat))
        {
            if (sourcePixelFormat.Contains("444", StringComparison.Ordinal))
            {
                chroma = "444";
            }
            else if (sourcePixelFormat.Contains("422", StringComparison.Ordinal))
            {
                chroma = "422";
            }
        }

        return bitDepth >= 10
            ? FormattableString.Invariant($"yuv{chroma}p10le")
            : FormattableString.Invariant($"yuv{chroma}p");
    }

    private static string NormaliseContainer(string container)
    {
        var c = container.Trim().TrimStart('.').ToLowerInvariant();
        return c is "mkv" or "matroska" ? "mkv" : c is "mp4" or "m4v" ? "mp4" : "mkv";
    }

    private void PlanVideoEncode(
        FileAnalysis analysis,
        EncodeRequest request,
        Capabilities caps,
        List<string> args,
        List<PlanWarning> warnings,
        ref bool everythingBitExact)
    {
        var video = analysis.Video!;
        var encoder = request.VideoCodec;

        if (string.IsNullOrEmpty(encoder))
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, "NO_ENCODER", "No video encoder was selected."));
            return;
        }

        var option = caps.VideoEncoders.FirstOrDefault(e => string.Equals(e.Name, encoder, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Blocker,
                "ENCODER_UNAVAILABLE",
                FormattableString.Invariant($"This server's ffmpeg does not provide the encoder '{encoder}'.")));
            return;
        }

        if (option.Codec == "hevc" && !caps.AllowHevcEncoding)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "HEVC_DISABLED_SERVER",
                "HEVC encoding is switched off in this server's transcoding settings. The conversion will still run, but check Dashboard → Playback if it fails."));
        }

        if (option.Codec == "av1" && !caps.AllowAv1Encoding)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "AV1_DISABLED_SERVER",
                "AV1 encoding is switched off in this server's transcoding settings. The conversion will still run, but check Dashboard → Playback if it fails."));
        }

        // Dolby Vision: the RPU cannot survive an ffmpeg re-encode, so this is a hard stop
        // unless the user has explicitly accepted losing it.
        if (video.IsDolbyVision && !request.AcceptDolbyVisionLoss)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Blocker,
                "DOLBY_VISION_LOSS",
                "This file carries Dolby Vision. FFmpeg cannot re-inject the Dolby Vision RPU, so re-encoding the video would leave it playing with badly wrong colour on Dolby Vision displays. Either keep the video stream unchanged and optimise audio only, or explicitly accept conversion to HDR10."));
        }
        else if (video.IsDolbyVision)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "DOLBY_VISION_ACCEPTED",
                "Dolby Vision metadata will be discarded. The output keeps the HDR10 base layer only."));
        }

        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-c:v");
        args.Add(option.Name);

        // Resolution.
        var height = ResolveTargetHeight(video.Width, video.Height, request.TargetHeight);
        if (request.TargetHeight is > 0 && height is null && video.Height is > 0 && request.TargetHeight.Value > video.Height.Value)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Info,
                "NO_UPSCALE",
                FormattableString.Invariant($"The source is {video.Height.Value}p, so it will not be upscaled to {request.TargetHeight.Value}p. The original resolution is kept.")));
        }

        var filters = new List<string>();
        if (height is not null)
        {
            var width = request.TargetWidth is > 0
                ? request.TargetWidth.Value.ToString(CultureInfo.InvariantCulture)
                : "-2";
            filters.Add(FormattableString.Invariant($"scale={width}:{height.Value}:flags=lanczos"));
        }

        // VAAPI encoders only accept frames already on the GPU, so the chain has to end with a
        // format conversion and an upload. Without this every VAAPI job fails at startup.
        if (option.Name.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            var device = caps.VaapiDevice;
            if (!string.IsNullOrEmpty(device))
            {
                args.Insert(0, device);
                args.Insert(0, "-vaapi_device");
            }

            filters.Add("format=nv12");
            filters.Add("hwupload");
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(',', filters));
        }

        // Bit depth.
        var bitDepth = request.BitDepth ?? video.BitDepth ?? 8;
        if (bitDepth >= 10 && !option.Supports10Bit)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "NO_10BIT",
                FormattableString.Invariant($"{option.DisplayName} cannot produce 10-bit output on this server. Falling back to 8-bit.")));
            bitDepth = 8;
        }

        if ((video.BitDepth ?? 8) > bitDepth)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "BIT_DEPTH_REDUCED",
                FormattableString.Invariant($"Reducing {video.BitDepth}-bit to {bitDepth}-bit can introduce banding, especially in HDR content.")));
        }

        args.Add("-pix_fmt");
        args.Add(PixelFormatFor(bitDepth, video.PixelFormat));

        // Frame rate: never force CFR silently. Forcing it on VFR sources desynchronises audio.
        args.Add("-fps_mode");
        args.Add("passthrough");

        PlanRateControl(request, option, ComputeTargetVideoBitrate(analysis, request), args, warnings);

        if (!string.IsNullOrEmpty(request.Preset) && option.Presets.Count > 0)
        {
            args.Add("-preset");
            args.Add(request.Preset);
        }

        PlanHdr(video, option, args, warnings);

        if (video.IsVariableFrameRate)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Info,
                "VFR_SOURCE",
                "This source appears to be variable frame rate. The frame timing is passed through unchanged to avoid audio drift."));
        }

        // A lossless re-encode reproduces the decoded pixels exactly, so it is genuinely
        // bit-exact -- but only worth doing from a lossless source. Re-encoding an
        // already-compressed stream "losslessly" stores the decoded frames, which carry far
        // more entropy than the bitstream they came from, and the result is several times
        // larger. Offering that would be a trap, so it is refused rather than warned about.
        if (request.RateControl == RateControlMode.Lossless)
        {
            if (!video.IsLosslessCodec)
            {
                warnings.Add(new PlanWarning(
                    WarningLevel.Blocker,
                    "LOSSLESS_FROM_LOSSY",
                    FormattableString.Invariant($"This file's video is {video.Codec}, which is already lossy. Re-encoding it losslessly would preserve the decoded picture exactly but produce a file several times larger, not smaller. Copy the video stream instead, or pick a lossy quality setting.")));
            }

            if (request.TargetHeight is not null)
            {
                warnings.Add(new PlanWarning(
                    WarningLevel.Blocker,
                    "LOSSLESS_RESCALE",
                    "Rescaling discards picture information, so it cannot be combined with lossless encoding."));
            }
        }
        else
        {
            everythingBitExact = false;
        }
    }

    private static void PlanRateControl(
        EncodeRequest request,
        EncoderOption option,
        long? targetVideoBitrate,
        List<string> args,
        List<PlanWarning> warnings)
    {
        switch (request.RateControl)
        {
            case RateControlMode.ConstantQuality:
                var quality = request.Quality ?? DefaultQualityFor(option.Codec);
                if (option.IsHardware)
                {
                    // Hardware encoders spell constant quality differently per vendor.
                    if (option.Name.EndsWith("_nvenc", StringComparison.Ordinal))
                    {
                        args.Add("-rc");
                        args.Add("constqp");
                        args.Add("-qp");
                        args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (option.Name.EndsWith("_qsv", StringComparison.Ordinal))
                    {
                        args.Add("-global_quality");
                        args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (option.Name.EndsWith("_vaapi", StringComparison.Ordinal))
                    {
                        args.Add("-rc_mode");
                        args.Add("CQP");
                        args.Add("-qp");
                        args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (option.Name.EndsWith("_videotoolbox", StringComparison.Ordinal))
                    {
                        args.Add("-q:v");
                        args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        args.Add("-qp");
                        args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    }

                    warnings.Add(new PlanWarning(
                        WarningLevel.Info,
                        "HARDWARE_QUALITY",
                        "Hardware encoding is much faster but produces a noticeably larger file than a CPU encode at the same visual quality."));
                }
                else
                {
                    args.Add("-crf");
                    args.Add(quality.ToString(CultureInfo.InvariantCulture));
                }

                break;

            case RateControlMode.AverageBitrate:
                if (request.VideoBitrateBps is not > 0)
                {
                    warnings.Add(new PlanWarning(WarningLevel.Blocker, "NO_BITRATE", "Average bitrate mode needs a target bitrate."));
                    return;
                }

                args.Add("-b:v");
                args.Add(FormattableString.Invariant($"{request.VideoBitrateBps.Value}"));
                break;

            case RateControlMode.TargetSize:
                if (targetVideoBitrate is not > 0)
                {
                    warnings.Add(new PlanWarning(
                        WarningLevel.Blocker,
                        "NO_TARGET_SIZE",
                        "Target-size mode needs a size large enough to leave room for the audio tracks."));
                    return;
                }

                args.Add("-b:v");
                args.Add(FormattableString.Invariant($"{targetVideoBitrate.Value}"));
                args.Add("-maxrate");
                args.Add(FormattableString.Invariant($"{(long)(targetVideoBitrate.Value * 1.5d)}"));
                args.Add("-bufsize");
                args.Add(FormattableString.Invariant($"{targetVideoBitrate.Value * 2}"));
                break;

            case RateControlMode.Lossless:
                if (option.Name == "libx265")
                {
                    args.Add("-x265-params");
                    args.Add("lossless=1");
                }
                else if (option.Name == "libx264")
                {
                    args.Add("-qp");
                    args.Add("0");
                }
                else if (option.Name == "ffv1")
                {
                    args.Add("-level");
                    args.Add("3");
                }

                break;

            default:
                break;
        }
    }

    private static void PlanHdr(
        VideoTrackInfo video,
        EncoderOption option,
        List<string> args,
        List<PlanWarning> warnings)
    {
        var isHdr = video.Range.StartsWith("HDR", StringComparison.OrdinalIgnoreCase)
            || video.Range.StartsWith("Dolby", StringComparison.OrdinalIgnoreCase);

        if (!isHdr && !string.Equals(video.Range, "HLG", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Colour tags must be carried explicitly or the output plays washed out.
        if (!string.IsNullOrEmpty(video.ColorPrimaries))
        {
            args.Add("-color_primaries");
            args.Add(video.ColorPrimaries);
        }

        if (!string.IsNullOrEmpty(video.ColorTransfer))
        {
            args.Add("-color_trc");
            args.Add(video.ColorTransfer);
        }

        if (!string.IsNullOrEmpty(video.ColorSpace))
        {
            args.Add("-colorspace");
            args.Add(video.ColorSpace);
        }

        if (option.Name == "libx265")
        {
            args.Add("-x265-params");
            args.Add("hdr10=1:repeat-headers=1");
        }
        else if (option.Codec == "av1" || option.IsHardware)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "HDR_METADATA_RISK",
                FormattableString.Invariant($"{option.DisplayName} has weaker HDR metadata support than x265. Colour tags are carried over, but mastering-display and content-light-level metadata may not survive. x265 is the safer choice for HDR sources.")));
        }

        if (string.Equals(video.RangeType, "HDR10Plus", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "HDR10PLUS_LOSS",
                "HDR10+ dynamic metadata will not survive this conversion. The output keeps static HDR10 only."));
        }
    }

    private static void PlanAudioEncode(
        AudioTrackInfo track,
        AudioTrackRequest req,
        int outIndex,
        List<string> args,
        List<PlanWarning> warnings,
        List<int> losslessAudioIndexes,
        ref bool everythingBitExact)
    {
        var codec = req.Codec ?? "libopus";
        args.Add(FormattableString.Invariant($"-c:a:{outIndex}"));
        args.Add(codec);

        var isFlac = codec.Equals("flac", StringComparison.OrdinalIgnoreCase);
        var bitExact = isFlac && track.IsLossless && (req.Channels is null || req.Channels == track.Channels);

        if (bitExact)
        {
            losslessAudioIndexes.Add(track.Index);

            if (track.HasObjectAudio && !string.IsNullOrEmpty(track.Profile))
            {
                warnings.Add(new PlanWarning(
                    WarningLevel.Warning,
                    "OBJECT_AUDIO_LOSS",
                    FormattableString.Invariant($"Track {track.TypeIndex + 1} ({track.Profile}) carries object audio. FLAC keeps the lossless channel bed bit-for-bit but discards the height objects — this is lossless for the bed and a real downgrade for an Atmos or DTS:X setup.")));
            }

            if (track.Channels is > 8)
            {
                warnings.Add(new PlanWarning(
                    WarningLevel.Blocker,
                    "FLAC_CHANNEL_LIMIT",
                    FormattableString.Invariant($"FLAC supports at most 8 channels; this track has {track.Channels}.")));
            }
        }
        else
        {
            everythingBitExact = false;

            if (isFlac && !track.IsLossless)
            {
                warnings.Add(new PlanWarning(
                    WarningLevel.Warning,
                    "FLAC_FROM_LOSSY",
                    FormattableString.Invariant($"Track {track.TypeIndex + 1} is {track.Codec}, which is already lossy. Encoding it to FLAC preserves the decoded audio exactly but will usually make the file considerably larger, not smaller.")));
            }
        }

        if (req.BitrateBps is > 0 && !isFlac)
        {
            args.Add(FormattableString.Invariant($"-b:a:{outIndex}"));
            args.Add(FormattableString.Invariant($"{req.BitrateBps.Value}"));
        }

        if (req.Channels is > 0 && req.Channels != track.Channels)
        {
            args.Add(FormattableString.Invariant($"-ac:a:{outIndex}"));
            args.Add(req.Channels.Value.ToString(CultureInfo.InvariantCulture));
            warnings.Add(new PlanWarning(
                WarningLevel.Info,
                "AUDIO_DOWNMIX",
                FormattableString.Invariant($"Track {track.TypeIndex + 1} will be downmixed from {track.Channels} to {req.Channels.Value} channels.")));
        }
    }

    private static void PlanPassthrough(
        FileAnalysis analysis,
        EncodeRequest request,
        List<string> args,
        List<PlanWarning> warnings,
        ref bool everythingBitExact)
    {
        var container = NormaliseContainer(request.Container);
        var keep = request.KeepSubtitleIndexes;
        var embedded = analysis.Subtitles.Where(s => !s.IsExternal).ToList();

        var incompatible = 0;
        foreach (var sub in embedded)
        {
            if (keep is not null && !keep.Contains(sub.Index))
            {
                continue;
            }

            if (container == "mp4" && sub.IsGraphical)
            {
                incompatible++;
                continue;
            }

            args.Add("-map");
            args.Add(FormattableString.Invariant($"0:{sub.Index}"));
        }

        // One message for the whole set: a Blu-ray rip can carry a dozen image-based tracks and
        // a warning each would drown out everything that matters.
        if (incompatible > 0)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Warning,
                "SUBTITLE_INCOMPATIBLE",
                FormattableString.Invariant($"MP4 cannot store image-based subtitles, so {incompatible} track(s) will be dropped. Switch the container to MKV to keep them.")));
        }

        if (embedded.Count > 0)
        {
            args.Add("-c:s");
            args.Add("copy");
        }

        if (keep is not null && embedded.Count > keep.Count)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Info,
                "SUBTITLES_DROPPED",
                FormattableString.Invariant($"{embedded.Count - keep.Count} subtitle track(s) will be removed.")));
        }

        if (request.KeepAttachments && analysis.AttachmentCount > 0)
        {
            if (container == "mkv")
            {
                args.Add("-map");
                args.Add("0:t?");
                args.Add("-c:t");
                args.Add("copy");
            }
            else
            {
                warnings.Add(new PlanWarning(
                    WarningLevel.Warning,
                    "ATTACHMENTS_LOST",
                    FormattableString.Invariant($"MP4 cannot carry the {analysis.AttachmentCount} embedded attachment(s). Subtitle fonts will be lost — choose MKV to keep them.")));
                everythingBitExact = false;
            }
        }
        else if (!request.KeepAttachments && analysis.AttachmentCount > 0)
        {
            warnings.Add(new PlanWarning(
                WarningLevel.Info,
                "ATTACHMENTS_DROPPED",
                "Embedded attachments will be removed. If this file uses ASS subtitles, their fonts go with them."));
        }

        args.Add("-map_metadata");
        args.Add("0");

        if (request.KeepChapters)
        {
            args.Add("-map_chapters");
            args.Add("0");
        }
        else
        {
            args.Add("-map_chapters");
            args.Add("-1");
        }
    }

    /// <summary>
    /// Converts a requested output size into a video bitrate, leaving room for the audio that
    /// will be muxed alongside it and a small allowance for container overhead.
    /// </summary>
    /// <param name="analysis">Source analysis.</param>
    /// <param name="request">Requested settings.</param>
    /// <returns>Bits per second for the video stream, or null when the target leaves no room.</returns>
    internal static long? ComputeTargetVideoBitrate(FileAnalysis analysis, EncodeRequest request)
    {
        if (request.TargetSizeBytes is not > 0 || analysis.DurationSeconds is not > 0)
        {
            return null;
        }

        var totalBits = request.TargetSizeBytes.Value * 8d * 0.99d;
        var audioBits = SizeEstimator.EstimateAudioBitrate(analysis, request) * analysis.DurationSeconds.Value;
        var videoBits = totalBits - audioBits;

        if (videoBits <= 0d)
        {
            return null;
        }

        var bitrate = (long)(videoBits / analysis.DurationSeconds.Value);
        return bitrate > 1000 ? bitrate : null;
    }

    private static int DefaultQualityFor(string codec) => codec switch
    {
        "hevc" => 28,
        "av1" => 32,
        "vp9" => 31,
        _ => 23
    };
}
