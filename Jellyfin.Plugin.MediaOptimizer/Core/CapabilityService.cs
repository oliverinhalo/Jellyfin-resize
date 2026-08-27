using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Discovers what this server's ffmpeg build can actually do.</summary>
public interface ICapabilityService
{
    /// <summary>Gets the discovered capabilities, probing on first use.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capabilities.</returns>
    Task<Capabilities> GetAsync(CancellationToken cancellationToken);

    /// <summary>Checks whether a named encoder is present.</summary>
    /// <param name="encoder">Encoder name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the encoder can be used.</returns>
    Task<bool> HasEncoderAsync(string encoder, CancellationToken cancellationToken);
}

/// <inheritdoc />
public partial class CapabilityService : ICapabilityService
{
    // Candidate encoders in preference order. Only the ones ffmpeg actually reports are offered.
    private static readonly (string Encoder, string Codec, string Display, bool Hardware, bool TenBit)[] VideoCandidates =
    [
        ("libx264", "h264", "H.264 (x264, CPU)", false, true),
        ("libx265", "hevc", "H.265 / HEVC (x265, CPU)", false, true),
        ("libsvtav1", "av1", "AV1 (SVT-AV1, CPU)", false, true),
        ("libaom-av1", "av1", "AV1 (libaom, CPU — very slow)", false, true),
        ("libvpx-vp9", "vp9", "VP9 (CPU)", false, true),
        ("h264_nvenc", "h264", "H.264 (NVENC)", true, false),
        ("hevc_nvenc", "hevc", "H.265 / HEVC (NVENC)", true, true),
        ("av1_nvenc", "av1", "AV1 (NVENC)", true, true),
        ("h264_qsv", "h264", "H.264 (Intel QSV)", true, false),
        ("hevc_qsv", "hevc", "H.265 / HEVC (Intel QSV)", true, true),
        ("av1_qsv", "av1", "AV1 (Intel QSV)", true, true),
        ("h264_vaapi", "h264", "H.264 (VAAPI)", true, false),
        ("hevc_vaapi", "hevc", "H.265 / HEVC (VAAPI)", true, true),
        ("av1_vaapi", "av1", "AV1 (VAAPI)", true, true),
        ("h264_videotoolbox", "h264", "H.264 (VideoToolbox)", true, false),
        ("hevc_videotoolbox", "hevc", "H.265 / HEVC (VideoToolbox)", true, true),
        ("h264_amf", "h264", "H.264 (AMD AMF)", true, false),
        ("hevc_amf", "hevc", "H.265 / HEVC (AMD AMF)", true, true),
        ("ffv1", "ffv1", "FFV1 (lossless archival)", false, true)
    ];

    private static readonly (string Encoder, string Codec, string Display)[] AudioCandidates =
    [
        ("libopus", "opus", "Opus"),
        ("aac", "aac", "AAC"),
        ("libfdk_aac", "aac", "AAC (libfdk)"),
        ("ac3", "ac3", "Dolby Digital (AC-3)"),
        ("eac3", "eac3", "Dolby Digital Plus (E-AC-3)"),
        ("libmp3lame", "mp3", "MP3"),
        ("flac", "flac", "FLAC (lossless)"),
        ("libvorbis", "vorbis", "Vorbis")
    ];

