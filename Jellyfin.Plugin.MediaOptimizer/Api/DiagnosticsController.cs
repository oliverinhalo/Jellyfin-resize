using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Web;
using MediaBrowser.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// Self-check endpoint. Every part of the plugin is checked independently so that one broken
/// piece reports itself rather than making the whole thing look dead.
/// </summary>
[ApiController]
[Authorize]
[Route("MediaOptimizer")]
[Produces("application/json")]
public class DiagnosticsController : ControllerBase
{
    private readonly ICapabilityService _capabilities;
    private readonly IJobStore _store;
    private readonly IApplicationHost _appHost;
    private readonly ILogger<DiagnosticsController> _logger;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticsController"/> class.</summary>
    /// <param name="capabilities">Capability service.</param>
    /// <param name="store">Job store.</param>
    /// <param name="appHost">Application host, for the server version.</param>
    /// <param name="logger">Logger.</param>
    public DiagnosticsController(
        ICapabilityService capabilities,
        IJobStore store,
        IApplicationHost appHost,
        ILogger<DiagnosticsController> logger)
    {
        _capabilities = capabilities;
        _store = store;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>Runs every self-check and reports the results.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticsReport>> GetDiagnostics(CancellationToken cancellationToken)
    {
        var checks = new List<DiagnosticCheck>();
        var isAdmin = User.IsInRole("Administrator");

        // If this endpoint answered at all, the plugin loaded and its API is routed.
        checks.Add(new DiagnosticCheck(
            "Plugin loaded",
            CheckStatus.Ok,
            string.Format(
                CultureInfo.InvariantCulture,
                "Version {0}. The API is responding, so the assembly loaded and its routes are registered.",
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown")));

        await AddFfmpegChecksAsync(checks, cancellationToken).ConfigureAwait(false);
        AddQueueCheck(checks);
        AddInjectionCheck(checks);
        AddPermissionCheck(checks, isAdmin);

        var overall = checks.Count == 0
            ? CheckStatus.Ok
            : checks.Max(c => c.Status == CheckStatus.Disabled ? CheckStatus.Ok : c.Status);

        return Ok(new DiagnosticsReport
        {
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            ServerVersion = _appHost.ApplicationVersionString,
            IsAdministrator = isAdmin,
            Checks = checks,
            Overall = overall,
            Summary = overall switch
            {
                CheckStatus.Ok => "Everything is working.",
                CheckStatus.Warning => "Working, with something degraded — see below.",
                _ => "Something is broken — see below."
            }
        });
    }

    private async Task AddFfmpegChecksAsync(List<DiagnosticCheck> checks, CancellationToken cancellationToken)
    {
        try
        {
            var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(caps.FfmpegPath))
            {
                checks.Add(new DiagnosticCheck(
                    "FFmpeg",
                    CheckStatus.Failed,
                    "Jellyfin has no FFmpeg path configured. Set one under Dashboard → Playback → Transcoding. "
                    + "Nothing can be converted until this is fixed."));
                return;
            }

            if (caps.VideoEncoders.Count == 0)
            {
                checks.Add(new DiagnosticCheck(
                    "FFmpeg",
                    CheckStatus.Failed,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Found at {0}, but it reported no usable video encoders. The binary may be missing or not executable.",
                        caps.FfmpegPath)));
                return;
            }

            checks.Add(new DiagnosticCheck(
                "FFmpeg",
                CheckStatus.Ok,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} at {1} — {2} video encoders, {3} audio encoders. Hardware acceleration: {4}.",
                    caps.FfmpegVersion ?? "version unknown",
                    caps.FfmpegPath,
                    caps.VideoEncoders.Count,
                    caps.AudioEncoders.Count,
                    string.IsNullOrEmpty(caps.HardwareAcceleration) ? "none" : caps.HardwareAcceleration)));

            var disabled = new List<string>();
            if (!caps.AllowHevcEncoding)
            {
                disabled.Add("HEVC");
            }

            if (!caps.AllowAv1Encoding)
            {
                disabled.Add("AV1");
            }

            if (disabled.Count > 0)
            {
                checks.Add(new DiagnosticCheck(
                    "Server encoding settings",
                    CheckStatus.Warning,
                    string.Join(" and ", disabled)
                    + " encoding is switched off under Dashboard → Playback → Transcoding. "
                    + "Those codecs are still offered here and usually work, but enable them there if a job fails."));
            }
        }
#pragma warning disable CA1031 // A broken probe must still report, not throw the whole page away.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "[MediaOptimizer] FFmpeg capability check failed");
            checks.Add(new DiagnosticCheck("FFmpeg", CheckStatus.Failed, "Probing FFmpeg failed: " + ex.Message));
        }
    }

    private void AddQueueCheck(List<DiagnosticCheck> checks)
    {
        try
        {
            var all = _store.GetAll();
            var active = _store.GetActive();

            checks.Add(new DiagnosticCheck(
                "Conversion queue",
                CheckStatus.Ok,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} active, {1} in history. The queue survives restarts.",
                    active.Count,
                    all.Count)));
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            checks.Add(new DiagnosticCheck("Conversion queue", CheckStatus.Failed, "Could not read the job store: " + ex.Message));
        }
    }

    private static void AddInjectionCheck(List<DiagnosticCheck> checks)
    {
        var status = WebInjectionHostedService.Status;
        var mode = Plugin.Instance?.Configuration.Injection ?? InjectionMode.FileTransformation;

        if (status.Disabled || mode == InjectionMode.Disabled)
        {
            checks.Add(new DiagnosticCheck(
                "In-app buttons",
                CheckStatus.Disabled,
                "Switched off in settings. Start conversions from this page instead."));
            return;
        }

        var checkStatus = status.Outcome switch
        {
            RegistrationOutcome.Registered => CheckStatus.Ok,
            _ => CheckStatus.Warning
        };

        var detail = status.Detail;
        if (checkStatus != CheckStatus.Ok)
        {
            detail += " This only affects the buttons inside the web client — everything on this page works regardless.";
        }
        else
        {
            detail += " If the buttons still do not appear, hard-refresh the browser (Ctrl+Shift+R).";
        }

        checks.Add(new DiagnosticCheck("In-app buttons", checkStatus, detail));
    }

    private static void AddPermissionCheck(List<DiagnosticCheck> checks, bool isAdmin)
    {
        checks.Add(isAdmin
            ? new DiagnosticCheck("Your permissions", CheckStatus.Ok, "You are an administrator, so you can start and revert conversions.")
            : new DiagnosticCheck(
                "Your permissions",
                CheckStatus.Warning,
                "You are not an administrator. You can inspect files, but starting, cancelling and reverting "
                + "conversions is restricted to administrators because it rewrites files in the library."));
    }
}
