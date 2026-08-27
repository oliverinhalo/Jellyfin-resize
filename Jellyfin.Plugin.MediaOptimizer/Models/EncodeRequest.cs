using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaOptimizer.Configuration;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>Named starting points that populate the whole form.</summary>
public enum OptimizationStrategy
{
    /// <summary>User-specified settings.</summary>
    Custom = 0,

    /// <summary>Sensible size reduction at near-transparent quality.</summary>
    Balanced = 1,

    /// <summary>Smallest reasonable file, accepting visible quality loss.</summary>
    MaximumCompression = 2,

    /// <summary>Preserve quality, modernise the codec.</summary>
    Archive = 3,

    /// <summary>Bit-exact operations only. No lossy re-encoding anywhere.</summary>
    LosslessOnly = 4
}

/// <summary>What to do with the video stream.</summary>
public enum VideoAction
{
    /// <summary>Stream-copy the video untouched.</summary>
    Copy = 0,

    /// <summary>Re-encode the video.</summary>
    Encode = 1,

    /// <summary>Drop the video entirely.</summary>
    Drop = 2
}

/// <summary>How the video encoder is driven.</summary>
public enum RateControlMode
{
    /// <summary>Constant quality (CRF / CQ / QP).</summary>
    ConstantQuality = 0,

    /// <summary>Average bitrate, single pass.</summary>
    AverageBitrate = 1,

    /// <summary>Two-pass targeting a specific output size.</summary>
    TargetSize = 2,

    /// <summary>Mathematically lossless. Only valid from a lossless source.</summary>
    Lossless = 3
}

/// <summary>What to do with one audio track.</summary>
public enum AudioAction
{
    /// <summary>Stream-copy the track untouched.</summary>
    Copy = 0,

    /// <summary>Re-encode the track.</summary>
    Encode = 1,

    /// <summary>Leave the track out of the output.</summary>
    Drop = 2
}

/// <summary>Per-track audio instruction.</summary>
public class AudioTrackRequest
{
    /// <summary>Gets or sets the ffmpeg stream index this refers to.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets what to do with the track.</summary>
    public AudioAction Action { get; set; } = AudioAction.Copy;

    /// <summary>Gets or sets the target encoder, e.g. libopus.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the target bitrate in bits per second.</summary>
    public long? BitrateBps { get; set; }

    /// <summary>Gets or sets the target channel count. Null keeps the source layout.</summary>
    public int? Channels { get; set; }
}

/// <summary>A fully-specified conversion, as submitted by the dialog.</summary>
public class EncodeRequest
{
    /// <summary>Gets or sets the Jellyfin item to convert.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the strategy the user picked. Informational; the explicit fields win.</summary>
    public OptimizationStrategy Strategy { get; set; } = OptimizationStrategy.Custom;

    /// <summary>Gets or sets the output container, e.g. mkv or mp4.</summary>
    public string Container { get; set; } = "mkv";

    /// <summary>Gets or sets what to do with the video stream.</summary>
    public VideoAction Video { get; set; } = VideoAction.Copy;

    /// <summary>Gets or sets the video encoder, e.g. libx265.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the target height. Null keeps the source resolution.</summary>
    public int? TargetHeight { get; set; }

    /// <summary>Gets or sets an explicit target width. Normally left null so aspect is preserved.</summary>
    public int? TargetWidth { get; set; }

    /// <summary>Gets or sets the target bit depth, 8 or 10.</summary>
    public int? BitDepth { get; set; }

    /// <summary>Gets or sets how the encoder is driven.</summary>
    public RateControlMode RateControl { get; set; } = RateControlMode.ConstantQuality;

    /// <summary>Gets or sets the CRF/CQ value for constant-quality mode.</summary>
    public int? Quality { get; set; }

    /// <summary>Gets or sets the target video bitrate for ABR mode.</summary>
    public long? VideoBitrateBps { get; set; }

    /// <summary>Gets or sets the desired output size in bytes for two-pass mode.</summary>
    public long? TargetSizeBytes { get; set; }

    /// <summary>Gets or sets the encoder preset, e.g. medium.</summary>
    public string? Preset { get; set; }

    /// <summary>Gets or sets a value indicating whether hardware encoding may be used.</summary>
    public bool UseHardware { get; set; }

    /// <summary>Gets or sets the per-track audio instructions.</summary>
    public IReadOnlyList<AudioTrackRequest> AudioTracks { get; set; } = Array.Empty<AudioTrackRequest>();

    /// <summary>Gets or sets the subtitle stream indexes to keep. Null keeps all.</summary>
    public IReadOnlyList<int>? KeepSubtitleIndexes { get; set; }

    /// <summary>Gets or sets a value indicating whether font/cover attachments are carried over.</summary>
    public bool KeepAttachments { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether chapters are carried over.</summary>
    public bool KeepChapters { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether filler NAL units are stripped. Bit-exact.</summary>
    public bool StripFillerData { get; set; }

    /// <summary>Gets or sets a value indicating whether the user accepted losing Dolby Vision.</summary>
    public bool AcceptDolbyVisionLoss { get; set; }

    /// <summary>Gets or sets a value indicating whether the user accepted losing Atmos/DTS:X objects.</summary>
    public bool AcceptObjectAudioLoss { get; set; }

    /// <summary>Gets or sets where the result is placed.</summary>
    public OutputPolicy OutputPolicy { get; set; } = OutputPolicy.Sidecar;
}
