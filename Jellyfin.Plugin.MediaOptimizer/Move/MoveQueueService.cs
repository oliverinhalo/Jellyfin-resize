using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Move;

/// <summary>Runs queued relocations in the background.</summary>
public interface IMoveQueueService
{
    /// <summary>Cancels a running or queued move.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>True when a job was found and cancelled.</returns>
    bool Cancel(Guid id);
}

/// <summary>
/// The background worker that drains the move queue.
/// <para>
/// The order of operations is what makes a move safe: the file is copied to a hidden staging name
/// on the destination drive, checked against the original, renamed into place, and only then is
/// the original deleted. Every failure before that last step leaves the library exactly as it was.
/// </para>
/// </summary>
public class MoveQueueService : BackgroundService, IMoveQueueService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>Big enough that a spinning disk streams, small enough to cancel promptly.</summary>
    private const int CopyBufferBytes = 4 * 1024 * 1024;

    private readonly IMoveJobStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryReconciler _reconciler;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MoveQueueService> _logger;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>Initializes a new instance of the <see cref="MoveQueueService"/> class.</summary>
    /// <param name="store">Move job store.</param>
    /// <param name="libraryManager">Library manager, for re-reading the item before it is touched.</param>
    /// <param name="reconciler">Library reconciler.</param>
    /// <param name="sessionManager">Session manager, so a file being watched is never moved.</param>
    /// <param name="logger">Logger.</param>
    public MoveQueueService(
        IMoveJobStore store,
        ILibraryManager libraryManager,
        LibraryReconciler reconciler,
        ISessionManager sessionManager,
        ILogger<MoveQueueService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _reconciler = reconciler;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public bool Cancel(Guid id)
    {
        if (_running.TryGetValue(id, out var cts))
        {
            cts.Cancel();
            return true;
        }

        var job = _store.Get(id);
        if (job is null || !job.IsActive)
        {
            return false;
        }

        job.Status = MoveStatus.Cancelled;
        job.FinishedAt = DateTime.UtcNow;
        _store.Update(job);
        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.ReconcileInterrupted();

        _logger.LogInformation("[MediaOptimizer] Move queue worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_running.Count >= Math.Max(1, Config.MaxConcurrentMoves))
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (Config.PauseWhilePlaybackActive && IsPlaybackActive())
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var job = _store.TakeNextQueued();
                if (job is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _running[job.Id] = cts;

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await RunJobAsync(job, cts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            _running.TryRemove(job.Id, out _);
                            cts.Dispose();
                        }
                    },
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
#pragma warning disable CA1031 // The worker loop must never die on one bad job.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "[MediaOptimizer] Unexpected error in the move queue loop");
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("[MediaOptimizer] Move queue worker stopped");
    }

    private async Task RunJobAsync(MoveJob job, CancellationToken cancellationToken)
    {
        var staging = string.Empty;

        // Everything before the file actually moves is undoable and reports itself as such. Once
        // it has moved, a failure means something quite different — the bytes are on the new
        // drive — and saying "the file was left where it was" would send someone looking in the
        // wrong place.
        var hasMoved = false;

        try
        {
            var error = Preflight(job);
            if (error is not null)
            {
                Fail(job, error);
                return;
            }

            var sourceDirectory = Path.GetDirectoryName(job.SourcePath) ?? job.SourcePath;
            var destinationDirectory = Path.GetDirectoryName(job.DestinationPath) ?? job.DestinationPath;
            Directory.CreateDirectory(destinationDirectory);

            _reconciler.ReportChangeBegin(sourceDirectory);
            _reconciler.ReportChangeBegin(destinationDirectory);

            try
            {
                job.Status = MoveStatus.Copying;
                _store.Update(job);

                if (TryInstantRename(job))
                {
                    job.WasInstantRename = true;
                    job.BytesCopied = job.SizeBytes ?? 0;
                    job.ProgressPercent = 100d;
                }
                else
                {
                    staging = MovePathPlanner.BuildStagingPath(job.DestinationPath, job.Id);
                    var sourceHash = await CopyAsync(job, staging, cancellationToken).ConfigureAwait(false);

                    job.Status = MoveStatus.Verifying;
                    _store.Update(job);

                    var verifyError = await VerifyAsync(job, staging, sourceHash, cancellationToken).ConfigureAwait(false);
                    if (verifyError is not null)
                    {
                        TryDelete(staging);
                        Fail(job, verifyError);
                        return;
                    }

                    CopyTimestamps(job.SourcePath, staging);

                    // Within one folder this rename is atomic, so the destination either does not
                    // exist or is the complete file. Never overwrite: a file that appeared here
                    // since the plan was made is not ours to replace.
                    File.Move(staging, job.DestinationPath, overwrite: false);
                    staging = string.Empty;

                    var deleteError = DeleteSource(job);
                    if (deleteError is not null)
                    {
                        Fail(job, deleteError);
                        return;
                    }
                }

                hasMoved = true;
                job.Status = MoveStatus.Finalizing;
                _store.Update(job);

                job.CompanionFilesMoved = _reconciler.MoveCompanionFiles(job.SourcePath, job.DestinationPath).Count;
            }
            finally
            {
                _reconciler.ReportChangeComplete(sourceDirectory, refreshPath: false);
                _reconciler.ReportChangeComplete(destinationDirectory, refreshPath: false);
            }

            // The file is on the new drive from here on, so the library has to be told about it
            // whatever happens. Cancellation is no longer an option either: stopping now would
            // leave the item pointing at a path that no longer exists.
            try
            {
                await _reconciler.RepointAsync(job.ItemId, job.DestinationPath, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The move itself succeeded; the message has to say so.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "[MediaOptimizer] Move {JobId} could not update the library entry", job.Id);
                Fail(
                    job,
                    "The file was moved to " + job.DestinationPath + ", but the library entry could not be "
                    + "updated (" + ex.Message + "). Run a library scan to pick it up again.");
                return;
            }

            if (Config.RemoveEmptyFoldersAfterMove)
            {
                RemoveEmptySourceFolder(sourceDirectory, job.SourceRoot);
            }

            job.Status = MoveStatus.Completed;
            job.ProgressPercent = 100d;
            job.FinishedAt = DateTime.UtcNow;
            _store.Update(job);

            _logger.LogInformation(
                "[MediaOptimizer] Moved {Source} to {Destination}{Mode}",
                job.SourcePath,
                job.DestinationPath,
                job.WasInstantRename ? " (rename)" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            job.Status = MoveStatus.Cancelled;
            job.FinishedAt = DateTime.UtcNow;
            job.Error = hasMoved
                ? "Cancelled after the file had already been moved to " + job.DestinationPath + "."
                : "Cancelled. The file was left where it was.";
            _store.Update(job);
            _logger.LogInformation("[MediaOptimizer] Move {JobId} cancelled", job.Id);
        }
#pragma warning disable CA1031 // A failed move must be recorded, not thrown out of the worker.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            TryDelete(staging);
            _logger.LogError(ex, "[MediaOptimizer] Move {JobId} failed", job.Id);
            Fail(
                job,
                hasMoved
                    ? ex.Message + " The file itself had already been moved to " + job.DestinationPath + "."
                    : ex.Message);
        }
    }

    /// <summary>
    /// Re-checks everything the plan assumed, because a queued move may have waited hours behind
    /// other jobs and the library can have changed underneath it.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <returns>An error message, or null when the move may proceed.</returns>
    private string? Preflight(MoveJob job)
    {
        var item = _libraryManager.GetItemById(job.ItemId);
        if (item is null)
        {
            return "The library item no longer exists.";
        }

        // A conversion that finished while this job waited may have changed the extension, so the
        // library is the authority on where the file is now, not the path captured at queue time.
        if (!string.IsNullOrEmpty(item.Path) && !string.Equals(item.Path, job.SourcePath, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "[MediaOptimizer] Move {JobId}: the source moved to {Path} since it was queued",
                job.Id,
                item.Path);

            job.SourcePath = item.Path;
            job.DestinationPath = MovePathPlanner.BuildDestinationPath(item.Path, job.SourceRoot, job.DestinationRoot);
        }

        if (!File.Exists(job.SourcePath))
        {
            return "The file is no longer where the library says it is.";
        }

        if (File.Exists(job.DestinationPath))
        {
            return FormattableString.Invariant(
                $"A file already exists at {job.DestinationPath}. Nothing was overwritten.");
        }

        if (IsBeingPlayed(job.ItemId))
        {
            return "Someone is watching this file right now. It was left alone; try again later.";
        }

        try
        {
            job.SizeBytes = new FileInfo(job.SourcePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The file could not be read: " + ex.Message;
        }

        var destinationDirectory = Path.GetDirectoryName(job.DestinationPath);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            return "The destination path is not a valid folder.";
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            var free = new DriveInfo(destinationDirectory).AvailableFreeSpace;
            var needed = (job.SizeBytes ?? 0) + (64L * 1024 * 1024);

            if (free < needed)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Not enough free space on the destination: {0:N1} GB available, {1:N1} GB needed.",
                    free / 1024d / 1024d / 1024d,
                    needed / 1024d / 1024d / 1024d);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DriveNotFoundException)
        {
            return "The destination folder could not be prepared: " + ex.Message;
        }

        return null;
    }

    /// <summary>
    /// Tries the instant path: a rename within one drive, which costs nothing however large the
    /// file is. Returns false when the destination is on another file system and every byte has
    /// to be copied instead.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <returns>True when the file was renamed into place.</returns>
    private bool TryInstantRename(MoveJob job)
    {
        if (!MovePathPlanner.CanTryInstantRename(job.SourcePath, job.DestinationPath, OperatingSystem.IsWindows()))
        {
            return false;
        }

        try
        {
            File.Move(job.SourcePath, job.DestinationPath, overwrite: false);
            return true;
        }
        catch (IOException ex)
        {
            // Either a different file system, or the destination appeared underneath us. The copy
            // path re-checks the destination, so it is safe to fall through to it either way.
            _logger.LogDebug(ex, "[MediaOptimizer] Rename was not possible for {Path}; copying instead", job.SourcePath);
            return false;
        }
    }

    /// <summary>
    /// Copies the file to a staging name on the destination drive, reporting progress as it goes
    /// and hashing the source on the way past when verification is switched on — reading it twice
    /// would double the cost of every move.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="staging">The staging path to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The source hash, or null when hashing is off.</returns>
    private async Task<byte[]?> CopyAsync(MoveJob job, string staging, CancellationToken cancellationToken)
    {
        var hash = Config.VerifyMovesWithHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var total = job.SizeBytes ?? 0;
        var clock = Stopwatch.StartNew();
        var buffer = new byte[CopyBufferBytes];

        try
        {
            using var source = new FileStream(
                job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var destination = new FileStream(
                staging, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous);

            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, read);

                copied += read;
                job.BytesCopied = copied;
                job.ProgressPercent = total > 0 ? Math.Min(100d, copied * 100d / total) : 0d;

                var seconds = clock.Elapsed.TotalSeconds;
                if (seconds > 0.5d)
                {
                    job.BytesPerSecond = copied / seconds;
                    job.EtaSeconds = job.BytesPerSecond > 0 ? (total - copied) / job.BytesPerSecond : null;
                }

                _store.ReportProgress(job);
            }

            // Without this the copy exists only in the page cache, and a power cut between here
            // and deleting the original would take the file with it.
            destination.Flush(flushToDisk: true);

            return hash?.GetCurrentHash();
        }
        finally
        {
            hash?.Dispose();
        }
    }

    /// <summary>Checks the copy against the original before the original is deleted.</summary>
    /// <param name="job">The job.</param>
    /// <param name="staging">The file that was written.</param>
    /// <param name="sourceHash">The hash computed while reading the source, when hashing is on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An error message, or null when the copy is good.</returns>
    private async Task<string?> VerifyAsync(
        MoveJob job,
        string staging,
        byte[]? sourceHash,
        CancellationToken cancellationToken)
    {
        var expected = job.SizeBytes ?? 0;
        var actual = new FileInfo(staging).Length;

        if (actual != expected)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "The copy is {0:N0} bytes but the original is {1:N0}. Nothing was deleted.",
                actual,
                expected);
        }

        if (sourceHash is null)
        {
            job.HashVerified = null;
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];

        using (var stream = new FileStream(
            staging, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        if (!hash.GetCurrentHash().AsSpan().SequenceEqual(sourceHash))
        {
            job.HashVerified = false;
            return "The copy does not match the original byte for byte, so the original was kept and the copy discarded.";
        }

        job.HashVerified = true;
        return null;
    }

    /// <summary>
    /// Removes the original now that a verified copy is in place. If it cannot be removed — a
    /// lock, a read-only mount — the copy is taken away again rather than left behind: two
    /// identical files in two library folders would be scanned as two copies of the same film,
    /// and the library would still be pointing at the one that could not be deleted.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <returns>An error message, or null when the original is gone.</returns>
    private string? DeleteSource(MoveJob job)
    {
        try
        {
            File.Delete(job.SourcePath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Could not delete {Path} after copying it; undoing the move", job.SourcePath);

            try
            {
                File.Delete(job.DestinationPath);
                return "The copy was made, but the original could not be deleted (" + ex.Message
                    + "). The copy was removed again, so nothing changed.";
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                return "The copy was made, but the original could not be deleted (" + ex.Message
                    + "), and the copy at " + job.DestinationPath + " could not be removed either ("
                    + cleanup.Message + "). Both files now exist; delete one of them by hand.";
            }
        }
    }

    /// <summary>Carries the original's timestamps across, so "recently added" keeps meaning something.</summary>
    /// <param name="source">The original file.</param>
    /// <param name="destination">The copy.</param>
    private void CopyTimestamps(string source, string destination)
    {
        try
        {
            var info = new FileInfo(source);
            File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
            File.SetCreationTimeUtc(destination, info.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not copy timestamps to {Path}", destination);
        }
    }

    /// <summary>
    /// Deletes the folder the file came out of when it is now empty — a film in its own folder
    /// otherwise leaves an empty one behind on the old drive after every move.
    /// </summary>
    /// <param name="directory">The folder the file was in.</param>
    /// <param name="sourceRoot">The library folder, which is never removed.</param>
    private void RemoveEmptySourceFolder(string directory, string? sourceRoot)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var normalized = MovePathPlanner.NormalizeDirectory(directory);
            if (string.IsNullOrEmpty(sourceRoot)
                || string.Equals(normalized, MovePathPlanner.NormalizeDirectory(sourceRoot), StringComparison.OrdinalIgnoreCase)
                || !MovePathPlanner.IsUnder(normalized, sourceRoot))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }

            Directory.Delete(directory);
            _logger.LogInformation("[MediaOptimizer] Removed the empty folder left behind at {Path}", directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not remove the empty folder at {Path}", directory);
        }
    }

    private bool IsPlaybackActive()
    {
        try
        {
            return _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read the active sessions");
            return false;
        }
    }

    private bool IsBeingPlayed(Guid itemId)
    {
        try
        {
            return _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == itemId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read the active sessions");
            return false;
        }
    }

    private void Fail(MoveJob job, string message)
    {
        job.Status = MoveStatus.Failed;
        job.Error = message;
        job.FinishedAt = DateTime.UtcNow;
        _store.Update(job);
    }

    private void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not delete the partial copy at {Path}", path);
        }
    }
}
