using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>
/// Knows which codecs carry a bit-exact payload. This is what separates the plugin's
/// "lossless" claims from wishful thinking, so the lists are deliberately conservative.
/// </summary>
public static class LosslessAnalyzer
{
    // Video codecs that reconstruct their input exactly. Re-encoding these to another lossless
    // codec genuinely shrinks the file; re-encoding anything else "losslessly" inflates it.
    private static readonly HashSet<string> LosslessVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ffv1", "huffyuv", "ffvhuff", "magicyuv", "utvideo", "lagarith",
        "rawvideo", "v210", "v410", "r210", "y41p", "qtrle", "png", "tiff"
    };

    // Audio codecs whose decoded PCM is bit-identical to what was encoded.
    private static readonly HashSet<string> LosslessAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "truehd", "mlp", "flac", "alac", "wavpack", "tta", "tak", "ape",
        "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_s16be", "pcm_s24be", "pcm_s32be",
        "pcm_f32le", "pcm_f64le", "pcm_bluray", "pcm_dvd", "pcm_s24daud"
    };

    // Uncompressed PCM specifically — the case where FLAC wins big (roughly half).
    private static readonly HashSet<string> UncompressedPcmCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_s16be", "pcm_s24be", "pcm_s32be",
        "pcm_f32le", "pcm_f64le", "pcm_bluray", "pcm_dvd", "pcm_s24daud"
    };

    /// <summary>Returns true when the video codec reconstructs its input exactly.</summary>
    /// <param name="codec">Codec name.</param>
    /// <returns>True for mathematically lossless video codecs.</returns>
    public static bool IsLosslessVideo(string? codec) =>
        !string.IsNullOrEmpty(codec) && LosslessVideoCodecs.Contains(codec);

    /// <summary>
    /// Returns true when the audio stream decodes to bit-exact PCM. DTS is only lossless in its
    /// Master Audio variant, which shows up in the stream profile rather than the codec name.
    /// </summary>
    /// <param name="codec">Codec name.</param>
    /// <param name="profile">Codec profile, where the DTS variant lives.</param>
    /// <returns>True for bit-exact audio.</returns>
    public static bool IsLosslessAudio(string? codec, string? profile)
    {
        if (string.IsNullOrEmpty(codec))
        {
            return false;
        }

        if (LosslessAudioCodecs.Contains(codec))
        {
            return true;
        }

        if (codec.Equals("dts", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(profile))
        {
            // "DTS-HD MA" and "DTS-HD Master Audio" both appear in the wild.
            return profile.Contains("MA", StringComparison.OrdinalIgnoreCase)
                || profile.Contains("Master Audio", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Returns true for raw PCM, where FLAC typically halves the size.</summary>
    /// <param name="codec">Codec name.</param>
    /// <returns>True for uncompressed PCM.</returns>
    public static bool IsUncompressedPcm(string? codec) =>
        !string.IsNullOrEmpty(codec) && UncompressedPcmCodecs.Contains(codec);

    /// <summary>
    /// Returns true when the track carries height/object metadata that FLAC cannot represent.
    /// Converting these keeps the lossless channel bed and discards the objects.
    /// </summary>
    /// <param name="codec">Codec name.</param>
    /// <param name="profile">Codec profile.</param>
    /// <returns>True when object audio would be lost.</returns>
    public static bool HasObjectAudio(string? codec, string? profile)
    {
        if (string.IsNullOrEmpty(profile))
        {
            return false;
        }

        if (profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (codec is not null
            && codec.Equals("dts", StringComparison.OrdinalIgnoreCase)
            && profile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Estimated fraction of the original size after a bit-exact FLAC re-encode.
    /// These are rough real-world figures, used only for the pre-run estimate.
    /// </summary>
    /// <param name="codec">Source codec name.</param>
    /// <param name="profile">Source codec profile.</param>
    /// <returns>Expected output size as a fraction of the input, or null when FLAC would not help.</returns>
    public static double? FlacSizeFraction(string? codec, string? profile)
    {
        if (IsUncompressedPcm(codec))
        {
            return 0.55d;
        }

        if (codec is null)
        {
            return null;
        }

        if (codec.Equals("truehd", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("mlp", StringComparison.OrdinalIgnoreCase))
        {
            return 0.80d;
        }

        if (codec.Equals("dts", StringComparison.OrdinalIgnoreCase)
            && IsLosslessAudio(codec, profile))
        {
            return 0.85d;
        }

        // Already FLAC or ALAC: re-encoding buys nothing worth showing.
        return null;
    }
}
