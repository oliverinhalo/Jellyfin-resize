using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Move;

/// <summary>Finds the drives media can be moved between, and works out what a move would do.</summary>
public interface IMediaMoveService
{
    /// <summary>
    /// Lists every folder media can be moved out of or into: each library folder, plus any extra
    /// destinations configured in the plugin settings.
    /// </summary>
    /// <param name="includeUsage">Whether to count the files currently living in each folder.</param>
    /// <returns>The locations, in library order.</returns>
    IReadOnlyList<MoveLocation> GetLocations(bool includeUsage);

    /// <summary>Describes where one file is now, for the in-app move dialog.</summary>
    /// <param name="itemId">The library item.</param>
    /// <returns>The file's current home, or null when the item is unknown or has no file.</returns>
    MoveItemInfo? Describe(Guid itemId);

    /// <summary>Works out what moving these files would do, without moving anything.</summary>
    /// <param name="request">The files and the destination.</param>
    /// <returns>The plan.</returns>
    MovePlan Plan(MoveRequest request);

    /// <summary>Queues the movable files from a plan.</summary>
    /// <param name="request">The files and the destination.</param>
    /// <returns>What was queued and what was not.</returns>
    MoveQueueResult Queue(MoveRequest request);
}

/// <inheritdoc />
public class MediaMoveService : IMediaMoveService
{
    /// <summary>Leave this much room on the destination beyond the files themselves.</summary>
    private const long FreeSpaceMargin = 256L * 1024 * 1024;

    private readonly ILibraryManager _libraryManager;
    private readonly IJobStore _encodeJobs;
    private readonly IMoveJobStore _moveJobs;
    private readonly ILogger<MediaMoveService> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaMoveService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="encodeJobs">The conversion queue, so a file is never moved mid-encode.</param>
    /// <param name="moveJobs">The move queue.</param>
    /// <param name="logger">Logger.</param>
    public MediaMoveService(
        ILibraryManager libraryManager,
        IJobStore encodeJobs,
        IMoveJobStore moveJobs,
        ILogger<MediaMoveService> logger)
    {
        _libraryManager = libraryManager;
        _encodeJobs = encodeJobs;
        _moveJobs = moveJobs;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <inheritdoc />
    public IReadOnlyList<MoveLocation> GetLocations(bool includeUsage)
    {
        var locations = new List<MoveLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in GetVirtualFolders())
        {
            foreach (var path in folder.Locations ?? [])
            {
                var normalized = MovePathPlanner.NormalizeDirectory(path);
                if (normalized.Length == 0 || !seen.Add(normalized))
                {
                    continue;
                }

                locations.Add(Describe(normalized, folder.Name, folder.CollectionType?.ToString(), isExtra: false));
            }
        }

        foreach (var path in ParseExtraTargets(Config.AdditionalMoveTargets))
        {
            var normalized = MovePathPlanner.NormalizeDirectory(path);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            locations.Add(Describe(normalized, null, null, isExtra: true));
        }

        if (includeUsage && locations.Count > 0)
        {
            CountUsage(locations);
        }

        return locations;
    }

    /// <inheritdoc />
    public MoveItemInfo? Describe(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        return new MoveItemInfo
        {
            ItemId = item.Id,
            Name = item.Name ?? itemId.ToString("N"),
            Path = item.Path,
            CurrentRoot = MovePathPlanner.ResolveRoot(item.Path, GetLibraryRoots()),
            SizeBytes = TryGetSize(item.Path),
            HasActiveMove = _moveJobs.HasActiveJobForItem(itemId)
        };
    }

