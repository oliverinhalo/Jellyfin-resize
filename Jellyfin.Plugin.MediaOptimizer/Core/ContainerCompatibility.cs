using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>
/// Knows which codecs each output container can actually hold.
/// <para>
/// This exists because getting it wrong is not a warning — FFmpeg refuses to write the header, the
/// video filter then fails with <c>Invalid argument</c> before a single frame is encoded, and the
/// job dies after the user has waited for it. Every place that decides a container or emits a
/// <c>copy</c> goes through here so the rules live in exactly one file.
/// </para>
/// </summary>
public static class ContainerCompatibility
{
    /// <summary>
    /// Audio codecs the MP4/MOV family can carry. An allowlist, not a blocklist: a codec nobody
    /// thought about must fail closed (re-encode or MKV), never produce an unwritable file.
    /// </summary>
    private static readonly HashSet<string> Mp4Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "ac3", "eac3", "ac-3", "e-ac-3", "mp3", "mp2", "mp1", "mp4als", "als",
        "alac", "flac", "opus", "vorbis", "dts", "dca", "amr_nb", "amr_wb", "amrnb", "amrwb",
        "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be",
        "pcm_f32le", "pcm_f32be", "pcm_f64le", "pcm_f64be", "pcm_alaw", "pcm_mulaw",
    };

    /// <summary>Subtitle codecs that are image-based and therefore cannot be turned into text.</summary>
    private static readonly HashSet<string> Graphical = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "pgs", "dvd_subtitle", "dvdsub", "vobsub",
        "dvb_subtitle", "dvbsub", "xsub", "hdmv_text_subtitle",
    };

    /// <summary>Subtitle codecs Matroska cannot store.</summary>
    private static readonly HashSet<string> MatroskaSubtitleReject = new(StringComparer.OrdinalIgnoreCase)
    {
        "mov_text", "tx3g", "eia_608", "eia-608", "cc_dec",
    };

    /// <summary>Normalises a container name to a lower-case family key.</summary>
    /// <param name="container">A container name or file extension.</param>
    /// <returns>The normalised family name.</returns>
    public static string Normalise(string? container)
    {
        var value = (container ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return value switch
        {
            "m4v" or "mov" or "mp4" => "mp4",
            "mka" or "mks" or "mkv" => "mkv",
            _ => value.Length == 0 ? "mkv" : value,
        };
    }

    /// <summary>Whether a subtitle codec is image-based rather than text.</summary>
    /// <param name="codec">The FFmpeg or Jellyfin codec name.</param>
    /// <returns><c>true</c> when the track is a bitmap subtitle.</returns>
    public static bool IsGraphicalSubtitle(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return false;
        }

        if (Graphical.Contains(codec))
        {
            return true;
        }

        // Jellyfin and FFmpeg spell these several ways across versions, so match loosely too.
        return codec.Contains("pgs", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("dvd_sub", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("dvdsub", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("vobsub", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("dvb_sub", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("dvbsub", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("xsub", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps an FFmpeg encoder name onto the codec it produces, so a container check works whether
    /// it is handed a source codec (<c>opus</c>) or the encoder that will write it (<c>libopus</c>).
    /// </summary>
    /// <param name="encoderOrCodec">An encoder or codec name.</param>
    /// <returns>The codec name.</returns>
    public static string CodecOf(string? encoderOrCodec)
    {
        var value = (encoderOrCodec ?? string.Empty).Trim();
        return value.ToLowerInvariant() switch
        {
            "libopus" => "opus",
            "libvorbis" => "vorbis",
            "libmp3lame" or "libshine" => "mp3",
            "libfdk_aac" or "aac_at" => "aac",
            "libtwolame" => "mp2",
            "dca" => "dts",
            "ac3_fixed" => "ac3",
            "eac3_core" => "eac3",
            "libopencore_amrnb" => "amr_nb",
            "vorbis" => "vorbis",
            _ => value,
        };
    }

    /// <summary>Whether an audio track can be stream-copied into the container unchanged.</summary>
    /// <param name="container">The output container.</param>
    /// <param name="codec">The source audio codec.</param>
    /// <returns><c>true</c> when a <c>copy</c> is safe.</returns>
    public static bool CanCopyAudio(string? container, string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return true;
        }

        var name = CodecOf(codec);
        return Normalise(container) switch
        {
            // Matroska takes anything we are ever going to hand it.
            "mkv" => true,
            "mp4" => Mp4Audio.Contains(name),
            "webm" => name.Equals("opus", StringComparison.OrdinalIgnoreCase)
                || name.Equals("vorbis", StringComparison.OrdinalIgnoreCase),
            // Unknown container: only assume the universally safe codecs.
            _ => Mp4Audio.Contains(name),
        };
    }

    /// <summary>
    /// Picks the subtitle codec argument for one track, or <c>null</c> when the track cannot be
    /// carried at all and must be dropped.
    /// </summary>
    /// <param name="container">The output container.</param>
    /// <param name="codec">The source subtitle codec.</param>
    /// <returns><c>copy</c>, a transcode target such as <c>mov_text</c>, or <c>null</c> to drop.</returns>
    public static string? SubtitleCodecFor(string? container, string? codec)
    {
        var family = Normalise(container);
        var graphical = IsGraphicalSubtitle(codec);

        switch (family)
        {
            case "mkv":
                // Matroska stores every text and bitmap format we hand it, except the MP4-only
                // ones, which have to be converted back to SubRip.
                return codec is not null && MatroskaSubtitleReject.Contains(codec) ? "srt" : "copy";

            case "mp4":
                // MP4 has exactly one text subtitle format, and no way to store bitmaps.
                if (graphical)
                {
                    return null;
                }

                return codec is not null
                    && (codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase)
                        || codec.Equals("tx3g", StringComparison.OrdinalIgnoreCase))
                    ? "copy"
                    : "mov_text";

            case "webm":
                return graphical ? null : "webvtt";

            default:
                return graphical ? null : "copy";
        }
    }

    /// <summary>
    /// Whether the container can carry every subtitle track in the set without dropping any.
    /// </summary>
    /// <param name="container">The output container.</param>
    /// <param name="codecs">The source subtitle codecs.</param>
    /// <returns><c>true</c> when nothing would be lost.</returns>
    public static bool CanCarryAllSubtitles(string? container, IEnumerable<string?> codecs)
        => codecs.All(c => SubtitleCodecFor(container, c) is not null);
}
