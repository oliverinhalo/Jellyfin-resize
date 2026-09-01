using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Models;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>
/// Turns a named strategy plus a file's actual properties into concrete encode settings.
/// <para>
/// This lives on the server so the dialog, the batch runner and the estimate all agree. A preset
/// resolved here is a normal <see cref="EncodeRequest"/> the user can still edit.
/// </para>
/// </summary>
public static class StrategyResolver
{
    /// <summary>Standard resolution rungs, tallest first. "One step down" means the next rung.</summary>
    private static readonly int[] Ladder = [4320, 2160, 1440, 1080, 720, 480];

    /// <summary>
    /// Roughly how much bitrate each codec needs for the same perceived quality, relative to
    /// H.264. Lower is more efficient. Used both to pick a codec and to predict the saving.
    /// </summary>
    private static readonly Dictionary<string, double> Efficiency = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mpeg2video"] = 2.2d,
        ["mpeg4"] = 1.6d,
        ["msmpeg4v3"] = 1.7d,
        ["vc1"] = 1.2d,
        ["h264"] = 1.0d,
        ["vp8"] = 1.0d,
        ["vp9"] = 0.65d,
        ["hevc"] = 0.62d,
        ["h265"] = 0.62d,
        ["av1"] = 0.50d
    };

    /// <summary>The CRF at which each codec produces roughly "normal" quality for its family.</summary>
    private static readonly Dictionary<string, int> ReferenceCrf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = 23,
        ["hevc"] = 28,
        ["av1"] = 32,
        ["vp9"] = 31
    };

    /// <summary>Gets how efficient a codec is relative to H.264. Unknown codecs are treated as H.264.</summary>
    /// <param name="codec">Codec name.</param>
    /// <returns>A relative bitrate multiplier.</returns>
    public static double EfficiencyOf(string? codec) =>
        codec is not null && Efficiency.TryGetValue(codec, out var value) ? value : 1.0d;

    /// <summary>Gets the reference CRF for a codec family.</summary>
    /// <param name="codec">Codec family, e.g. hevc.</param>
    /// <returns>The reference CRF.</returns>
    public static int ReferenceCrfOf(string? codec) =>
        codec is not null && ReferenceCrf.TryGetValue(codec, out var value) ? value : 23;

    /// <summary>Maps an encoder name onto its codec family.</summary>
    /// <param name="encoder">Encoder name such as libx265 or hevc_nvenc.</param>
    /// <returns>The codec family.</returns>
    public static string CodecFamilyOf(string? encoder)
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

    /// <summary>
    /// Returns the next rung down from a source height, or null when the source is already at or
    /// below the bottom of the ladder.
    /// </summary>
    /// <param name="sourceHeight">The source height in pixels.</param>
    /// <returns>The target height, or null to leave the resolution alone.</returns>
    public static int? OneStepDown(int? sourceHeight)
    {
        if (sourceHeight is not > 0)
        {
            return null;
        }

        // Match the rung the source sits on, allowing for slightly-off heights such as 2156.
        foreach (var rung in Ladder)
        {
            if (sourceHeight.Value >= rung - 40)
            {
                var index = Array.IndexOf(Ladder, rung);
                return index >= 0 && index + 1 < Ladder.Length ? Ladder[index + 1] : null;
            }
        }

        return null;
    }

    /// <summary>Picks the most efficient video encoder this server can actually use.</summary>
    /// <param name="caps">Discovered capabilities.</param>
    /// <param name="allowAv1">Whether AV1 may be chosen. It is very slow on CPU.</param>
    /// <param name="preferHardware">Whether to pick a GPU encoder when one is available.</param>
    /// <returns>An encoder name, or null when none is available.</returns>
    public static string? PickEncoder(Capabilities caps, bool allowAv1, bool preferHardware = false)
    {
        if (preferHardware)
        {
            // Hardware encoding is the single biggest speed lever available: typically ten to
            // twenty times faster than x265 on the same machine.
            var hardware = caps.VideoEncoders.Where(e => e.IsHardware).ToList();
            var pick = hardware.FirstOrDefault(e => e.Codec == "hevc")
                ?? hardware.FirstOrDefault(e => e.Codec == "av1")
                ?? hardware.FirstOrDefault(e => e.Codec == "h264");

            if (pick is not null)
            {
                return pick.Name;
            }
        }

        var software = caps.VideoEncoders.Where(e => !e.IsHardware && e.Codec != "ffv1").ToList();

        if (allowAv1 && caps.AllowAv1Encoding)
        {
            var av1 = software.FirstOrDefault(e => e.Name == "libsvtav1");
            if (av1 is not null)
            {
                return av1.Name;
            }
        }

        return software.FirstOrDefault(e => e.Codec == "hevc")?.Name
            ?? software.FirstOrDefault(e => e.Codec == "h264")?.Name
            ?? software.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// Drops audio and subtitle tracks the user has said they will never use. Always leaves at
    /// least one audio track behind: a file with no audio is worse than a file with the wrong
    /// language in it.
    /// </summary>
    /// <param name="analysis">The source file.</param>
    /// <param name="request">The request being built.</param>
    /// <param name="config">Plugin configuration.</param>
    internal static void ApplyTrackFilters(FileAnalysis analysis, EncodeRequest request, PluginConfiguration config)
    {
        var keepAudio = LanguageMatcher.ParseList(config.KeepAudioLanguages);
        var keepSubs = LanguageMatcher.ParseList(config.KeepSubtitleLanguages);

        if (keepAudio.Count > 0 || config.DropCommentaryTracks)
        {
            var survivors = new List<AudioTrackRequest>();

            foreach (var track in request.AudioTracks)
            {
                var source = analysis.Audio.FirstOrDefault(a => a.Index == track.Index);
                if (source is null)
                {
                    survivors.Add(track);
                    continue;
                }

                var keep = LanguageMatcher.ShouldKeep(source.Language, keepAudio, config.KeepUntaggedTracks);

                if (keep && config.DropCommentaryTracks && IsCommentary(source.Title))
                {
                    keep = false;
                }

                if (keep)
                {
                    survivors.Add(track);
                }
                else
                {
                    track.Action = AudioAction.Drop;
                }
            }

            // Nothing survived the filter: keep the default track, or the first one.
            if (survivors.Count == 0 && request.AudioTracks.Count > 0)
            {
                var fallbackIndex = analysis.Audio.FirstOrDefault(a => a.IsDefault)?.Index
                    ?? analysis.Audio.FirstOrDefault()?.Index;

                var fallback = request.AudioTracks.FirstOrDefault(t => t.Index == fallbackIndex)
                    ?? request.AudioTracks[0];
                fallback.Action = AudioAction.Copy;
            }
        }

        if (keepSubs.Count > 0)
        {
            request.KeepSubtitleIndexes = analysis.Subtitles
                .Where(sub => !sub.IsExternal
                    && LanguageMatcher.ShouldKeep(sub.Language, keepSubs, config.KeepUntaggedTracks))
                .Select(sub => sub.Index)
                .ToList();
        }
    }

    /// <summary>
    /// Detects commentary and audio-description tracks from their title, which is the only place
    /// most containers record it.
    /// </summary>
    /// <param name="title">The track title.</param>
    /// <returns>Whether the track looks like commentary.</returns>
    internal static bool IsCommentary(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string[] markers = ["commentary", "commentaries", "director", "audio description", "descriptive", "visually impaired"];
        return markers.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds concrete settings for a strategy against one file.</summary>
    /// <param name="analysis">The source file.</param>
    /// <param name="strategy">The chosen strategy.</param>
    /// <param name="caps">Server capabilities.</param>
    /// <param name="config">Plugin configuration, for the default container and output policy.</param>
    /// <returns>A fully populated request.</returns>
    public static EncodeRequest Resolve(
        FileAnalysis analysis,
        OptimizationStrategy strategy,
        Capabilities caps,
        PluginConfiguration config)
    {
        var resolved = ResolveCore(analysis, strategy, caps, config);
        ApplyContainerCompatibility(analysis, resolved);
        return resolved;
    }

    /// <summary>
    /// Moves the output to a container that can actually hold what the file has.
    /// <para>
    /// This runs last, once the audio actions are final, because whether MP4 is viable depends on
    /// whether a track is being copied or re-encoded. Picking MP4 for a Blu-ray remux is not a
    /// cosmetic mistake: FFmpeg refuses to write the header and the job fails having encoded
    /// nothing.
    /// </para>
    /// </summary>
    /// <param name="analysis">The source file.</param>
    /// <param name="request">The request to adjust in place.</param>
    internal static void ApplyContainerCompatibility(FileAnalysis analysis, EncodeRequest request)
    {
        request.ContainerSwitchReason = null;

        if (ContainerCompatibility.Normalise(request.Container) == "mkv")
        {
            return;
        }

        var asked = ContainerCompatibility.Normalise(request.Container).ToUpperInvariant();

        // Attachments (subtitle fonts) and image-based subtitles are Matroska-only.
        var graphical = analysis.Subtitles.Count(s => s.IsGraphical && !s.IsExternal);
        if (graphical > 0 || analysis.AttachmentCount > 0)
        {
            var what = graphical > 0 ? "image-based subtitles" : "embedded subtitle fonts";
            request.Container = "mkv";
            request.ContainerSwitchReason = FormattableString.Invariant(
                $"Writing MKV instead of {asked}, because {asked} cannot store this file's {what} and they would have been lost. Nothing is dropped this way.");
            return;
        }

        var audioRequests = request.AudioTracks.ToDictionary(a => a.Index);
        foreach (var track in analysis.Audio)
        {
            if (!audioRequests.TryGetValue(track.Index, out var req) || req.Action == AudioAction.Drop)
            {
                continue;
            }

            var outgoing = req.Action == AudioAction.Copy ? track.Codec : req.Codec;
            if (!ContainerCompatibility.CanCopyAudio(request.Container, outgoing))
            {
                // Keeping the lossless track intact beats silently transcoding it away.
                request.Container = "mkv";
                request.ContainerSwitchReason = FormattableString.Invariant(
                    $"Writing MKV instead of {asked}, because {asked} has no way to store {outgoing?.ToUpperInvariant()} audio. The track is kept exactly as it is rather than being re-encoded.");
                return;
            }
        }
    }

    private static EncodeRequest ResolveCore(
        FileAnalysis analysis,
        OptimizationStrategy strategy,
        Capabilities caps,
        PluginConfiguration config)
    {
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = strategy,
            Container = config.DefaultContainer,
            OutputPolicy = config.DefaultOutputPolicy,
            KeepAttachments = true,
            KeepChapters = true,
            AudioTracks = analysis.Audio
                .Select(a => new AudioTrackRequest { Index = a.Index, Action = AudioAction.Copy })
                .ToList(),
            KeepAudioLanguages = config.KeepAudioLanguages,
            KeepSubtitleLanguages = config.KeepSubtitleLanguages
        };

        ApplyTrackFilters(analysis, request, config);

        if (strategy == OptimizationStrategy.LosslessOnly)
        {
            ResolveLossless(analysis, request);
            return request;
        }

        var encoder = PickEncoder(
            caps,
            allowAv1: strategy == OptimizationStrategy.HighReduction && config.Speed == SpeedPreference.SmallestFile,
            preferHardware: config.PreferHardwareEncoding);
        if (encoder is null || analysis.Video is null)
        {
            request.Video = VideoAction.Copy;
            return request;
        }

        request.Video = VideoAction.Encode;
        request.VideoCodec = encoder;
        request.RateControl = RateControlMode.ConstantQuality;
        request.BitDepth = analysis.Video.BitDepth ?? 8;
        request.UseHardware = caps.VideoEncoders.Any(e => e.Name == encoder && e.IsHardware);

        var family = CodecFamilyOf(encoder);
        var reference = ReferenceCrfOf(family);
        request.Preset = DefaultPresetFor(encoder, caps, config.Speed);

        switch (strategy)
        {
            case OptimizationStrategy.Standard:
                // Same resolution; the saving comes from the codec being more efficient.
                request.Quality = reference;
                break;

            case OptimizationStrategy.Medium:
                request.TargetHeight = OneStepDown(analysis.Video.Height);
                request.Quality = reference;
                TrimHeavyAudio(analysis, request, caps, 256_000);
                break;

            case OptimizationStrategy.HighReduction:
                request.TargetHeight = OneStepDown(analysis.Video.Height);
                // Three CRF steps above the reference is roughly a 30% bitrate cut.
                request.Quality = reference + 3;
                TrimHeavyAudio(analysis, request, caps, 128_000);
                break;

            default:
                request.Quality = reference;
                break;
        }

        return request;
    }

    /// <summary>
    /// Picks the strategy that gives this particular file a worthwhile saving, so the dialog does
    /// not open on an option that would do nothing. A file that is already efficiently encoded at
    /// its resolution can only be shrunk by reducing the resolution.
    /// </summary>
    /// <param name="analysis">The source file.</param>
    /// <param name="caps">Server capabilities.</param>
    /// <returns>The strategy to preselect.</returns>
    public static OptimizationStrategy Recommend(FileAnalysis analysis, Capabilities caps)
    {
        var video = analysis.Video;
        if (video is null)
        {
            return OptimizationStrategy.Standard;
        }

        var encoder = PickEncoder(caps, allowAv1: false);
        var targetFamily = CodecFamilyOf(encoder);

        var sourceEfficiency = EfficiencyOf(video.Codec);
        var targetEfficiency = EfficiencyOf(targetFamily);

        // A codec change that buys at least 15% is worth doing on its own.
        if (targetEfficiency < sourceEfficiency * 0.85d)
        {
            return OptimizationStrategy.Standard;
        }

        // Otherwise the only real lever is resolution.
        return OneStepDown(video.Height) is not null
            ? OptimizationStrategy.Medium
            : OptimizationStrategy.Standard;
    }

    /// <summary>
    /// Picks an encoder preset for the requested speed. The preset is the second biggest lever
    /// after hardware encoding: x265 "veryfast" runs several times quicker than "medium" for a
    /// file perhaps 10% larger.
    /// </summary>
    /// <param name="encoder">Encoder name.</param>
    /// <param name="caps">Server capabilities.</param>
    /// <param name="speed">How much time to trade for size.</param>
    /// <returns>A preset name, or null when the encoder takes none.</returns>
    internal static string? DefaultPresetFor(string encoder, Capabilities caps, SpeedPreference speed)
    {
        var option = caps.VideoEncoders.FirstOrDefault(e => e.Name == encoder);
        if (option is null || option.Presets.Count == 0)
        {
            return null;
        }

        if (option.Name == "libsvtav1")
        {
            return speed switch
            {
                SpeedPreference.Fastest => "10",
                SpeedPreference.SmallestFile => "6",
                _ => "8"
            };
        }

        string[] wanted = speed switch
        {
            SpeedPreference.Fastest => ["veryfast", "faster", "p2", "fast"],
            SpeedPreference.SmallestFile => ["slow", "slower", "p6", "medium"],
            _ => ["medium", "fast", "p4"]
        };

        foreach (var candidate in wanted)
        {
            if (option.Presets.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return option.Presets[option.Presets.Count / 2];
    }

    private static void TrimHeavyAudio(
        FileAnalysis analysis,
        EncodeRequest request,
        Capabilities caps,
        long ceilingBps)
    {
        var encoder = caps.AudioEncoders.FirstOrDefault(e => e.Name == "libopus")
            ?? caps.AudioEncoders.FirstOrDefault(e => e.Name == "aac");

        if (encoder is null)
        {
            return;
        }

        foreach (var track in request.AudioTracks)
        {
            var source = analysis.Audio.FirstOrDefault(a => a.Index == track.Index);
            if (source is null)
            {
                continue;
            }

            var current = source.Bitrate.Bps ?? MediaProbeService.EstimateAudioBitrate(source);

            // Only touch tracks that are actually costing space; re-encoding a 128k track to
            // 128k just loses quality for nothing.
            if (source.IsLossless || current > ceilingBps * 1.2d)
            {
                track.Action = AudioAction.Encode;
                track.Codec = encoder.Name;
                track.BitrateBps = Math.Min(ceilingBps, Math.Max(96_000, (source.Channels ?? 2) * 48_000));
            }
        }
    }

    private static void ResolveLossless(FileAnalysis analysis, EncodeRequest request)
    {
        // Matroska is the only container that reliably carries FLAC alongside everything else.
        request.Container = "mkv";

        var sourceIsLossless = analysis.Video?.IsLosslessCodec ?? false;
        request.Video = sourceIsLossless ? VideoAction.Encode : VideoAction.Copy;
        request.RateControl = sourceIsLossless ? RateControlMode.Lossless : RateControlMode.ConstantQuality;
        request.VideoCodec = sourceIsLossless ? "ffv1" : null;

        foreach (var track in request.AudioTracks)
        {
            var source = analysis.Audio.FirstOrDefault(a => a.Index == track.Index);

            // FLAC is bit-exact from a lossless source and caps out at 8 channels.
            if (source is not null && source.IsLossless && (source.Channels ?? 0) <= 8)
            {
                track.Action = AudioAction.Encode;
                track.Codec = "flac";
            }
        }
    }
}
