using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaOptimizer.Configuration;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>Where a job is in its lifecycle.</summary>
public enum JobStatus
{
    /// <summary>Waiting for a worker.</summary>
    Queued = 0,

    /// <summary>Running preflight checks.</summary>
    Preflight = 1,

    /// <summary>FFmpeg is running.</summary>
    Encoding = 2,

    /// <summary>Checking the produced file before anything is applied.</summary>
    Verifying = 3,

    /// <summary>Moving the result into place.</summary>
    Applying = 4,

    /// <summary>Finished successfully.</summary>
    Completed = 5,

    /// <summary>Failed. The original was never modified.</summary>
    Failed = 6,

    /// <summary>Cancelled by a user.</summary>
    Cancelled = 7,

    /// <summary>The server stopped mid-encode.</summary>
    Interrupted = 8
}

/// <summary>A queued or completed conversion.</summary>
public class EncodeJob
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the Jellyfin item being converted.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name, cached for display.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the source path at the time of queueing.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the request that produced this job.</summary>
    public EncodeRequest Request { get; set; } = new EncodeRequest();

    /// <summary>Gets or sets the current status.</summary>
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Gets or sets completion between 0 and 100.</summary>
    public double ProgressPercent { get; set; }

    /// <summary>Gets or sets the current encoding speed as a multiple of realtime.</summary>
    public double? Speed { get; set; }

    /// <summary>Gets or sets the estimated seconds remaining.</summary>
    public double? EtaSeconds { get; set; }

    /// <summary>Gets or sets when the job was queued.</summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the job started running.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets when the job finished.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the source size in bytes.</summary>
    public long? SourceSizeBytes { get; set; }

    /// <summary>Gets or sets the produced size in bytes.</summary>
    public long? OutputSizeBytes { get; set; }

    /// <summary>Gets or sets the final path of the produced file.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets where the original was moved for a Replace job.</summary>
    public string? QuarantinePath { get; set; }

    /// <summary>Gets or sets when the quarantined original becomes eligible for deletion.</summary>
    public DateTime? QuarantineExpiresAt { get; set; }

    /// <summary>Gets or sets the failure message, when the job failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the planner messages captured at queue time.</summary>
    public IReadOnlyList<PlanWarning> Warnings { get; set; } = Array.Empty<PlanWarning>();

    /// <summary>Gets or sets a value indicating whether this job claimed to be bit-exact.</summary>
    public bool IsLossless { get; set; }

    /// <summary>Gets or sets the result of the lossless hash comparison, when one ran.</summary>
    public bool? LosslessVerified { get; set; }

    /// <summary>
    /// Gets or sets encoded pixels per second measured on this job. Feeds the time estimate for
    /// later jobs, so predictions come from this server's real speed rather than a guess.
    /// </summary>
    public double? PixelsPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the queue position weight. Lower runs first; ties fall back to arrival order.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets how many times this job has been resumed after a server restart.</summary>
    public int ResumeCount { get; set; }

    /// <summary>Gets or sets the policy that was applied.</summary>
    public OutputPolicy OutputPolicy { get; set; }

    /// <summary>Gets a value indicating whether the job is still active.</summary>
    public bool IsActive =>
        Status is JobStatus.Queued or JobStatus.Preflight or JobStatus.Encoding
            or JobStatus.Verifying or JobStatus.Applying;

    /// <summary>Gets a value indicating whether the original can still be restored.</summary>
    public bool CanRevert =>
        Status == JobStatus.Completed
        && OutputPolicy is OutputPolicy.Replace or OutputPolicy.ReplaceAndDelete
        && !string.IsNullOrEmpty(QuarantinePath);
}
