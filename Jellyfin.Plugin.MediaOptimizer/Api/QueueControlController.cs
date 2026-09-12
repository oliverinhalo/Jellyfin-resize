using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>Running totals for the dashboard.</summary>
public class OptimizerStatistics
{
    /// <summary>Gets or sets how many conversions have completed.</summary>
    public int CompletedJobs { get; set; }

    /// <summary>Gets or sets total bytes reclaimed across all completed conversions.</summary>
    public long BytesSaved { get; set; }

    /// <summary>Gets or sets the total size of the source files that were converted.</summary>
    public long SourceBytesProcessed { get; set; }

    /// <summary>Gets or sets the average saving as a fraction of the original size.</summary>
    public double AverageSavingFraction { get; set; }

    /// <summary>Gets or sets how many jobs failed.</summary>
    public int FailedJobs { get; set; }

    /// <summary>Gets or sets how many jobs are waiting or running.</summary>
    public int ActiveJobs { get; set; }

    /// <summary>Gets or sets originals still held in quarantine and therefore still restorable.</summary>
    public int RestorableOriginals { get; set; }

    /// <summary>Gets or sets disk still occupied by quarantined originals.</summary>
    public long QuarantineBytes { get; set; }

    /// <summary>Gets or sets measured encoding throughput in megapixels per second, when known.</summary>
    public double? MegapixelsPerSecond { get; set; }

    /// <summary>Gets or sets a plain-language note about how long a typical film would take.</summary>
    public string? SpeedNote { get; set; }

    /// <summary>Gets or sets a value indicating whether the queue is currently paused.</summary>
    public bool IsPaused { get; set; }
}

/// <summary>Pause, resume, reorder and report on the conversion queue.</summary>
[ApiController]
[Authorize]
[Route("MediaOptimizer")]
[Produces("application/json")]
public class QueueControlController : ControllerBase
{
    private readonly IJobStore _store;
    private readonly ILogger<QueueControlController> _logger;

    /// <summary>Initializes a new instance of the <see cref="QueueControlController"/> class.</summary>
    /// <param name="store">Job store.</param>
    /// <param name="logger">Logger.</param>
    public QueueControlController(IJobStore store, ILogger<QueueControlController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Reports totals, including how much disk the plugin has actually reclaimed.</summary>
    /// <returns>The statistics.</returns>
    [HttpGet("Statistics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OptimizerStatistics> GetStatistics()
    {
        var all = _store.GetAll();
        var completed = all.Where(j => j.Status == JobStatus.Completed && j.SourceSizeBytes is > 0 && j.OutputSizeBytes is > 0).ToList();

        var stats = new OptimizerStatistics
        {
            CompletedJobs = all.Count(j => j.Status == JobStatus.Completed),
            FailedJobs = all.Count(j => j.Status == JobStatus.Failed),
            ActiveJobs = all.Count(j => j.IsActive),
            RestorableOriginals = all.Count(j => j.CanRevert),
            IsPaused = _store.IsPaused
        };

        foreach (var job in completed)
        {
            stats.SourceBytesProcessed += job.SourceSizeBytes!.Value;

            // Only count a policy that actually took the original away. A sidecar or an alternate
            // version keeps both files, so nothing was freed. Replace counts even while the
            // original sits in quarantine, because that space comes back on its own; the
            // "still undoable" figure alongside says how much is not free yet.
            if (job.OutputPolicy is Configuration.OutputPolicy.Replace or Configuration.OutputPolicy.ReplaceAndDelete)
            {
                stats.BytesSaved += job.SourceSizeBytes.Value - job.OutputSizeBytes!.Value;
            }
        }

        if (stats.SourceBytesProcessed > 0)
        {
            var outputTotal = completed.Sum(j => j.OutputSizeBytes!.Value);
            stats.AverageSavingFraction = 1d - ((double)outputTotal / stats.SourceBytesProcessed);
        }

        foreach (var job in all.Where(j => j.CanRevert && j.SourceSizeBytes is > 0))
        {
            stats.QuarantineBytes += job.SourceSizeBytes!.Value;
        }

        var throughput = all
            .Where(j => j.Status == JobStatus.Completed && j.PixelsPerSecond is > 0)
            .OrderByDescending(j => j.FinishedAt ?? j.QueuedAt)
            .Take(10)
            .Select(j => j.PixelsPerSecond!.Value)
            .ToList();

        if (throughput.Count > 0)
        {
            var pps = throughput.Average();
            stats.MegapixelsPerSecond = Math.Round(pps / 1_000_000d, 1);

            // A two-hour 1080p film at 24 fps is a yardstick people can reason about.
            var referencePixels = 1920d * 1080d * 24d * 7200d;
            var hours = referencePixels / pps / 3600d;
            stats.SpeedNote = string.Format(
                CultureInfo.InvariantCulture,
                "Measured on this server: about {0:F1} hours to re-encode a two-hour 1080p film.",
                hours);
        }

        return Ok(stats);
    }

    /// <summary>Stops the worker claiming new jobs. A running job finishes first.</summary>
    /// <returns>The new paused state.</returns>
    [HttpPost("Queue/Pause")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Pause()
    {
        _store.IsPaused = true;
        _logger.LogInformation("[MediaOptimizer] Queue paused");
        return Ok(new { paused = true });
    }

    /// <summary>Lets the worker claim jobs again.</summary>
    /// <returns>The new paused state.</returns>
    [HttpPost("Queue/Resume")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Resume()
    {
        _store.IsPaused = false;
        _logger.LogInformation("[MediaOptimizer] Queue resumed");
        return Ok(new { paused = false });
    }

    /// <summary>Moves a queued job to the front so it runs next.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/{id}/MoveToFront")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult MoveToFront([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        var lowest = _store.GetActive().Select(j => j.Priority).DefaultIfEmpty(0).Min();
        job.Priority = lowest - 1;
        _store.Update(job);
        return NoContent();
    }

    /// <summary>Cancels every queued job. Anything already running is left alone.</summary>
    /// <returns>How many were cancelled.</returns>
    [HttpPost("Queue/ClearPending")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ClearPending()
    {
        var cleared = 0;
        foreach (var job in _store.GetActive().Where(j => j.Status == JobStatus.Queued))
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "Cancelled with the rest of the pending queue.";
            job.FinishedAt = DateTime.UtcNow;
            _store.Update(job);
            cleared++;
        }

        _logger.LogInformation("[MediaOptimizer] Cleared {Count} pending job(s)", cleared);
        return Ok(new { cleared });
    }

    /// <summary>
    /// Deletes every quarantined original whose retention has not yet expired, freeing the space
    /// immediately at the cost of no longer being able to undo those conversions.
    /// </summary>
    /// <returns>How many were released and how much space that freed.</returns>
    [HttpPost("Queue/ReleaseQuarantine")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ReleaseQuarantine()
    {
        var released = 0;
        long freed = 0;

        foreach (var job in _store.GetAll().Where(j => j.CanRevert))
        {
            try
            {
                if (System.IO.File.Exists(job.QuarantinePath!))
                {
                    freed += new System.IO.FileInfo(job.QuarantinePath!).Length;
                    System.IO.File.Delete(job.QuarantinePath!);
                }

                job.QuarantinePath = null;
                job.QuarantineExpiresAt = null;
                _store.Update(job);
                released++;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[MediaOptimizer] Could not release quarantined file for job {JobId}", job.Id);
            }
        }

        _logger.LogInformation("[MediaOptimizer] Released {Count} quarantined original(s)", released);
        return Ok(new { released, freedBytes = freed });
    }
}
