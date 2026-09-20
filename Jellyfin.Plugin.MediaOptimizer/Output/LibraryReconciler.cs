using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Output;

/// <summary>Keeps the Jellyfin database consistent after a file on disk has been swapped.</summary>
public interface ILibraryReconciler
{
    /// <summary>
    /// Repoints an existing item at a new path and refreshes it.
    /// </summary>
    /// <param name="itemId">The item to update.</param>
    /// <param name="newPath">The new file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RepointAsync(Guid itemId, string newPath, CancellationToken cancellationToken);

}

/// <inheritdoc />
public class LibraryReconciler : ILibraryReconciler
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ITrickplayManager _trickplayManager;
    private readonly ILogger<LibraryReconciler> _logger;

    /// <summary>Initializes a new instance of the <see cref="LibraryReconciler"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="libraryMonitor">Library monitor, so the watcher does not race the swap.</param>
    /// <param name="providerManager">Provider manager, for the post-swap refresh.</param>
    /// <param name="fileSystem">Jellyfin file system abstraction.</param>
    /// <param name="trickplayManager">Trickplay manager, for discarding stale scrub previews.</param>
    /// <param name="logger">Logger.</param>
    public LibraryReconciler(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ITrickplayManager trickplayManager,
        ILogger<LibraryReconciler> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _trickplayManager = trickplayManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RepointAsync(Guid itemId, string newPath, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogError("[MediaOptimizer] Cannot repoint {ItemId}: the item no longer exists", itemId);
            return;
        }

        var oldPath = item.Path;

        // Updating the existing item is the whole point: watched state, resume position,
        // favourites, playlists and collections are keyed on the item id. Letting a rescan
        // create a replacement item would silently discard all of it for every user.
        item.Path = newPath;

        await _libraryManager.UpdateItemAsync(
            item,
            item.GetParent(),
            ItemUpdateType.MetadataImport,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[MediaOptimizer] Repointed item {ItemId} from {OldPath} to {NewPath}",
            itemId,
            oldPath,
            newPath);

        // Scrub previews were generated from the old file. After a resolution change they are
        // both wrong and the wrong size, so discard them and let Jellyfin rebuild on demand.
        if (Plugin.Instance?.Configuration.RegenerateTrickplayAfterReplace ?? true)
        {
            try
            {
                await _trickplayManager.DeleteTrickplayDataAsync(item.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
            {
                _logger.LogDebug(ex, "[MediaOptimizer] Could not clear trickplay data for {ItemId}", itemId);
            }
        }

        // Re-probe so streams, bitrate and runtime reflect the new file straight away
        // instead of after the next scheduled scan.
        try
        {
            _providerManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    EnableRemoteContentProbe = true
                },
                RefreshPriority.High);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not queue a refresh for {ItemId}", itemId);
        }
    }

    /// <summary>Suspends the library watcher around a file swap.</summary>
    /// <param name="path">Directory or file being changed.</param>
    public void ReportChangeBegin(string path)
    {
        try
        {
            _libraryMonitor.ReportFileSystemChangeBeginning(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] ReportFileSystemChangeBeginning failed for {Path}", path);
        }
    }

    /// <summary>Resumes the library watcher after a file swap.</summary>
    /// <param name="path">Directory or file that was changed.</param>
    /// <param name="refreshPath">Whether to ask Jellyfin to look at the path again.</param>
    public void ReportChangeComplete(string path, bool refreshPath)
    {
        try
        {
            _libraryMonitor.ReportFileSystemChangeComplete(path, refreshPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] ReportFileSystemChangeComplete failed for {Path}", path);
        }
    }
}
