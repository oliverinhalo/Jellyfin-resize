using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// The plugin's API. Reads are available to any signed-in user when the admin allows it;
/// anything that starts, cancels or reverses a conversion requires an administrator, because
/// these operations permanently rewrite files in the library.
/// </summary>
[ApiController]
[Authorize]
[Route("MediaOptimizer")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaOptimizerController : ControllerBase
{
    private readonly IMediaProbeService _probe;
    private readonly ICapabilityService _capabilities;
    private readonly IEncodePlanner _planner;
    private readonly ISizeEstimator _estimator;
    private readonly IJobStore _store;
    private readonly IJobQueueService _queue;
    private readonly IOutputPolicyService _output;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaOptimizerController> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaOptimizerController"/> class.</summary>
    /// <param name="probe">Probe service.</param>
    /// <param name="capabilities">Capability service.</param>
    /// <param name="planner">Encode planner.</param>
    /// <param name="estimator">Size estimator.</param>
    /// <param name="store">Job store.</param>
    /// <param name="queue">Job queue.</param>
    /// <param name="output">Output policy service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public MediaOptimizerController(
        IMediaProbeService probe,
        ICapabilityService capabilities,
        IEncodePlanner planner,
        ISizeEstimator estimator,
        IJobStore store,
        IJobQueueService queue,
        IOutputPolicyService output,
        ILibraryManager libraryManager,
        ILogger<MediaOptimizerController> logger)
    {
        _probe = probe;
        _capabilities = capabilities;
        _planner = planner;
        _estimator = estimator;
        _store = store;
        _queue = queue;
        _output = output;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private bool IsAdmin => User.IsInRole("Administrator");

    /// <summary>
    /// Finds convertible library items, so a conversion can be started from the dashboard
    /// without depending on the injected in-app UI.
    /// </summary>
    /// <param name="query">Optional search text. Empty returns the most recently added items.</param>
    /// <param name="limit">Maximum results.</param>
    /// <returns>Matching movies and episodes.</returns>
    [HttpGet("Library/Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibraryItemSummary>> SearchLibrary(
        [FromQuery] string? query,
        [FromQuery] int limit = 40)
    {
        var itemQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false,
            Limit = Math.Clamp(limit, 1, 200),
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)]
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            itemQuery.SearchTerm = query.Trim();
        }

        var results = new List<LibraryItemSummary>();

        foreach (var item in _libraryManager.GetItemList(itemQuery))
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            results.Add(new LibraryItemSummary
            {
                Id = item.Id,
                Name = BuildDisplayName(item),
                Type = item.GetType().Name,
                Path = item.Path,
                SizeBytes = TryGetSize(item.Path),
                RunTimeTicks = item.RunTimeTicks,
                HasActiveJob = _store.HasActiveJobForItem(item.Id)
            });
        }

        return Ok(results);
    }

    /// <summary>Builds a name that identifies an episode, not just its title.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>A display name.</returns>
    private static string BuildDisplayName(BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var series = episode.SeriesName;
            var season = episode.ParentIndexNumber;
            var number = episode.IndexNumber;

            if (!string.IsNullOrEmpty(series) && season is not null && number is not null)
            {
                return FormattableString.Invariant($"{series} — S{season:00}E{number:00} — {episode.Name}");
            }

            if (!string.IsNullOrEmpty(series))
            {
                return FormattableString.Invariant($"{series} — {episode.Name}");
            }
        }

        if (item.ProductionYear is not null)
        {
            return FormattableString.Invariant($"{item.Name} ({item.ProductionYear})");
        }

        return item.Name ?? "Untitled";
    }

    private static long? TryGetSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Describes a file as it currently exists on disk.</summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analysis.</returns>
    [HttpGet("Analyze/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FileAnalysis>> Analyze(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin && !(Plugin.Instance?.Configuration.AllowNonAdminAnalysis ?? true))
        {
            return Forbid();
        }

        var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        analysis.HasActiveJob = _store.HasActiveJobForItem(itemId);

        var previous = _store.GetAll()
            .FirstOrDefault(j => j.ItemId == itemId && j.Status == JobStatus.Completed);
        if (previous is not null)
        {
            analysis.OptimizationHistory = FormattableString.Invariant(
                $"This item was already optimised by this plugin on {previous.FinishedAt:yyyy-MM-dd}. Re-encoding an encode compounds quality loss.");
        }

        return Ok(analysis);
    }

    /// <summary>Reports what this server's ffmpeg can actually do.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capabilities.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<Capabilities>> GetCapabilities(CancellationToken cancellationToken)
    {
        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        caps.CanConvert = IsAdmin;
        return Ok(caps);
    }

    /// <summary>Predicts the outcome of a conversion without running it.</summary>
    /// <param name="request">The proposed settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The estimate and any warnings.</returns>
    [HttpPost("Estimate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EstimateResult>> Estimate(
        [FromBody] EncodeRequest request,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        // The planner needs a destination to build a complete argument vector; nothing is written.
        var plan = await _planner
            .PlanAsync(analysis, request, "/dev/null", cancellationToken)
            .ConfigureAwait(false);

        return Ok(_estimator.Estimate(analysis, request, plan));
    }

    /// <summary>Measures a stream's exact bitrate. Reads the whole file, so it is opt-in.</summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measured video bitrate.</returns>
    [HttpGet("MeasureBitrate/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BitrateInfo>> MeasureBitrate(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (analysis?.Video is null || analysis.DurationSeconds is not > 0)
        {
            return NotFound();
        }

        var bps = await _probe
            .MeasureBitrateAsync(analysis.Path, "v:0", analysis.DurationSeconds.Value, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new BitrateInfo
        {
            Bps = bps,
            Source = bps is not null ? ValueSource.Measured : ValueSource.Unknown
        });
    }

    /// <summary>Lists the queue and recent history.</summary>
    /// <returns>All known jobs, newest first.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<EncodeJob>> GetJobs() => Ok(_store.GetAll());

    /// <summary>Gets one job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job.</returns>
    [HttpGet("Jobs/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EncodeJob> GetJob([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Queues a conversion.</summary>
    /// <param name="request">The settings to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The queued job.</returns>
    [HttpPost("Jobs")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EncodeJob>> CreateJob(
        [FromBody] EncodeRequest request,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        if (!analysis.IsEligible)
        {
            return BadRequest(new { error = analysis.IneligibleReason });
        }

        if (_store.HasActiveJobForItem(request.ItemId))
        {
            return BadRequest(new { error = "This item already has a conversion queued or running." });
        }

        // Validate before queueing so blockers surface immediately rather than on a worker
        // thread minutes later.
        var plan = await _planner
            .PlanAsync(analysis, request, "/dev/null", cancellationToken)
            .ConfigureAwait(false);

        if (!plan.IsRunnable)
        {
            var blockers = plan.Warnings.Where(w => w.Level == WarningLevel.Blocker).ToList();
            return BadRequest(new { error = blockers[0].Message, warnings = blockers });
        }

        var job = new EncodeJob
        {
            ItemId = request.ItemId,
            ItemName = analysis.Name,
            SourcePath = analysis.Path,
            SourceSizeBytes = analysis.SizeBytes,
            Request = request,
            OutputPolicy = request.OutputPolicy,
            Warnings = plan.Warnings,
            IsLossless = plan.IsLossless
        };

        _store.Add(job);

        _logger.LogInformation(
            "[MediaOptimizer] Queued job {JobId} for {Name} ({Policy})",
            job.Id,
            job.ItemName,
            job.OutputPolicy);

        return Ok(job);
    }

    /// <summary>Cancels a queued or running job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelJob([FromRoute] Guid id) =>
        _queue.Cancel(id) ? NoContent() : NotFound();

    /// <summary>Deletes a finished job from the history.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}/History")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DeleteJob([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        if (job.IsActive)
        {
            return BadRequest(new { error = "Cancel the job before removing it from the history." });
        }

        if (job.CanRevert)
        {
            return BadRequest(new { error = "This job still holds the original file in quarantine. Revert it, or wait for the retention period to expire." });
        }

        _store.Remove(id);
        return NoContent();
    }

    /// <summary>Restores the original file for a completed Replace job.</summary>
    /// <param name="id">Job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated job.</returns>
    [HttpPost("Jobs/{id}/Revert")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EncodeJob>> RevertJob(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var job = _store.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        if (!job.CanRevert)
        {
            return BadRequest(new { error = "This job cannot be reverted. The original is no longer in quarantine." });
        }

        try
        {
            await _output.RevertAsync(job, cancellationToken).ConfigureAwait(false);
            _store.Update(job);
            return Ok(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Revert failed for job {JobId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Requeues an interrupted or failed job with the same settings.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The new job.</returns>
    [HttpPost("Jobs/{id}/Requeue")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EncodeJob> RequeueJob([FromRoute] Guid id)
    {
        var original = _store.Get(id);
        if (original is null)
        {
            return NotFound();
        }

        if (_store.HasActiveJobForItem(original.ItemId))
        {
            return BadRequest(new { error = "This item already has a conversion queued or running." });
        }

        var job = new EncodeJob
        {
            ItemId = original.ItemId,
            ItemName = original.ItemName,
            SourcePath = original.SourcePath,
            Request = original.Request,
            OutputPolicy = original.OutputPolicy
        };

        _store.Add(job);
        return Ok(job);
    }
}
