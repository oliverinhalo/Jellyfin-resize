using System;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>Where a relocation is in its lifecycle.</summary>
public enum MoveStatus
{
    /// <summary>Waiting for a worker.</summary>
    Queued = 0,

    /// <summary>Re-checking the file, the destination and the free space.</summary>
    Preflight = 1,

    /// <summary>Bytes are being written to the destination drive.</summary>
    Copying = 2,

    /// <summary>Comparing the copy against the original before anything is deleted.</summary>
    Verifying = 3,

    /// <summary>Moving companion files and repointing the library entry.</summary>
    Finalizing = 4,

    /// <summary>Finished. The file now lives on the destination drive.</summary>
    Completed = 5,

    /// <summary>Failed. The original was left exactly where it was.</summary>
    Failed = 6,

    /// <summary>Cancelled by a user. The original was left exactly where it was.</summary>
    Cancelled = 7,

    /// <summary>The server stopped mid-move.</summary>
    Interrupted = 8
}

/// <summary>A queued, running or finished relocation of one media file to another drive.</summary>
public class MoveJob
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the Jellyfin item being moved.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name, cached for display.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets where the file was when the job was queued.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the library folder the source was found under, when it was in one.</summary>
    public string? SourceRoot { get; set; }

    /// <summary>Gets or sets the library folder the file is being moved into.</summary>
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path the file is being moved to.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the size of the file in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets how many bytes have been written to the destination so far.</summary>
    public long BytesCopied { get; set; }

    /// <summary>Gets or sets completion between 0 and 100.</summary>
    public double ProgressPercent { get; set; }

    /// <summary>Gets or sets the current copy throughput in bytes per second.</summary>
    public double? BytesPerSecond { get; set; }

    /// <summary>Gets or sets the estimated seconds remaining.</summary>
    public double? EtaSeconds { get; set; }

    /// <summary>Gets or sets the current status.</summary>
    public MoveStatus Status { get; set; } = MoveStatus.Queued;

    /// <summary>Gets or sets when the job was queued.</summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the job started running.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets when the job finished.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the failure message, when the job failed.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the move was a rename within one drive rather
    /// than a copy. A rename is instant however large the file is.
    /// </summary>
    public bool WasInstantRename { get; set; }

    /// <summary>Gets or sets a value indicating whether the copy was checked against the original by hash.</summary>
    public bool? HashVerified { get; set; }

    /// <summary>Gets or sets how many companion files (artwork, .nfo, subtitles) travelled with the media file.</summary>
    public int CompanionFilesMoved { get; set; }

    /// <summary>Gets a value indicating whether the job is still active.</summary>
    public bool IsActive =>
        Status is MoveStatus.Queued or MoveStatus.Preflight or MoveStatus.Copying
            or MoveStatus.Verifying or MoveStatus.Finalizing;
}
