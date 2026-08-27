using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>How a numeric field was obtained. Drives the "≈" marker in the UI.</summary>
public enum ValueSource
{
    /// <summary>Read directly from the container/stream metadata.</summary>
    Measured = 0,

    /// <summary>Computed from file size and duration. Approximate.</summary>
    Derived = 1,

    /// <summary>Not available at all.</summary>
    Unknown = 2
}

/// <summary>A bitrate together with how confident we are in it.</summary>
public class BitrateInfo
{
    /// <summary>Gets or sets bits per second, when known.</summary>
    public long? Bps { get; set; }

    /// <summary>Gets or sets how the value was obtained.</summary>
    public ValueSource Source { get; set; } = ValueSource.Unknown;
}

/// <summary>Video track description.</summary>
public class VideoTrackInfo
{
    /// <summary>Gets or sets the ffmpeg stream index.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the codec name, e.g. hevc.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec profile, e.g. Main 10.</summary>
    public string? Profile { get; set; }

    /// <summary>Gets or sets the codec level.</summary>
    public double? Level { get; set; }

    /// <summary>Gets or sets the coded width.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the coded height.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets bits per colour component.</summary>
    public int? BitDepth { get; set; }

    /// <summary>Gets or sets the ffmpeg pixel format, e.g. yuv420p10le.</summary>
    public string? PixelFormat { get; set; }

    /// <summary>Gets or sets the nominal frame rate.</summary>
    public float? FrameRate { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream appears to be variable frame rate.</summary>
    public bool IsVariableFrameRate { get; set; }

    /// <summary>Gets or sets the dynamic range label: SDR, HDR10, HLG, HDR10+, Dolby Vision.</summary>
    public string Range { get; set; } = "SDR";

    /// <summary>Gets or sets the raw Jellyfin VideoRangeType value.</summary>
    public string? RangeType { get; set; }

    /// <summary>Gets or sets a value indicating whether a Dolby Vision RPU is present.</summary>
    public bool IsDolbyVision { get; set; }

    /// <summary>Gets or sets the video bitrate.</summary>
    public BitrateInfo Bitrate { get; set; } = new BitrateInfo();

    /// <summary>Gets or sets a value indicating whether the codec itself is mathematically lossless.</summary>
    public bool IsLosslessCodec { get; set; }

    /// <summary>Gets or sets the colour transfer characteristic.</summary>
    public string? ColorTransfer { get; set; }

    /// <summary>Gets or sets the colour primaries.</summary>
    public string? ColorPrimaries { get; set; }

    /// <summary>Gets or sets the colour space / matrix coefficients.</summary>
    public string? ColorSpace { get; set; }
}

/// <summary>Audio track description.</summary>
public class AudioTrackInfo
{
    /// <summary>Gets or sets the ffmpeg stream index.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the position among audio streams only (the "a:N" index).</summary>
    public int TypeIndex { get; set; }

    /// <summary>Gets or sets the codec name.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec profile, e.g. DTS-HD MA.</summary>
    public string? Profile { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    public int? Channels { get; set; }

    /// <summary>Gets or sets the channel layout, e.g. 5.1(side).</summary>
    public string? ChannelLayout { get; set; }

    /// <summary>Gets or sets the sample rate in Hz.</summary>
    public int? SampleRate { get; set; }

    /// <summary>Gets or sets the bit depth for PCM-like formats.</summary>
    public int? BitDepth { get; set; }

    /// <summary>Gets or sets the audio bitrate.</summary>
    public BitrateInfo Bitrate { get; set; } = new BitrateInfo();

    /// <summary>Gets or sets the ISO language code.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the human-readable track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the default track.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether the codec carries a bit-exact PCM payload.</summary>
    public bool IsLossless { get; set; }

    /// <summary>Gets or sets a value indicating whether object audio (Atmos / DTS:X) is present.</summary>
    public bool HasObjectAudio { get; set; }
}

/// <summary>Subtitle track description.</summary>
public class SubtitleTrackInfo
{
    /// <summary>Gets or sets the ffmpeg stream index.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the codec name.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO language code.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the default track.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitles are bitmap-based (PGS, VOBSUB).</summary>
    public bool IsGraphical { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitle lives in a separate file.</summary>
    public bool IsExternal { get; set; }
}

/// <summary>Everything the dialog shows about the file as it exists now.</summary>
public class FileAnalysis
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type, e.g. Movie or Episode.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path on disk.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the container format.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the duration in ticks.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Gets or sets the overall bitrate across all streams.</summary>
    public BitrateInfo OverallBitrate { get; set; } = new BitrateInfo();

    /// <summary>Gets or sets the primary video track, if any.</summary>
    public VideoTrackInfo? Video { get; set; }

    /// <summary>Gets or sets the audio tracks.</summary>
    public IReadOnlyList<AudioTrackInfo> Audio { get; set; } = Array.Empty<AudioTrackInfo>();

    /// <summary>Gets or sets the subtitle tracks.</summary>
    public IReadOnlyList<SubtitleTrackInfo> Subtitles { get; set; } = Array.Empty<SubtitleTrackInfo>();

    /// <summary>Gets or sets the number of embedded attachments (fonts, cover art).</summary>
    public int AttachmentCount { get; set; }

    /// <summary>Gets or sets the number of chapters.</summary>
    public int ChapterCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the containing directory is writable.</summary>
    public bool IsWritable { get; set; }

    /// <summary>Gets or sets a value indicating whether this item can be converted at all.</summary>
    public bool IsEligible { get; set; }

    /// <summary>Gets or sets the reason the item is not eligible.</summary>
    public string? IneligibleReason { get; set; }

    /// <summary>Gets or sets a value indicating whether a job for this item is already queued or running.</summary>
    public bool HasActiveJob { get; set; }

    /// <summary>
    /// Gets or sets the strategy that actually helps this file, so the dialog does not open on
    /// an option that would save nothing.
    /// </summary>
    public string RecommendedStrategy { get; set; } = "Standard";

    /// <summary>Gets or sets notes about prior optimisation of this item by this plugin.</summary>
    public string? OptimizationHistory { get; set; }
}
