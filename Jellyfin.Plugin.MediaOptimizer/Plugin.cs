using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaOptimizer;

/// <summary>
/// Adds an in-interface media file analyser and FFmpeg conversion tool to Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Configuration serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Media Optimizer";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("919929b2-0857-4c61-8647-c05fbbdc62fa");

    /// <inheritdoc />
    public override string Description =>
        "Inspect any media file from inside Jellyfin and convert it with FFmpeg: resolution, codec, "
        + "bit depth, bitrate and audio presets, plus a genuinely lossless mode. Also moves media "
        + "between drives, copying and checking each file before the original is removed. The in-app "
        + "dialogs require the browser-based web client; every feature is also available from this dashboard.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", ns)
            },
            new PluginPageInfo
            {
                Name = "MediaOptimizerMove",
                DisplayName = "Move Media",
                EnableInMainMenu = true,
                MenuIcon = "drive_file_move",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.movePage.html", ns)
            },
            new PluginPageInfo
            {
                Name = "MediaOptimizerQueue",
                DisplayName = "Media Optimizer",
                EnableInMainMenu = true,
                MenuIcon = "tune",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.queuePage.html", ns)
            }
        ];
    }
}
