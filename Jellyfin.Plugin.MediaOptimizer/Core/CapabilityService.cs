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

    /// <summary>
    /// How long a probe that found no encoders is held before it is tried again. A failed probe
    /// is never cached for good: the usual cause is ffmpeg being briefly unreachable — a path
    /// Jellyfin had not set yet, or an upgrade swapping the binary out underneath a running
    /// server — and caching that would leave the plugin unusable until the next restart.
    /// </summary>
    private static readonly TimeSpan FailedProbeRetryAfter = TimeSpan.FromSeconds(30);

    private readonly IFfmpegRunner _runner;
    private readonly IServerEncodingContext _server;
    private readonly ILogger<CapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    /// <summary>
    /// How long a successful probe is trusted. An ffmpeg replaced in place, at the same path, is
    /// otherwise invisible until the server restarts -- and the probe is a handful of processes,
    /// so asking again twice an hour costs nothing worth counting.
    /// </summary>
    internal static readonly TimeSpan SuccessfulProbeLifetime = TimeSpan.FromMinutes(30);

    private Capabilities? _cached;
    private DateTime _cachedAt;
    private Capabilities? _lastFailed;
    private DateTime _lastFailedAt;

    /// <summary>Initializes a new instance of the <see cref="CapabilityService"/> class.</summary>
    /// <param name="runner">FFmpeg runner.</param>
    /// <param name="server">The server around this plugin: its ffmpeg and its settings.</param>
    /// <param name="logger">Logger.</param>
    public CapabilityService(
        IFfmpegRunner runner,
        IServerEncodingContext server,
        ILogger<CapabilityService> logger)
    {
        _runner = runner;
        _server = server;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Capabilities> GetAsync(CancellationToken cancellationToken)
    {
        var probed = await ProbedAsync(cancellationToken).ConfigureAwait(false);

        // Every caller gets its own copy. One of them writes to it -- the API stamps "may this
        // user convert?" onto the answer it is about to send -- and handing the cached instance
        // out would let one request's answer change another's, which is the same class of bug as
        // the paused flag that used to be static.
        var snapshot = probed.Clone();

        // The server's own settings are read here rather than cached with the probe: an
        // administrator turning HEVC encoding on should not have to restart Jellyfin before this
        // plugin stops warning that it is off.
        var settings = _server.GetSettings();
        snapshot.HardwareAcceleration = settings.HardwareAcceleration;
        snapshot.VaapiDevice = settings.VaapiDevice;
        snapshot.AllowHevcEncoding = settings.AllowHevcEncoding;
        snapshot.AllowAv1Encoding = settings.AllowAv1Encoding;

        return snapshot;
    }

    /// <summary>
    /// The cached answer to "what can this ffmpeg do?".
    /// <para>
    /// Cached because it costs several processes to work out, and re-probed when the binary it
    /// describes is not the one Jellyfin is pointing at any more — the case where an
    /// administrator fixes the path, or an upgrade moves it. An upgrade that replaces the binary
    /// at the same path is picked up when the cache ages out.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    private async Task<Capabilities> ProbedAsync(CancellationToken cancellationToken)
    {
        var cached = _cached;
        if (cached is not null && IsStillCurrent(cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cached;
            if (cached is not null && IsStillCurrent(cached))
            {
                return cached;
            }

            if (_lastFailed is not null && DateTime.UtcNow - _lastFailedAt < FailedProbeRetryAfter)
            {
                return _lastFailed;
            }

            var probed = await ProbeAsync(cancellationToken).ConfigureAwait(false);

            // Only a probe that actually found something is kept. Otherwise the plugin would be
            // permanently dead after one bad moment, with an empty codec list and no way back
            // short of restarting the server.
            if (probed.VideoEncoders.Count > 0)
            {
                _cached = probed;
                _cachedAt = DateTime.UtcNow;
                _lastFailed = null;
                return probed;
            }

            _lastFailed = probed;
            _lastFailedAt = DateTime.UtcNow;
            return probed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether a cached probe still describes the ffmpeg this server is using.</summary>
    /// <param name="cached">The cached probe.</param>
    /// <returns>Whether it may be reused.</returns>
    private bool IsStillCurrent(Capabilities cached)
    {
        if (DateTime.UtcNow - _cachedAt > SuccessfulProbeLifetime)
        {
            return false;
        }

        string? current;
        try
        {
            current = _runner.FfmpegPath;
        }
#pragma warning disable CA1031 // An unreadable path is a reason to re-probe, not to throw.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        return string.Equals(cached.FfmpegPath, current, StringComparison.Ordinal);
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

    // " V....D libx264              H.264 ..." — a block of capability flags, then the name.
    // The flag block is deliberately not pinned to today's exact six letters: ffmpeg has added
    // flags before, and a listing this does not recognise leaves the plugin with no codecs at
    // all. The legend ffmpeg prints above the table ("V..... = Video") is still excluded,
    // because its second column starts with "=".
    [GeneratedRegex(@"^\s*[VAS.][A-Za-z.]{1,7}\s+(?<name>[A-Za-z0-9_.\-]{2,})(?:\s|$)")]
    private static partial Regex EncoderLineRegex();

    private async Task<Capabilities> ProbeAsync(CancellationToken cancellationToken)
    {
        string? ffmpegPath = null;
        string? probeError = null;

        try
        {
            ffmpegPath = _runner.FfmpegPath;
        }
#pragma warning disable CA1031 // A path this plugin cannot read must be reported, not thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "[MediaOptimizer] Jellyfin did not report an ffmpeg path");
            probeError = "Jellyfin did not report an FFmpeg path: " + ex.Message;
        }

        var caps = new Capabilities { FfmpegPath = ffmpegPath };

        try
        {
            caps.FfmpegVersion = _server.EncoderVersion;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read ffmpeg version");
        }

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ffmpegAnswered = false;

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            probeError ??= "Jellyfin has no FFmpeg path configured. Set one under Dashboard → Playback → Transcoding.";
        }
        else
        {
            try
            {
                var result = await _runner
                    .RunAsync(ffmpegPath, ["-hide_banner", "-encoders"], cancellationToken)
                    .ConfigureAwait(false);
                available = ParseEncoderList(result.StandardOutput + "\n" + result.StandardError);
                ffmpegAnswered = true;

                if (available.Count == 0)
                {
                    probeError = FormattableString.Invariant(
                        $"'{ffmpegPath} -encoders' exited with code {result.ExitCode} and listed no encoders.");
                    _logger.LogWarning(
                        "[MediaOptimizer] ffmpeg at {Path} listed no encoders (exit code {ExitCode})",
                        ffmpegPath,
                        result.ExitCode);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Any failure here has to degrade to "nothing found", not throw.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "[MediaOptimizer] Could not enumerate ffmpeg encoders at {Path}", ffmpegPath);
                probeError = FormattableString.Invariant($"Could not run '{ffmpegPath} -encoders': {ex.Message}");
            }
        }

        var video = BuildVideoOptions(available);
        var audio = BuildAudioOptions(available);

        // Last resort. Both the bulk listing and Jellyfin's own encoder table came back with
        // nothing, which would leave the dialog with an empty codec dropdown and no way to
        // convert anything. Asking ffmpeg about each candidate one at a time costs a handful of
        // very short processes, only ever runs on a server that would otherwise offer nothing,
        // and does not depend on the exact shape of the "-encoders" table. It is skipped when
        // ffmpeg could not be run at all, since asking it twenty more times would not help.
        if (video.Count == 0 && ffmpegAnswered && !string.IsNullOrWhiteSpace(ffmpegPath))
        {
            _logger.LogWarning(
                "[MediaOptimizer] No encoders found from the listing; asking ffmpeg at {Path} about each candidate individually",
                ffmpegPath);

            foreach (var (encoder, _, _, _, _) in VideoCandidates)
            {
                if (await HasEncoderDirectlyAsync(ffmpegPath, encoder, cancellationToken).ConfigureAwait(false))
                {
                    available.Add(encoder);
                }
            }

            foreach (var (encoder, _, _) in AudioCandidates)
            {
                if (await HasEncoderDirectlyAsync(ffmpegPath, encoder, cancellationToken).ConfigureAwait(false))
                {
                    available.Add(encoder);
                }
            }

            video = BuildVideoOptions(available);
            audio = BuildAudioOptions(available);

            if (video.Count > 0)
            {
                // The listing was unreadable but ffmpeg itself is fine, so this is no longer a
                // failure the user needs to see.
                probeError = null;
            }
        }

        caps.VideoEncoders = video;
        caps.AudioEncoders = audio;
        caps.Containers = ["mkv", "mp4"];
        caps.QualityMetric = await DetectQualityMetricAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        caps.ProbeError = video.Count == 0
            ? probeError ?? FormattableString.Invariant(
                $"FFmpeg at {ffmpegPath} was reachable but reported none of the encoders this plugin can use.")
            : null;

        _logger.LogInformation(
            "[MediaOptimizer] ffmpeg at {Path}: {VideoCount} video encoders, {AudioCount} audio encoders",
            ffmpegPath,
            video.Count,
            audio.Count);

        return caps;
    }

    /// <summary>
    /// Finds the best quality metric this build can measure. VMAF is a model trained on what
    /// people actually said about video, which is the question being asked; SSIM is a structural
    /// comparison that correlates less well but is present in every build.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg binary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>"VMAF", "SSIM", or null.</returns>
    private async Task<string?> DetectQualityMetricAsync(string? ffmpegPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return null;
        }

        try
        {
            var result = await _runner
                .RunAsync(ffmpegPath, ["-hide_banner", "-filters"], cancellationToken)
                .ConfigureAwait(false);

            var text = result.StandardOutput + "\n" + result.StandardError;

            if (text.Contains(" libvmaf ", StringComparison.Ordinal))
            {
                return QualityProbe.Vmaf;
            }

            return text.Contains(" ssim ", StringComparison.Ordinal) ? QualityProbe.Ssim : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Not knowing simply means no quality number is offered.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not list ffmpeg filters");
            return null;
        }
    }

    private List<EncoderOption> BuildVideoOptions(HashSet<string> available)
    {
        var options = new List<EncoderOption>();
        foreach (var (encoder, codec, display, hardware, tenBit) in VideoCandidates)
        {
            // Trust ffmpeg's own list first, then Jellyfin's probe as a second opinion.
            if (!available.Contains(encoder) && !SupportsViaJellyfin(encoder))
            {
                continue;
            }

            options.Add(new EncoderOption
            {
                Name = encoder,
                Codec = codec,
                DisplayName = display,
                IsHardware = hardware,
                Supports10Bit = tenBit,
                Presets = PresetsFor(encoder)
            });
        }

        return options;
    }

    private List<EncoderOption> BuildAudioOptions(HashSet<string> available)
    {
        var options = new List<EncoderOption>();
        foreach (var (encoder, codec, display) in AudioCandidates)
        {
            if (!available.Contains(encoder) && !SupportsViaJellyfin(encoder))
            {
                continue;
            }

            options.Add(new EncoderOption { Name = encoder, Codec = codec, DisplayName = display });
        }

        return options;
    }

    /// <summary>
    /// Asks ffmpeg about one encoder directly. "-h encoder=libx264" answers with
    /// "Encoder libx264 [...]" when it exists and "Codec 'libx264' is not recognized" when it
    /// does not, which makes it a reliable check even when the encoder table cannot be read.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg binary.</param>
    /// <param name="encoder">The encoder name to ask about.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when ffmpeg reports the encoder.</returns>
    private async Task<bool> HasEncoderDirectlyAsync(
        string ffmpegPath,
        string encoder,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner
                .RunAsync(ffmpegPath, ["-hide_banner", "-h", "encoder=" + encoder], cancellationToken)
                .ConfigureAwait(false);

            return (result.StandardOutput + result.StandardError)
                .Contains("Encoder " + encoder, StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // One unanswerable candidate must not stop the rest being checked.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not ask ffmpeg about {Encoder}", encoder);
            return false;
        }
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

    private bool SupportsViaJellyfin(string encoder) => _server.SupportsEncoder(encoder);
}