    private static readonly string[] X264X265Presets =
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"
    ];

    private readonly IFfmpegRunner _runner;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<CapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private Capabilities? _cached;

    /// <summary>Initializes a new instance of the <see cref="CapabilityService"/> class.</summary>
    /// <param name="runner">FFmpeg runner.</param>
    /// <param name="mediaEncoder">Jellyfin media encoder, for its own capability checks.</param>
    /// <param name="config">Server configuration, for encoding options.</param>
    /// <param name="logger">Logger.</param>
    public CapabilityService(
        IFfmpegRunner runner,
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager config,
        ILogger<CapabilityService> logger)
    {
        _runner = runner;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Capabilities> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasEncoderAsync(string encoder, CancellationToken cancellationToken)
    {
        var caps = await GetAsync(cancellationToken).ConfigureAwait(false);
        foreach (var e in caps.VideoEncoders)
        {
            if (string.Equals(e.Name, encoder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var e in caps.AudioEncoders)
        {
            if (string.Equals(e.Name, encoder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extracts encoder names from the output of "ffmpeg -encoders".</summary>
    /// <param name="output">Raw command output.</param>
    /// <returns>The set of encoder names.</returns>
    internal static HashSet<string> ParseEncoderList(string output)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var match = EncoderLineRegex().Match(raw);
            if (match.Success)
            {
                found.Add(match.Groups["name"].Value);
            }
        }

        return found;
    }

    // " V....D libx264              H.264 ..." — six capability flags, then the name.
    [GeneratedRegex(@"^\s*[VAS.][F.][S.][X.][B.][D.]\s+(?<name>[A-Za-z0-9_\-]+)\s")]
    private static partial Regex EncoderLineRegex();

    private async Task<Capabilities> ProbeAsync(CancellationToken cancellationToken)
    {
        var caps = new Capabilities { FfmpegPath = _runner.FfmpegPath };

        try
        {
            caps.FfmpegVersion = _mediaEncoder.EncoderVersion?.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read ffmpeg version");
        }

        HashSet<string> available;
        try
        {
            var result = await _runner
                .RunAsync(_runner.FfmpegPath, ["-hide_banner", "-encoders"], cancellationToken)
                .ConfigureAwait(false);
            available = ParseEncoderList(result.StandardOutput + "\n" + result.StandardError);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not enumerate ffmpeg encoders at {Path}", _runner.FfmpegPath);
            available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var video = new List<EncoderOption>();
        foreach (var (encoder, codec, display, hardware, tenBit) in VideoCandidates)
        {
            // Trust ffmpeg's own list first, then Jellyfin's probe as a second opinion.
            if (!available.Contains(encoder) && !SupportsViaJellyfin(encoder))
            {
                continue;
            }

            video.Add(new EncoderOption
            {
                Name = encoder,
                Codec = codec,
                DisplayName = display,
                IsHardware = hardware,
                Supports10Bit = tenBit,
                Presets = PresetsFor(encoder)
            });
        }

        var audio = new List<EncoderOption>();
        foreach (var (encoder, codec, display) in AudioCandidates)
        {
            if (!available.Contains(encoder) && !SupportsViaJellyfin(encoder))
            {
                continue;
            }

            audio.Add(new EncoderOption { Name = encoder, Codec = codec, DisplayName = display });
        }

        caps.VideoEncoders = video;
        caps.AudioEncoders = audio;
        caps.Containers = ["mkv", "mp4"];

        var encoding = _config.GetEncodingOptions();
        caps.HardwareAcceleration = encoding.HardwareAccelerationType.ToString();
        caps.VaapiDevice = encoding.VaapiDevice;
        caps.AllowHevcEncoding = encoding.AllowHevcEncoding;
        caps.AllowAv1Encoding = encoding.AllowAv1Encoding;

        _logger.LogInformation(
            "[MediaOptimizer] ffmpeg at {Path}: {VideoCount} video encoders, {AudioCount} audio encoders",
            _runner.FfmpegPath,
            video.Count,
            audio.Count);

        return caps;
    }

    private static IReadOnlyList<string> PresetsFor(string encoder)
    {
        if (encoder is "libx264" or "libx265")
        {
            return X264X265Presets;
        }

        if (encoder == "libsvtav1")
        {
            // SVT-AV1 uses numeric presets; 0 is slowest, 13 fastest.
            var presets = new List<string>();
            for (var i = 0; i <= 13; i++)
            {
                presets.Add(i.ToString(CultureInfo.InvariantCulture));
            }

            return presets;
        }

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];
        }

        if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
        }

        return Array.Empty<string>();
    }

    private bool SupportsViaJellyfin(string encoder)
    {
        try
        {
            return _mediaEncoder.SupportsEncoder(encoder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] SupportsEncoder({Encoder}) failed", encoder);
            return false;
        }
    }
}
