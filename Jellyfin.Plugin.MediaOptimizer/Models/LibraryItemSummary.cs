using System;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>A library item as listed in the dashboard's file picker.</summary>
public class LibraryItemSummary
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name, including series and episode number where relevant.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the path on disk.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the duration in ticks.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the container extension.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the video width.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the video height.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the video codec.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the video bitrate, when the container reports one.</summary>
    public int? BitrateBps { get; set; }

    /// <summary>Gets or sets whether the calling user has watched this, when known.</summary>
    public bool? IsWatched { get; set; }

    /// <summary>Gets or sets a value indicating whether a conversion is already queued or running.</summary>
    public bool HasActiveJob { get; set; }

    /// <summary>
    /// Gets or sets roughly what a conversion would reclaim, from size, resolution and codec
    /// alone. Null when there is nothing worth doing. This is an ordering aid, not the estimate:
    /// the dialog's number reads the file's real stream bitrates.
    /// </summary>
    public long? PotentialSavingBytes { get; set; }

    /// <summary>Gets or sets the change that saving would come from, in words.</summary>
    public string? SavingBasis { get; set; }
}
