using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Web;

/// <summary>
/// Gets the client script into the served web UI.
/// <para>
/// Jellyfin has no supported way for a server plugin to extend the web client, so this either
/// registers with the File Transformation plugin (non-destructive, preferred) or, if the admin
/// explicitly opts in, patches index.html on disk. Both paths are unsupported by upstream; the
/// dashboard pages work regardless of whether either succeeds.
/// </para>
/// </summary>
public partial class WebInjectionHostedService : IHostedService
{
    /// <summary>The marker that identifies our injected block, so it can be found and replaced.</summary>
    internal const string MarkerStart = "<!-- MediaOptimizer:start -->";

    /// <summary>Closing marker for the injected block.</summary>
    internal const string MarkerEnd = "<!-- MediaOptimizer:end -->";

    private readonly IServerApplicationHost _appHost;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebInjectionHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="WebInjectionHostedService"/> class.</summary>
    /// <param name="appHost">Server host, used to reach the local API.</param>
    /// <param name="appPaths">Application paths, for locating jellyfin-web.</param>
    /// <param name="logger">Logger.</param>
    public WebInjectionHostedService(
        IServerApplicationHost appHost,
        IApplicationPaths appPaths,
        ILogger<WebInjectionHostedService> logger)
    {
        _appHost = appHost;
        _appPaths = appPaths;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The script tag inserted into index.html.</summary>
    /// <returns>An HTML fragment.</returns>
    internal static string BuildScriptTag() =>
        MarkerStart
        + "<script async defer src=\"/MediaOptimizer/client.js\"></script>"
        + MarkerEnd;

    /// <summary>
    /// Inserts the script tag before the closing body tag, replacing any block we added before.
    /// </summary>
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
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        switch (Config.Injection)
        {
            case InjectionMode.Disabled:
                _logger.LogInformation("[MediaOptimizer] In-app UI injection is switched off; dashboard pages only");
                return;

            case InjectionMode.FileTransformation:
                await RegisterWithFileTransformationAsync(cancellationToken).ConfigureAwait(false);
                return;

            case InjectionMode.PatchIndexHtml:
                PatchIndexHtml();
                return;

            default:
                return;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [GeneratedRegex(@"<!-- MediaOptimizer:start -->.*?<!-- MediaOptimizer:end -->", RegexOptions.Singleline)]
    private static partial Regex ExistingBlockRegex();

    private async Task RegisterWithFileTransformationAsync(CancellationToken cancellationToken)
    {
        // File Transformation exposes an HTTP registration endpoint. Calling it over the loopback
        // API avoids taking a compile-time dependency on another plugin's assembly, which would
        // make this plugin fail to load whenever that plugin is absent or a different version.
        var baseUrl = GetLocalApiUrl();
        if (baseUrl is null)
        {
            _logger.LogWarning("[MediaOptimizer] Could not determine the local API address; the in-app UI will not be injected");
            return;
        }

        var payload = new
        {
            id = Plugin.Instance?.Id.ToString() ?? Guid.Empty.ToString(),
            fileNamePattern = "index.html",
            transformationEndpoint = "/MediaOptimizer/Transform"
        };

        // The server is still starting when hosted services run, so the first attempts can
        // legitimately fail; back off and retry rather than giving up on the UI entirely.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url = baseUrl.TrimEnd('/') + "/FileTransformation/RegisterTransformation";

                using var response = await client
                    .PostAsJsonAsync(url, payload, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[MediaOptimizer] Registered the client script with the File Transformation plugin");
                    return;
                }

                _logger.LogDebug(
                    "[MediaOptimizer] File Transformation registration attempt {Attempt} returned {Status}",
                    attempt,
                    response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "[MediaOptimizer] File Transformation registration attempt {Attempt} failed", attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt * 3), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "[MediaOptimizer] Could not register with the File Transformation plugin. Install it from "
            + "https://github.com/IAmParadox27/jellyfin-plugin-file-transformation to get the in-app dialog, "
            + "or switch this plugin's injection mode. Everything still works from Dashboard -> Media Optimizer.");
    }

    private void PatchIndexHtml()
    {
        var indexPath = FindIndexHtml();
        if (indexPath is null)
        {
            _logger.LogWarning("[MediaOptimizer] Could not locate jellyfin-web/index.html to patch");
            return;
        }

        try
        {
            var html = File.ReadAllText(indexPath);
            var patched = InjectInto(html);

            if (string.Equals(html, patched, StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(indexPath, patched, Encoding.UTF8);
            _logger.LogWarning(
                "[MediaOptimizer] Patched {Path} on disk. This will be undone by every Jellyfin update; "
                + "the File Transformation plugin is the non-destructive alternative.",
                indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "[MediaOptimizer] Could not patch {Path}. This is expected on read-only or rootless "
                + "container images; use the File Transformation plugin instead.",
                indexPath);
        }
    }

    private string? FindIndexHtml()
    {
        var candidates = new[]
        {
            Path.Combine(_appPaths.WebPath, "index.html"),
            "/usr/share/jellyfin/web/index.html",
            "/jellyfin/jellyfin-web/index.html"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? GetLocalApiUrl()
    {
        try
        {
            var published = _appHost.GetApiUrlForLocalAccess();
            if (!string.IsNullOrEmpty(published))
            {
                return published;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] GetApiUrlForLocalAccess failed");
        }

        return string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_appHost.HttpPort}");
    }
}