    /// <inheritdoc />
    public MovePlan Plan(MoveRequest request)
    {
        var destination = MovePathPlanner.NormalizeDirectory(request.DestinationPath);
        var blockers = new List<string>();
        var notes = new List<string>();
        var items = new List<MovePlanItem>();

        if (destination.Length == 0)
        {
            blockers.Add("Choose a folder to move the files into.");
            return new MovePlan { DestinationPath = destination, Blockers = blockers };
        }

        if (!Directory.Exists(destination))
        {
            blockers.Add(FormattableString.Invariant($"{destination} does not exist, or the server cannot see it."));
        }
        else if (!OutputPolicyService.IsWritable(destination))
        {
            blockers.Add(FormattableString.Invariant($"The server cannot write to {destination}. Check the folder's permissions."));
        }

        var roots = GetLibraryRoots();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var sameDriveCount = 0;

        foreach (var item in ResolveItems(request))
        {
            var plan = PlanOne(item, destination, roots, claimed);
            items.Add(plan);

            if (plan.CanMove)
            {
                totalBytes += plan.SizeBytes ?? 0;
                if (plan.IsSameDrive)
                {
                    sameDriveCount++;
                }
            }
        }

        var movable = items.Count(i => i.CanMove);
        var free = TryGetFreeBytes(destination);

        if (movable == 0 && blockers.Count == 0)
        {
            blockers.Add(items.Count == 0
                ? "Nothing was selected to move."
                : "None of the selected files can be moved. The reasons are listed against each one.");
        }

        if (free is not null && movable > 0 && free.Value < totalBytes + FreeSpaceMargin)
        {
            blockers.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Not enough free space: {0:N1} GB available, {1:N1} GB needed.",
                free.Value / 1024d / 1024d / 1024d,
                (totalBytes + FreeSpaceMargin) / 1024d / 1024d / 1024d));
        }

        if (sameDriveCount > 0)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} of these are already on the destination drive, so they are renamed instantly rather than copied.",
                sameDriveCount));
        }

        if (movable > sameDriveCount)
        {
            notes.Add("Each file is copied to the new drive and checked before the original is deleted, "
                + "so nothing is lost if a copy fails part-way.");
        }

        return new MovePlan
        {
            DestinationPath = destination,
            Items = items,
            MovableCount = movable,
            SkippedCount = items.Count - movable,
            TotalBytes = totalBytes,
            DestinationFreeBytes = free,
            FreeBytesAfter = free is null ? null : free.Value - totalBytes,
            Blockers = blockers,
            Notes = notes
        };
    }

    /// <inheritdoc />
    public MoveQueueResult Queue(MoveRequest request)
    {
        var plan = Plan(request);
        if (!plan.IsRunnable)
        {
            return new MoveQueueResult
            {
                SkippedCount = plan.Items.Count,
                Skipped = plan.Items
            };
        }

        var roots = GetLibraryRoots();
        var jobs = new List<MoveJob>();

        foreach (var entry in plan.Items.Where(i => i.CanMove))
        {
            var job = new MoveJob
            {
                ItemId = entry.ItemId,
                ItemName = entry.Name,
                SourcePath = entry.SourcePath,
                SourceRoot = MovePathPlanner.ResolveRoot(entry.SourcePath, roots),
                DestinationRoot = plan.DestinationPath,
                DestinationPath = entry.DestinationPath!,
                SizeBytes = entry.SizeBytes
            };

            _moveJobs.Add(job);
            jobs.Add(job);
        }

        _logger.LogInformation(
            "[MediaOptimizer] Queued {Count} move(s) to {Destination} ({Bytes} bytes)",
            jobs.Count,
            plan.DestinationPath,
            plan.TotalBytes);

        return new MoveQueueResult
        {
            QueuedCount = jobs.Count,
            SkippedCount = plan.SkippedCount,
            TotalBytes = plan.TotalBytes,
            Jobs = jobs,
            Skipped = plan.Items.Where(i => !i.CanMove).ToList()
        };
    }

    /// <summary>Splits the configured extra destinations, which are one per line or separated by semicolons.</summary>
    /// <param name="configured">The raw setting.</param>
    /// <returns>The individual folder paths.</returns>
    internal static IReadOnlyList<string> ParseExtraTargets(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Array.Empty<string>();
        }

        return configured
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private MovePlanItem PlanOne(
        BaseItem item,
        string destination,
        IReadOnlyList<string> roots,
        HashSet<string> claimed)
    {
        var entry = new MovePlanItem
        {
            ItemId = item.Id,
            Name = item.Name ?? item.Id.ToString("N"),
            SourcePath = item.Path ?? string.Empty
        };

        if (string.IsNullOrEmpty(entry.SourcePath))
        {
            entry.SkippedReason = "This item has no file on disk.";
            return entry;
        }

        if (!File.Exists(entry.SourcePath))
        {
            entry.SkippedReason = "The file is no longer where the library says it is.";
            return entry;
        }

        if (_encodeJobs.HasActiveJobForItem(item.Id))
        {
            entry.SkippedReason = "A conversion is queued or running for this file.";
            return entry;
        }

        if (_moveJobs.HasActiveJobForItem(item.Id))
        {
            entry.SkippedReason = "This file is already queued to move.";
            return entry;
        }

        var sourceRoot = MovePathPlanner.ResolveRoot(entry.SourcePath, roots);
        var destinationPath = MovePathPlanner.BuildDestinationPath(entry.SourcePath, sourceRoot, destination);

        if (string.Equals(destinationPath, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            entry.SkippedReason = "It is already there.";
            return entry;
        }

        if (File.Exists(destinationPath))
        {
            entry.SkippedReason = FormattableString.Invariant(
                $"A file already exists at {destinationPath}. Nothing is overwritten, so this one is left alone.");
            return entry;
        }

        if (!claimed.Add(destinationPath) || _moveJobs.IsDestinationClaimed(destinationPath))
        {
            entry.SkippedReason = "Another file in this move is already heading to that exact path.";
            return entry;
        }

        entry.DestinationPath = destinationPath;
        entry.SizeBytes = TryGetSize(entry.SourcePath);
        entry.IsSameDrive = string.Equals(
            MovePathPlanner.TryGetRoot(entry.SourcePath),
            MovePathPlanner.TryGetRoot(destinationPath),
            StringComparison.OrdinalIgnoreCase);
        entry.CanMove = true;
        return entry;
    }

    /// <summary>
    /// Resolves the items a request names, either explicitly or by naming a folder to empty.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The library items.</returns>
    private IReadOnlyList<BaseItem> ResolveItems(MoveRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            var source = MovePathPlanner.NormalizeDirectory(request.SourcePath);
            return AllMediaItems()
                .Where(i => MovePathPlanner.IsUnder(i.Path, source))
                .ToList();
        }

        var items = new List<BaseItem>();
        foreach (var id in request.ItemIds.Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private MoveLocation Describe(string path, string? libraryName, string? collectionType, bool isExtra)
    {
        var location = new MoveLocation
        {
            Path = path,
            LibraryName = libraryName,
            CollectionType = collectionType,
            IsExtraTarget = isExtra,
            Exists = Directory.Exists(path)
        };

        if (location.Exists)
        {
            location.IsWritable = OutputPolicyService.IsWritable(path);

            try
            {
                var drive = new DriveInfo(path);
                location.TotalBytes = drive.TotalSize;
                location.FreeBytes = drive.AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DriveNotFoundException)
            {
                _logger.LogDebug(ex, "[MediaOptimizer] Could not read drive information for {Path}", path);
            }
        }

        return location;
    }

    /// <summary>Counts the library files sitting under each location, so the list shows what is where.</summary>
    /// <param name="locations">The locations to annotate.</param>
    private void CountUsage(IReadOnlyList<MoveLocation> locations)
    {
        // Longest first, so a file in a nested library folder is counted against that folder
        // rather than the parent it happens to sit inside.
        var ordered = locations.OrderByDescending(l => l.Path.Length).ToList();

        foreach (var item in AllMediaItems())
        {
            var match = ordered.FirstOrDefault(l => MovePathPlanner.IsUnder(item.Path, l.Path));
            if (match is null)
            {
                continue;
            }

            match.ItemCount++;
            match.ItemBytes += TryGetSize(item.Path!) ?? 0;
        }
    }

    private IEnumerable<BaseItem> AllMediaItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false
        };

        return _libraryManager.GetItemList(query).Where(i => !string.IsNullOrEmpty(i.Path));
    }

    private IReadOnlyList<string> GetLibraryRoots()
    {
        return GetVirtualFolders()
            .SelectMany(f => f.Locations ?? [])
            .Select(MovePathPlanner.NormalizeDirectory)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<MediaBrowser.Model.Entities.VirtualFolderInfo> GetVirtualFolders()
    {
        try
        {
            return _libraryManager.GetVirtualFolders();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not read the library folders");
            return Array.Empty<MediaBrowser.Model.Entities.VirtualFolderInfo>();
        }
    }

    private static long? TryGetFreeBytes(string path)
    {
        try
        {
            return new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DriveNotFoundException)
        {
            return null;
        }
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
}
