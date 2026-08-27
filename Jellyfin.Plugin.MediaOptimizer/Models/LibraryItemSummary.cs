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

    /// <summary>Gets or sets a value indicating whether a conversion is already queued or running.</summary>
    public bool HasActiveJob { get; set; }
}
