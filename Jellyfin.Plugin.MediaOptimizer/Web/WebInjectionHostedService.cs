using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Web;

/// <summary>
/// Gets the client script into the served web UI.
/// <para>
/// Jellyfin has no supported way for a server plugin to extend the web client, so this either
/// registers with the File Transformation plugin (non-destructive, preferred) or, if the admin
/// explicitly opts in, patches index.html on disk. Both are unsupported by upstream.
/// </para>
/// <para>
/// Whatever happens here is recorded on <see cref="Status"/> and surfaced on the dashboard, so a
/// failure reads as a specific, actionable message instead of the plugin appearing to do nothing.
/// Nothing in this class can prevent the rest of the plugin from working.
/// </para>
/// </summary>
public partial class WebInjectionHostedService : IHostedService
{
    /// <summary>The marker identifying our injected block, so it can be found and replaced.</summary>
    internal const string MarkerStart = "<!-- MediaOptimizer:start -->";

    /// <summary>Closing marker for the injected block.</summary>
    internal const string MarkerEnd = "<!-- MediaOptimizer:end -->";

    private readonly IServiceProvider _services;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebInjectionHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="WebInjectionHostedService"/> class.</summary>
    /// <param name="services">Jellyfin's service provider, used to reach the File Transformation service.</param>
    /// <param name="appPaths">Application paths, for locating jellyfin-web.</param>
    /// <param name="logger">Logger.</param>
    public WebInjectionHostedService(
        IServiceProvider services,
        IApplicationPaths appPaths,
        ILogger<WebInjectionHostedService> logger)
    {
        _services = services;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>Gets the outcome of the last injection attempt, for the diagnostics panel.</summary>
    public static InjectionStatus Status { get; private set; } = new InjectionStatus
    {
        Outcome = RegistrationOutcome.Failed,
        Detail = "Startup has not run yet."
    };

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The script tag inserted into index.html.</summary>
    /// <returns>An HTML fragment.</returns>
    internal static string BuildScriptTag() =>
        MarkerStart
        + "<script async defer src=\"/MediaOptimizer/client.js\"></script>"
        + MarkerEnd;

    /// <summary>Inserts the script tag before the closing body tag, replacing any earlier block.</summary>
    /// <param name="html">The current document.</param>
    /// <returns>The document with exactly one injected block.</returns>
    internal static string InjectInto(string html)
    {
        var cleaned = ExistingBlockRegex().Replace(html, string.Empty);

        var closing = cleaned.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closing < 0)
        {
            // No body tag to anchor to; leave the document untouched rather than guessing.
            return cleaned;
        }

        return cleaned[..closing] + BuildScriptTag() + cleaned[closing..];
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            switch (Config.Injection)
            {
                case InjectionMode.Disabled:
                    Status = new InjectionStatus
                    {
                        Outcome = RegistrationOutcome.PluginNotInstalled,
                        Disabled = true,
                        Detail = "In-app injection is switched off in the plugin settings. "
                            + "Conversions can still be started from this dashboard page."
                    };
                    _logger.LogInformation("[MediaOptimizer] In-app UI injection is switched off");
                    break;

                case InjectionMode.FileTransformation:
                    RegisterWithFileTransformation();
                    break;

                case InjectionMode.PatchIndexHtml:
                    PatchIndexHtml();
                    break;

                default:
                    break;
            }
        }
#pragma warning disable CA1031 // Injection must never be able to stop the plugin loading.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "[MediaOptimizer] Web injection failed; the dashboard page still works");
            Status = new InjectionStatus
            {
                Outcome = RegistrationOutcome.Failed,
                Detail = "Web injection failed: " + ex.Message
            };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [GeneratedRegex(@"<!-- MediaOptimizer:start -->.*?<!-- MediaOptimizer:end -->", RegexOptions.Singleline)]
    private static partial Regex ExistingBlockRegex();

    /// <summary>Reads the served document, injects the tag, and writes it back.</summary>
    /// <param name="path">The file being served, for logging.</param>
    /// <param name="contents">Read/write stream of the document.</param>
    /// <returns>A task.</returns>
    private async Task TransformAsync(string path, Stream contents)
    {
        string html;
        using (var reader = new StreamReader(contents, Encoding.UTF8, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var patched = InjectInto(html);

        contents.Seek(0, SeekOrigin.Begin);
        var bytes = Encoding.UTF8.GetBytes(patched);
        await contents.WriteAsync(bytes).ConfigureAwait(false);

        // The document only ever grows, but truncate anyway so a shorter result could never
        // leave trailing bytes from the previous contents.
        if (contents.CanSeek)
        {
            contents.SetLength(bytes.Length);
        }

        _logger.LogDebug("[MediaOptimizer] Injected client script into {Path}", path);
    }

    private void RegisterWithFileTransformation()
    {
        var registrar = new FileTransformationRegistrar(_services, _logger);
        var pluginId = Plugin.Instance?.Id ?? Guid.Empty;

        var result = registrar.Register(pluginId, "index.html", TransformAsync);

        Status = new InjectionStatus
        {
            Outcome = result.Outcome,
            Detail = result.Detail,
            FileTransformationVersion = result.DetectedVersion
        };

        if (result.Outcome != RegistrationOutcome.Registered)
        {
            _logger.LogWarning(
                "[MediaOptimizer] In-app UI unavailable: {Detail} Conversions can still be run from "
                + "Dashboard -> Media Optimizer.",
                result.Detail);
        }
    }

    private void PatchIndexHtml()
    {
        var indexPath = FindIndexHtml();
        if (indexPath is null)
        {
            Status = new InjectionStatus
            {
                Outcome = RegistrationOutcome.Failed,
                Detail = "Could not find jellyfin-web/index.html to patch."
            };
            _logger.LogWarning("[MediaOptimizer] Could not locate jellyfin-web/index.html to patch");
            return;
        }

        try
        {
            var html = File.ReadAllText(indexPath);
            var patched = InjectInto(html);

            if (!string.Equals(html, patched, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, patched, Encoding.UTF8);
                _logger.LogWarning(
                    "[MediaOptimizer] Patched {Path} on disk. Jellyfin updates will undo this; "
                    + "the File Transformation plugin is the non-destructive alternative.",
                    indexPath);
            }

            Status = new InjectionStatus
            {
                Outcome = RegistrationOutcome.Registered,
                Detail = "index.html was patched on disk. A Jellyfin update will undo this — "
                    + "switch to the File Transformation plugin to make it survive updates."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = new InjectionStatus
            {
                Outcome = RegistrationOutcome.Failed,
                Detail = "Could not write to " + indexPath + ". This is normal on read-only or "
                    + "rootless container images; use the File Transformation plugin instead."
            };
            _logger.LogError(ex, "[MediaOptimizer] Could not patch {Path}", indexPath);
        }
    }

    private string? FindIndexHtml()
    {
        string[] candidates =
        [
            Path.Combine(_appPaths.WebPath, "index.html"),
            "/usr/share/jellyfin/web/index.html",
            "/jellyfin/jellyfin-web/index.html"
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>The state of the in-app UI injection, as shown on the dashboard.</summary>
public class InjectionStatus
{
    /// <summary>Gets or sets what happened.</summary>
    public RegistrationOutcome Outcome { get; set; }

    /// <summary>Gets or sets a human-readable explanation.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected File Transformation version, when installed.</summary>
    public string? FileTransformationVersion { get; set; }

    /// <summary>Gets or sets a value indicating whether injection was deliberately switched off.</summary>
    public bool Disabled { get; set; }
}
