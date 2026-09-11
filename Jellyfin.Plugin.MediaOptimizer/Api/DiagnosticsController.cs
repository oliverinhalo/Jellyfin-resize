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
        AddSpeedCheck(checks);
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
                        "Found at {0}, but it reported no usable video encoders. {1}",
                        caps.FfmpegPath,
                        caps.ProbeError ?? "The binary may be missing or not executable.")));
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

    /// <summary>
    /// Explains the encoding speed people actually get, and which of the four levers is still
    /// available to them. Software x265 at 4K is inherently slow; the useful thing is to say so
    /// with numbers and name the fix rather than leave it looking broken.
    /// </summary>
    /// <param name="checks">The list being built.</param>
    private void AddSpeedCheck(List<DiagnosticCheck> checks)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var notes = new List<string>();
        var status = CheckStatus.Ok;

        var measured = _store.GetAll()
            .Where(j => j.Status == JobStatus.Completed && j.PixelsPerSecond is > 0)
            .OrderByDescending(j => j.FinishedAt ?? j.QueuedAt)
            .Take(10)
            .Select(j => j.PixelsPerSecond!.Value)
            .ToList();

        if (measured.Count > 0)
        {
            var pps = measured.Average();
            var hours1080 = 1920d * 1080d * 24d * 7200d / pps / 3600d;
            var hours4k = 3840d * 2160d * 24d * 7200d / pps / 3600d;
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Measured {0:F1} megapixels/second: roughly {1:F1}h for a two-hour 1080p film, {2:F1}h at 4K.",
                pps / 1_000_000d,
                hours1080,
                hours4k));
        }
        else
        {
            notes.Add("No completed encode yet, so this server's speed has not been measured.");
        }

        if (config.LowProcessPriority)
        {
            status = CheckStatus.Warning;
            notes.Add("FFmpeg runs at below-normal priority, which slows encoding whenever anything "
                + "else wants the CPU. Turn it off in the plugin settings if the server is otherwise idle.");
        }

        if (config.PauseWhilePlaybackActive)
        {
            notes.Add("The queue pauses entirely while anyone is streaming, so overnight is when it "
                + "will make progress.");
        }

        if (!config.PreferHardwareEncoding)
        {
            notes.Add("Hardware encoding is off. It is the single biggest speed lever — typically ten "
                + "to twenty times faster — at the cost of a file around 50% larger for the same quality.");
        }

        if (config.Speed == SpeedPreference.SmallestFile)
        {
            notes.Add("Speed is set to \"smallest file\", which uses a slow encoder preset. "
                + "Switch to balanced or fastest to trade a little size for a lot of time.");
        }

        if (config.EncodingThreadCount is > 0 && config.EncodingThreadCount < 4)
        {
            status = CheckStatus.Warning;
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "FFmpeg is capped at {0} thread(s), which throttles it badly. Set 0 to use every core.",
                config.EncodingThreadCount));
        }

        checks.Add(new DiagnosticCheck("Encoding speed", status, string.Join(" ", notes)));
    }

    private void AddQueueCheck(List<DiagnosticCheck> checks)
    {
        try
        {
            var all = _store.GetAll();
            var active = _store.GetActive();

            var resumed = all.Count(j => j.ResumeCount > 0);
            checks.Add(new DiagnosticCheck(
                "Conversion queue",
                JobStore.IsPaused ? CheckStatus.Warning : CheckStatus.Ok,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} active, {1} in history.{2} Every change is written to disk before it is "
                    + "acknowledged, so a crash or a power cut loses nothing and interrupted encodes "
                    + "are picked up again on restart.{3}",
                    active.Count,
                    all.Count,
                    JobStore.IsPaused ? " The queue is paused." : string.Empty,
                    resumed > 0
                        ? string.Format(CultureInfo.InvariantCulture, " {0} job(s) have already been resumed this way.", resumed)
                        : string.Empty)));
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
