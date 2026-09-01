using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Builds the "current file" report shown on the left of the dialog.</summary>
public interface IMediaProbeService
{
    /// <summary>Analyses one library item.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analysis, or null when the item does not exist.</returns>
    Task<FileAnalysis?> AnalyzeAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Measures a stream's exact bitrate by summing packet sizes. Reads the whole file.</summary>
    /// <param name="path">File path.</param>
    /// <param name="streamSpecifier">An ffmpeg stream specifier such as "v:0".</param>
    /// <param name="durationSeconds">Duration used to turn total bytes into a rate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bits per second, or null when it could not be measured.</returns>
    Task<long?> MeasureBitrateAsync(string path, string streamSpecifier, double durationSeconds, CancellationToken cancellationToken);
}

/// <inheritdoc />
public class MediaProbeService : IMediaProbeService
{
    private static readonly string[] ExcludedExtensions = [".iso", ".strm", ".m3u", ".m3u8"];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IFfmpegRunner _runner;
    private readonly ILogger<MediaProbeService> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaProbeService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager, for typed stream metadata.</param>
    /// <param name="runner">FFmpeg runner, for the supplementary ffprobe call.</param>
    /// <param name="logger">Logger.</param>
    public MediaProbeService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IFfmpegRunner runner,
        ILogger<MediaProbeService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileAnalysis?> AnalyzeAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var analysis = new FileAnalysis
        {
            ItemId = itemId,
            Name = item.Name ?? string.Empty,
            ItemType = item.GetType().Name,
            Path = item.Path ?? string.Empty,
            RunTimeTicks = item.RunTimeTicks
        };

        if (item.RunTimeTicks is > 0)
        {
            analysis.DurationSeconds = TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds;
        }

        SetEligibility(item, analysis);

        if (!string.IsNullOrEmpty(analysis.Path))
        {
            try
            {
                var info = new FileInfo(analysis.Path);
                if (info.Exists)
                {
                    analysis.SizeBytes = info.Length;
                    analysis.Container = info.Extension.TrimStart('.').ToLowerInvariant();
                    analysis.IsWritable = IsDirectoryWritable(info.DirectoryName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "[MediaOptimizer] Could not stat {Path}", analysis.Path);
            }
        }

        var streams = GetMediaStreams(itemId);
        BuildTracks(analysis, streams);

        // ffprobe fills the gaps Jellyfin's model does not carry: attachments, chapter count,
        // Dolby Vision side data and variable-frame-rate detection.
        await EnrichFromFfprobeAsync(analysis, cancellationToken).ConfigureAwait(false);

        DeriveBitrates(analysis);

        return analysis;
    }

