using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>A folder media can be moved out of or into — usually one drive's library folder.</summary>
public class MoveLocation
{
    /// <summary>Gets or sets the folder path, exactly as the library holds it.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the library this folder belongs to, when it belongs to one.</summary>
    public string? LibraryName { get; set; }

    /// <summary>Gets or sets the library's collection type, for example "movies" or "tvshows".</summary>
    public string? CollectionType { get; set; }

    /// <summary>Gets or sets a value indicating whether the folder exists on disk right now.</summary>
    public bool Exists { get; set; }

    /// <summary>Gets or sets a value indicating whether the server can write into the folder.</summary>
    public bool IsWritable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this folder came from the plugin's list of extra
    /// destinations rather than from a Jellyfin library.
    /// </summary>
    public bool IsExtraTarget { get; set; }

    /// <summary>Gets or sets the size of the drive holding the folder, in bytes.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>Gets or sets the free space on the drive holding the folder, in bytes.</summary>
    public long? FreeBytes { get; set; }

    /// <summary>Gets or sets how many library files currently live under this folder.</summary>
    public int ItemCount { get; set; }

    /// <summary>Gets or sets how many bytes those files take up.</summary>
    public long ItemBytes { get; set; }
}

/// <summary>Where one library file is right now, so a move can be offered for it.</summary>
public class MoveItemInfo
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's current path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the library folder the file currently sits under, when it is in one.</summary>
    public string? CurrentRoot { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the file already has a move queued or running.</summary>
    public bool HasActiveMove { get; set; }
}

/// <summary>A request to move library files onto another drive.</summary>
public class MoveRequest
{
    /// <summary>Gets or sets the items to move. Ignored when <see cref="SourcePath"/> is set.</summary>
    public IReadOnlyList<Guid> ItemIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a folder whose entire contents should be moved. Used instead of
    /// <see cref="ItemIds"/> to move a whole drive's worth of media in one go.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>Gets or sets the folder the files are moved into.</summary>
    public string DestinationPath { get; set; } = string.Empty;
}

/// <summary>What would happen to one file if the move went ahead.</summary>
public class MovePlanItem
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets where the file is now.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets where it would end up, when it can be moved.</summary>
    public string? DestinationPath { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether this file would actually be moved.</summary>
    public bool CanMove { get; set; }

    /// <summary>Gets or sets why the file would be skipped, when it would be.</summary>
    public string? SkippedReason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the move stays on one drive, making it an instant
    /// rename rather than a copy of every byte.
    /// </summary>
    public bool IsSameDrive { get; set; }
}

/// <summary>The outcome of asking what a move would do, before anything is queued.</summary>
public class MovePlan
{
    /// <summary>Gets or sets the destination folder.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the per-file plan.</summary>
    public IReadOnlyList<MovePlanItem> Items { get; set; } = Array.Empty<MovePlanItem>();

    /// <summary>Gets or sets how many files would move.</summary>
    public int MovableCount { get; set; }

    /// <summary>Gets or sets how many files would be skipped.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Gets or sets the total number of bytes that would be written to the destination.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the free space on the destination drive.</summary>
    public long? DestinationFreeBytes { get; set; }

    /// <summary>Gets or sets what would be left free on the destination drive afterwards.</summary>
    public long? FreeBytesAfter { get; set; }

    /// <summary>Gets or sets messages that stop the move from being queued at all.</summary>
    public IReadOnlyList<string> Blockers { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets messages worth reading before confirming.</summary>
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    /// <summary>Gets a value indicating whether the move can be queued.</summary>
    public bool IsRunnable => Blockers.Count == 0 && MovableCount > 0;
}

/// <summary>The result of queueing a move.</summary>
public class MoveQueueResult
{
    /// <summary>Gets or sets how many jobs were queued.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Gets or sets how many files were skipped.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Gets or sets the total number of bytes queued to move.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the jobs that were created.</summary>
    public IReadOnlyList<MoveJob> Jobs { get; set; } = Array.Empty<MoveJob>();

    /// <summary>Gets or sets the files that were not queued, with the reason.</summary>
    public IReadOnlyList<MovePlanItem> Skipped { get; set; } = Array.Empty<MovePlanItem>();
}
