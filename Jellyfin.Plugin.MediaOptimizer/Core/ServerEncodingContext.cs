using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>The server's own encoding settings, as they are right now.</summary>
public class ServerEncodingSettings
{
    /// <summary>Gets or sets the configured hardware acceleration type.</summary>
    public string? HardwareAcceleration { get; set; }

    /// <summary>Gets or sets the configured VAAPI device.</summary>
    public string? VaapiDevice { get; set; }

    /// <summary>Gets or sets a value indicating whether the server permits HEVC encoding.</summary>
    public bool AllowHevcEncoding { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the server permits AV1 encoding.</summary>
    public bool AllowAv1Encoding { get; set; } = true;
}

/// <summary>
/// Everything the capability probe needs from the Jellyfin server around it, behind one seam.
/// <para>
/// It exists so the probe can be tested at all — <see cref="IMediaEncoder"/> alone is far too
/// large to stand in for — and because these are two different kinds of fact with two different
/// lifetimes: what ffmpeg can do changes when the binary changes, and what the server permits
/// changes the moment an administrator clicks a checkbox.
/// </para>
/// </summary>
public interface IServerEncodingContext
{
    /// <summary>Gets the ffmpeg version Jellyfin reports, if it knows one.</summary>
    string? EncoderVersion { get; }

    /// <summary>Gets the server's current encoding settings.</summary>
    /// <returns>The settings, never null.</returns>
    ServerEncodingSettings GetSettings();

    /// <summary>Asks Jellyfin's own encoder whether it has a given encoder.</summary>
    /// <param name="encoder">The encoder name.</param>
    /// <returns>Whether it is available.</returns>
    bool SupportsEncoder(string encoder);
}

/// <inheritdoc />
public class ServerEncodingContext : IServerEncodingContext
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<ServerEncodingContext> _logger;

    /// <summary>Initializes a new instance of the <see cref="ServerEncodingContext"/> class.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder.</param>
    /// <param name="config">Server configuration.</param>
    /// <param name="logger">Logger.</param>
    public ServerEncodingContext(
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager config,
        ILogger<ServerEncodingContext> logger)
    {
        _mediaEncoder = mediaEncoder;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? EncoderVersion => _mediaEncoder.EncoderVersion?.ToString();

    /// <inheritdoc />
    public ServerEncodingSettings GetSettings()
    {
        try
        {
            var encoding = _config.GetEncodingOptions();
            return new ServerEncodingSettings
            {
                HardwareAcceleration = encoding.HardwareAccelerationType.ToString(),
                VaapiDevice = encoding.VaapiDevice,
                AllowHevcEncoding = encoding.AllowHevcEncoding,
                AllowAv1Encoding = encoding.AllowAv1Encoding
            };
        }
#pragma warning disable CA1031 // Settings we cannot read must not take the codec list with them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A server whose encoding options moved or could not be read still has a usable
            // ffmpeg. Assume the codecs are permitted rather than hiding them all.
            _logger.LogWarning(ex, "[MediaOptimizer] Could not read the server's encoding options");
            return new ServerEncodingSettings();
        }
    }

    /// <inheritdoc />
    public bool SupportsEncoder(string encoder)
    {
        try
        {
            return _mediaEncoder.SupportsEncoder(encoder);
        }
#pragma warning disable CA1031 // One unavailable answer must not fail the whole probe.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Jellyfin could not answer for encoder {Encoder}", encoder);
            return false;
        }
    }
}