    /// <inheritdoc />
    public async Task<long?> MeasureBitrateAsync(
        string path,
        string streamSpecifier,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (durationSeconds <= 0d)
        {
            return null;
        }

        string[] args =
        [
            "-v", "error",
            "-select_streams", streamSpecifier,
            "-show_entries", "packet=size",
            "-of", "compact=p=0:nk=1",
            path
        ];

        try
        {
            var result = await _runner.RunAsync(_runner.FfprobePath, args, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            long total = 0;
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                {
                    total += size;
                }
            }

            return total > 0 ? (long)(total * 8d / durationSeconds) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Exact bitrate measurement failed for {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Splits the overall bitrate across streams when the container carried no per-stream tag.
    /// Matroska very often omits these, so without this the dialog would show blanks on most libraries.
    /// </summary>
    /// <param name="analysis">The analysis to fill in.</param>
    internal static void DeriveBitrates(FileAnalysis analysis)
    {
        if (analysis.SizeBytes is not > 0 || analysis.DurationSeconds is not > 0)
        {
            return;
        }

        var overall = (long)(analysis.SizeBytes.Value * 8d / analysis.DurationSeconds.Value);
        if (analysis.OverallBitrate.Bps is null)
        {
            analysis.OverallBitrate.Bps = overall;
            analysis.OverallBitrate.Source = ValueSource.Derived;
        }

        if (analysis.Video is null)
        {
            return;
        }

        // Everything the container reports for non-video streams is subtracted from the total;
        // whatever is left is attributed to video. Unknown audio tracks fall back to a typical rate
        // so the estimate does not collapse to zero on a fully untagged file.
        long known = 0;
        foreach (var a in analysis.Audio)
        {
            known += a.Bitrate.Bps ?? EstimateAudioBitrate(a);
        }

        var remaining = overall - known;
        if (remaining <= 0)
        {
            return;
        }

        if (analysis.Video.Bitrate.Bps is null)
        {
            analysis.Video.Bitrate.Bps = remaining;
            analysis.Video.Bitrate.Source = ValueSource.Derived;
            return;
        }

        // Some muxers tag the video stream with the file's overall bitrate. Taken at face value
        // that makes video plus audio exceed the whole file, which then predicts that any
        // re-encode grows it. Trust the arithmetic over the tag when they disagree.
        if (analysis.Video.Bitrate.Bps > remaining)
        {
            analysis.Video.Bitrate.Bps = remaining;
            analysis.Video.Bitrate.Source = ValueSource.Derived;
        }
    }

    /// <summary>A stand-in bitrate for an audio track whose real rate is untagged.</summary>
    /// <param name="track">The audio track.</param>
    /// <returns>Bits per second.</returns>
    internal static long EstimateAudioBitrate(AudioTrackInfo track)
    {
        var channels = track.Channels ?? 2;

        if (LosslessAnalyzer.IsUncompressedPcm(track.Codec))
        {
            var rate = track.SampleRate ?? 48000;
            var depth = track.BitDepth ?? 16;
            return (long)channels * rate * depth;
        }

        if (LosslessAnalyzer.IsLosslessAudio(track.Codec, track.Profile))
        {
            // TrueHD/DTS-HD MA land around 700 kbps per channel on real discs.
            return (long)channels * 700_000;
        }

        return (long)channels * 96_000;
    }

    /// <summary>Maps Jellyfin's VideoRange/VideoRangeType onto the label shown in the UI.</summary>
    /// <param name="range">The VideoRange value.</param>
    /// <param name="rangeType">The VideoRangeType value.</param>
    /// <returns>A display label.</returns>
    internal static string DescribeRange(string? range, string? rangeType)
    {
        if (string.IsNullOrEmpty(rangeType) || rangeType.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(range) ? "SDR" : range;
        }

        return rangeType switch
        {
            "SDR" => "SDR",
            "HDR10" => "HDR10",
            "HLG" => "HLG",
            "HDR10Plus" => "HDR10+",
            "DOVI" => "Dolby Vision",
            "DOVIWithHDR10" => "Dolby Vision (HDR10 base)",
            "DOVIWithHLG" => "Dolby Vision (HLG base)",
            "DOVIWithSDR" => "Dolby Vision (SDR base)",
            "DOVIWithEL" => "Dolby Vision (with EL)",
            _ => rangeType
        };
    }

    private static bool IsDirectoryWritable(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(directory, ".mediaoptimizer-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static void SetEligibility(BaseItem item, FileAnalysis analysis)
    {
        if (item is not Video)
        {
            analysis.IsEligible = false;
            analysis.IneligibleReason = "Only video items can be converted.";
            return;
        }

        var path = analysis.Path;
        if (string.IsNullOrEmpty(path))
        {
            analysis.IsEligible = false;
            analysis.IneligibleReason = "This item has no file path on disk.";
            return;
        }

        var ext = Path.GetExtension(path);
        if (ExcludedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            analysis.IsEligible = false;
            analysis.IneligibleReason = $"{ext} items are disc images or playlists, not single media files.";
            return;
        }

        if (Directory.Exists(path))
        {
            analysis.IsEligible = false;
            analysis.IneligibleReason = "This item is a disc folder rip (BDMV / VIDEO_TS), not a single file.";
            return;
        }

        if (item is Video video && video.AdditionalParts.Length > 0)
        {
            analysis.IsEligible = false;
            analysis.IneligibleReason = "Multi-part items are not supported.";
            return;
        }

        analysis.IsEligible = true;
    }

    private IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        try
        {
            return _mediaSourceManager.GetMediaStreams(itemId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not read media streams for {ItemId}", itemId);
            return Array.Empty<MediaStream>();
        }
    }

    private static void BuildTracks(FileAnalysis analysis, IReadOnlyList<MediaStream> streams)
    {
        var audio = new List<AudioTrackInfo>();
        var subs = new List<SubtitleTrackInfo>();
        var audioTypeIndex = 0;

        foreach (var s in streams)
        {
            switch (s.Type)
            {
                case MediaStreamType.Video when analysis.Video is null && !s.IsExternal:
                    analysis.Video = new VideoTrackInfo
                    {
                        Index = s.Index,
                        Codec = s.Codec ?? string.Empty,
                        Profile = s.Profile,
                        Level = s.Level,
                        Width = s.Width,
                        Height = s.Height,
                        BitDepth = s.BitDepth,
                        PixelFormat = s.PixelFormat,
                        FrameRate = s.AverageFrameRate ?? s.RealFrameRate,
                        Range = DescribeRange(s.VideoRange.ToString(), s.VideoRangeType.ToString()),
                        RangeType = s.VideoRangeType.ToString(),
                        IsDolbyVision = s.VideoRangeType.ToString().StartsWith("DOVI", StringComparison.OrdinalIgnoreCase),
                        ColorTransfer = s.ColorTransfer,
                        ColorPrimaries = s.ColorPrimaries,
                        ColorSpace = s.ColorSpace,
                        IsLosslessCodec = LosslessAnalyzer.IsLosslessVideo(s.Codec),
                        Bitrate = new BitrateInfo
                        {
                            Bps = s.BitRate,
                            Source = s.BitRate is not null ? ValueSource.Measured : ValueSource.Unknown
                        }
                    };
                    break;

                case MediaStreamType.Audio:
                    audio.Add(new AudioTrackInfo
                    {
                        Index = s.Index,
                        TypeIndex = audioTypeIndex++,
                        Codec = s.Codec ?? string.Empty,
                        Profile = s.Profile,
                        Channels = s.Channels,
                        ChannelLayout = s.ChannelLayout,
                        SampleRate = s.SampleRate,
                        BitDepth = s.BitDepth,
                        Language = s.Language,
                        Title = s.Title,
                        IsDefault = s.IsDefault,
                        IsLossless = LosslessAnalyzer.IsLosslessAudio(s.Codec, s.Profile),
                        HasObjectAudio = LosslessAnalyzer.HasObjectAudio(s.Codec, s.Profile),
                        Bitrate = new BitrateInfo
                        {
                            Bps = s.BitRate,
                            Source = s.BitRate is not null ? ValueSource.Measured : ValueSource.Unknown
                        }
                    });
                    break;

                case MediaStreamType.Subtitle:
                    subs.Add(new SubtitleTrackInfo
                    {
                        Index = s.Index,
                        Codec = s.Codec ?? string.Empty,
                        Language = s.Language,
                        Title = s.Title,
                        IsDefault = s.IsDefault,
                        IsExternal = s.IsExternal,
                        IsGraphical = ContainerCompatibility.IsGraphicalSubtitle(s.Codec)
                    });
                    break;

                default:
                    break;
            }
        }

        analysis.Audio = audio;
        analysis.Subtitles = subs;
    }

    private async Task EnrichFromFfprobeAsync(FileAnalysis analysis, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(analysis.Path) || !File.Exists(analysis.Path))
        {
            return;
        }

        string[] args =
        [
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "-show_chapters",
            analysis.Path
        ];

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_runner.FfprobePath, args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] ffprobe failed for {Path}", analysis.Path);
            return;
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var root = doc.RootElement;

            if (root.TryGetProperty("format", out var format))
            {
                if (analysis.DurationSeconds is null
                    && format.TryGetProperty("duration", out var dur)
                    && double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    analysis.DurationSeconds = seconds;
                }

                if (format.TryGetProperty("bit_rate", out var br)
                    && long.TryParse(br.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var overall))
                {
                    analysis.OverallBitrate.Bps = overall;
                    analysis.OverallBitrate.Source = ValueSource.Measured;
                }

                if (format.TryGetProperty("format_name", out var fname))
                {
                    var name = fname.GetString();
                    if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(analysis.Container))
                    {
                        analysis.Container = name.Split(',')[0];
                    }
                }
            }

            if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            {
                analysis.ChapterCount = chapters.GetArrayLength();
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                ApplyStreamDetails(analysis, streams);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not parse ffprobe output for {Path}", analysis.Path);
        }
    }

    private static void ApplyStreamDetails(FileAnalysis analysis, JsonElement streams)
    {
        var attachments = 0;

        foreach (var s in streams.EnumerateArray())
        {
            var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;

            if (string.Equals(type, "attachment", StringComparison.Ordinal))
            {
                attachments++;
                continue;
            }

            if (!string.Equals(type, "video", StringComparison.Ordinal) || analysis.Video is null)
            {
                continue;
            }

            var index = s.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : -1;
            if (index != analysis.Video.Index)
            {
                continue;
            }

            // r_frame_rate is the container's nominal rate, avg_frame_rate the measured one.
            // A meaningful gap between them is the practical signal for variable frame rate.
            var r = ParseRational(s, "r_frame_rate");
            var avg = ParseRational(s, "avg_frame_rate");
            if (r is > 0 && avg is > 0 && Math.Abs(r.Value - avg.Value) > 0.01d)
            {
                analysis.Video.IsVariableFrameRate = true;
            }

            if (analysis.Video.FrameRate is null && avg is > 0)
            {
                analysis.Video.FrameRate = (float)avg.Value;
            }

            if (s.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            {
                foreach (var sd in sideData.EnumerateArray())
                {
                    var sdType = sd.TryGetProperty("side_data_type", out var sdt) ? sdt.GetString() : null;
                    if (sdType is not null && sdType.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                    {
                        analysis.Video.IsDolbyVision = true;
                    }
                }
            }
        }

        analysis.AttachmentCount = attachments;
    }

    private static double? ParseRational(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            || den == 0d)
        {
            return null;
        }

        return num / den;
    }
}
