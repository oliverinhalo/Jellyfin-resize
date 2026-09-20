using System;
using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Move;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// Moving library files between drives. Separate from the conversion API on purpose: a move
/// changes where a file lives and never touches a single byte of it, so it has its own queue,
/// its own history and its own page.
/// <para>
/// Every endpoint here requires an administrator. Even reading the list of locations exposes the
/// server's folder layout and how full each drive is.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MediaOptimizer/Move")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaMoveController : ControllerBase
{
    private readonly IMediaMoveService _moves;
    private readonly IMoveJobStore _store;
    private readonly IMoveQueueService _queue;
    private readonly ILogger<MediaMoveController> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaMoveController"/> class.</summary>
    /// <param name="moves">Move planning service.</param>
    /// <param name="store">Move job store.</param>
    /// <param name="queue">Move queue.</param>
    /// <param name="logger">Logger.</param>
    public MediaMoveController(
        IMediaMoveService moves,
        IMoveJobStore store,
        IMoveQueueService queue,
        ILogger<MediaMoveController> logger)
    {
        _moves = moves;
        _store = store;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// Lists every folder media can be moved between — one entry per library folder, plus any
    /// extra destinations configured in the plugin settings.
    /// </summary>
    /// <param name="includeUsage">Whether to count what currently lives in each folder.</param>
    /// <returns>The locations.</returns>
    [HttpGet("Locations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MoveLocation>> GetLocations([FromQuery] bool includeUsage = true) =>
        Ok(_moves.GetLocations(includeUsage));

    /// <summary>
    /// Describes where one file is now. The in-app dialog opens on this rather than on the
    /// analysis endpoint, which runs ffprobe over the file for information a move does not need.
    /// </summary>
    /// <param name="itemId">The library item.</param>
    /// <returns>The file's current home.</returns>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MoveItemInfo> GetItem([FromRoute] Guid itemId)
    {
        var info = _moves.Describe(itemId);
        return info is null ? NotFound() : Ok(info);
    }

    /// <summary>Says what a move would do, without moving anything.</summary>
    /// <param name="request">The files and the destination.</param>
    /// <returns>The plan, including anything that would stop it.</returns>
    [HttpPost("Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MovePlan> Preview([FromBody] MoveRequest request) => Ok(_moves.Plan(request));

    /// <summary>Queues the move.</summary>
    /// <param name="request">The files and the destination.</param>
    /// <returns>What was queued, and what was skipped.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<MoveQueueResult> Create([FromBody] MoveRequest request)
    {
        var plan = _moves.Plan(request);
        if (!plan.IsRunnable)
        {
            return BadRequest(new
            {
                error = plan.Blockers.Count > 0 ? plan.Blockers[0] : "There is nothing to move.",
                blockers = plan.Blockers,
                items = plan.Items
            });
        }

        var result = _moves.Queue(request);

        _logger.LogInformation(
            "[MediaOptimizer] {Count} file(s) queued to move to {Destination}",
            result.QueuedCount,
            plan.DestinationPath);

        return Ok(result);
    }

    /// <summary>Lists the move queue and its history.</summary>
    /// <returns>All known move jobs, newest first.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MoveJob>> GetJobs() => Ok(_store.GetAll());

    /// <summary>Gets one move job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job.</returns>
    [HttpGet("Jobs/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MoveJob> GetJob([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Cancels a queued or running move. The file is left where it is.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelJob([FromRoute] Guid id) =>
        _queue.Cancel(id) ? NoContent() : NotFound();

    /// <summary>Removes a finished move from the history.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}/History")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteJob([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        if (job.IsActive)
        {
            return BadRequest(new { error = "Cancel the move before removing it from the history." });
        }

        _store.Remove(id);
        return NoContent();
    }

    /// <summary>Requeues a failed, cancelled or interrupted move with the same destination.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The new job.</returns>
    [HttpPost("Jobs/{id}/Requeue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MoveQueueResult> RequeueJob([FromRoute] Guid id)
    {
        var original = _store.Get(id);
        if (original is null)
        {
            return NotFound();
        }

        if (original.IsActive)
        {
            return BadRequest(new { error = "This move has not finished yet." });
        }

        // Re-plan rather than replaying the old job: the destination may have filled up, and the
        // file may have been converted — and so renamed — while this job sat in the history.
        var request = new MoveRequest
        {
            ItemIds = [original.ItemId],
            DestinationPath = original.DestinationRoot
        };

        var plan = _moves.Plan(request);
        if (!plan.IsRunnable)
        {
            return BadRequest(new
            {
                error = plan.Blockers.Count > 0
                    ? plan.Blockers[0]
                    : plan.Items.Count > 0 ? plan.Items[0].SkippedReason : "This file can no longer be moved.",
                blockers = plan.Blockers
            });
        }

        return Ok(_moves.Queue(request));
    }
}
