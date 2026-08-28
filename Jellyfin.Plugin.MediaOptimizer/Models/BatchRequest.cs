using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaOptimizer.Configuration;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>Apply one strategy to many items at once.</summary>
public class BatchRequest
{
    /// <summary>Gets or sets the items to convert.</summary>
    public IReadOnlyList<Guid> ItemIds { get; set; } = Array.Empty<Guid>();

    /// <summary>Gets or sets the strategy applied to every item.</summary>
    public OptimizationStrategy Strategy { get; set; } = OptimizationStrategy.Standard;

    /// <summary>Gets or sets an explicit target height, overriding the strategy's own choice.</summary>
    public int? TargetHeight { get; set; }

    /// <summary>Gets or sets the output container. Null keeps each item's recommended container.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets where results are placed.</summary>
    public OutputPolicy? OutputPolicy { get; set; }

    /// <summary>Gets or sets a value indicating whether hardware encoding may be used.</summary>
    public bool UseHardware { get; set; }

    /// <summary>Gets or sets a value indicating whether losing Dolby Vision is accepted, per item.</summary>
    public bool AcceptDolbyVisionLoss { get; set; }

    /// <summary>
    /// Gets or sets the audio languages to keep across the whole batch, overriding the plugin
    /// default. Empty string keeps every track; null uses the configured default.
    /// </summary>
    public string? KeepAudioLanguages { get; set; }

    /// <summary>Gets or sets the subtitle languages to keep across the whole batch.</summary>
    public string? KeepSubtitleLanguages { get; set; }
}

/// <summary>What happened to one item in a batch.</summary>
public class BatchItemResult
{
    /// <summary>Gets or sets the item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a job was queued.</summary>
    public bool Queued { get; set; }

    /// <summary>Gets or sets the job id, when one was created.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Gets or sets why the item was skipped.</summary>
    public string? SkippedReason { get; set; }

    /// <summary>Gets or sets the predicted saving in bytes, when the item was queued.</summary>
    public long? EstimatedSavingBytes { get; set; }
}

/// <summary>The outcome of a batch submission.</summary>
public class BatchResult
{
    /// <summary>Gets or sets how many jobs were queued.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Gets or sets how many items were skipped.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Gets or sets the total predicted saving across the queued jobs.</summary>
    public long EstimatedSavingBytes { get; set; }

    /// <summary>Gets or sets the per-item outcomes.</summary>
    public IReadOnlyList<BatchItemResult> Items { get; set; } = Array.Empty<BatchItemResult>();
}
