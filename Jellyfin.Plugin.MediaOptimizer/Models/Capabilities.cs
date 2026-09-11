using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>One encoder this server's ffmpeg can actually use.</summary>
public class EncoderOption
{
    /// <summary>Gets or sets the ffmpeg encoder name, e.g. libx265.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the label shown in the dropdown.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec family, e.g. hevc.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this encoder uses hardware.</summary>
    public bool IsHardware { get; set; }

    /// <summary>Gets or sets a value indicating whether 10-bit output is supported.</summary>
    public bool Supports10Bit { get; set; }

    /// <summary>Gets or sets the presets this encoder accepts.</summary>
    public IReadOnlyList<string> Presets { get; set; } = Array.Empty<string>();
}

/// <summary>What this particular server can do, discovered at startup.</summary>
public class Capabilities
{
    /// <summary>Gets or sets the ffmpeg version string.</summary>
    public string? FfmpegVersion { get; set; }

    /// <summary>Gets or sets the resolved ffmpeg binary path.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Gets or sets the available video encoders.</summary>
    public IReadOnlyList<EncoderOption> VideoEncoders { get; set; } = Array.Empty<EncoderOption>();

    /// <summary>Gets or sets the available audio encoders.</summary>
    public IReadOnlyList<EncoderOption> AudioEncoders { get; set; } = Array.Empty<EncoderOption>();

    /// <summary>Gets or sets the output containers offered.</summary>
    public IReadOnlyList<string> Containers { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the hardware acceleration type configured on the server.</summary>
    public string? HardwareAcceleration { get; set; }

    /// <summary>Gets or sets the VAAPI render node configured on the server.</summary>
    public string? VaapiDevice { get; set; }

    /// <summary>Gets or sets a value indicating whether the server permits HEVC encoding.</summary>
    public bool AllowHevcEncoding { get; set; }

    /// <summary>Gets or sets a value indicating whether the server permits AV1 encoding.</summary>
    public bool AllowAv1Encoding { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller may start conversions.</summary>
    public bool CanConvert { get; set; }

    /// <summary>
    /// Gets or sets the reason no encoders could be discovered, or null when discovery worked.
    /// Shown in the dialog so a server whose ffmpeg cannot be reached says so, instead of
    /// presenting an empty codec list.
    /// </summary>
    public string? ProbeError { get; set; }
}
