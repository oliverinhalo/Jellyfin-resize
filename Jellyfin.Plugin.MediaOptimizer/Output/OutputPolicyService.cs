using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Output;

/// <summary>Moves a verified encode into its final home.</summary>
public interface IOutputPolicyService
{
    /// <summary>Gets the directory used for in-progress encodes.</summary>
    /// <returns>An existing directory path.</returns>
    string GetTempDirectory();

    /// <summary>Applies the requested policy to a verified output file.</summary>
    /// <param name="job">The job being completed.</param>
    /// <param name="tempOutputPath">The verified temporary file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task ApplyAsync(EncodeJob job, string tempOutputPath, CancellationToken cancellationToken);

    /// <summary>Restores a quarantined original, undoing a Replace.</summary>
    /// <param name="job">The completed job to undo.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RevertAsync(EncodeJob job, CancellationToken cancellationToken);

    /// <summary>Gets the directory quarantined originals are moved to.</summary>
    /// <returns>An existing directory path.</returns>
    string GetQuarantineDirectory();
}

/// <inheritdoc />
public class OutputPolicyService : IOutputPolicyService
{
    private readonly IApplicationPaths _appPaths;
    private readonly LibraryReconciler _reconciler;
    private readonly ILogger<OutputPolicyService> _logger;

    /// <summary>Initializes a new instance of the <see cref="OutputPolicyService"/> class.</summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="reconciler">Library reconciler.</param>
    /// <param name="logger">Logger.</param>
    public OutputPolicyService(
        IApplicationPaths appPaths,
        LibraryReconciler reconciler,
        ILogger<OutputPolicyService> logger)
    {
        _appPaths = appPaths;
        _reconciler = reconciler;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public string GetTempDirectory()
    {
        var configured = Config.TempDirectory;
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_appPaths.DataPath, "mediaoptimizer", "work")
            : configured;

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <inheritdoc />
    public string GetQuarantineDirectory()
    {
        var configured = Config.QuarantineDirectory;
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_appPaths.DataPath, "mediaoptimizer", "quarantine")
            : configured;

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(EncodeJob job, string tempOutputPath, CancellationToken cancellationToken)
    {
        switch (job.OutputPolicy)
        {
            case OutputPolicy.Sidecar:
                ApplySidecar(job, tempOutputPath);
                break;

            case OutputPolicy.AlternateVersion:
                ApplyAlternateVersion(job, tempOutputPath);
                break;

            case OutputPolicy.Replace:
                await ApplyReplaceAsync(job, tempOutputPath, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException("Unknown output policy.");
        }
    }

    /// <inheritdoc />
    public async Task RevertAsync(EncodeJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(job.QuarantinePath) || !File.Exists(job.QuarantinePath))
        {
            throw new InvalidOperationException("The original file is no longer in quarantine and cannot be restored.");
        }

        var originalPath = job.SourcePath;
        var currentPath = job.OutputPath;

        _reconciler.ReportChangeBegin(Path.GetDirectoryName(originalPath) ?? originalPath);
        try
        {
            MoveAcrossVolumes(job.QuarantinePath, originalPath, overwrite: true);

            if (!string.IsNullOrEmpty(currentPath)
                && File.Exists(currentPath)
                && !string.Equals(currentPath, originalPath, StringComparison.Ordinal))
            {
                File.Delete(currentPath);
            }

            await _reconciler.RepointAsync(job.ItemId, originalPath, cancellationToken).ConfigureAwait(false);

            job.QuarantinePath = null;
            job.QuarantineExpiresAt = null;
            job.OutputPath = originalPath;

            _logger.LogInformation("[MediaOptimizer] Reverted job {JobId}; original restored to {Path}", job.Id, originalPath);
        }
        finally
        {
            _reconciler.ReportChangeComplete(Path.GetDirectoryName(originalPath) ?? originalPath, refreshPath: true);
        }
    }

    /// <summary>
    /// Builds a non-colliding destination path for a sidecar output.
    /// </summary>
    /// <param name="sourcePath">The original file path.</param>
    /// <param name="directory">Target directory, or empty to sit beside the original.</param>
    /// <param name="extension">Output extension without a dot.</param>
    /// <param name="exists">Predicate used to test for collisions.</param>
    /// <returns>A path that does not currently exist.</returns>
    internal static string BuildSidecarPath(
        string sourcePath,
        string directory,
        string extension,
        Func<string, bool> exists)
    {
        var dir = string.IsNullOrWhiteSpace(directory)
            ? Path.GetDirectoryName(sourcePath) ?? "."
            : directory;

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(dir, FormattableString.Invariant($"{baseName} - Optimized.{extension}"));

        var counter = 2;
        while (exists(candidate))
        {
            candidate = Path.Combine(dir, FormattableString.Invariant($"{baseName} - Optimized ({counter}).{extension}"));
            counter++;
        }

        return candidate;
    }

    /// <summary>
    /// Moves a file, falling back to copy-then-delete when the destination is on another volume.
    /// A same-directory rename is atomic; a cross-volume copy is not, so the copy lands on a
    /// hidden temporary name first and is renamed into place once it is fully written.
    /// </summary>
    /// <param name="source">Source path.</param>
    /// <param name="destination">Destination path.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    internal static void MoveAcrossVolumes(string source, string destination, bool overwrite)
    {
        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        try
        {
            File.Move(source, destination, overwrite);
            return;
        }
        catch (IOException)
        {
            // Different filesystem: fall through to copy.
        }

        var staging = destination + ".mopt-partial";
        try
        {
            File.Copy(source, staging, overwrite: true);

            using (var stream = new FileStream(staging, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            File.Move(staging, destination, overwrite);
            File.Delete(source);
        }
        catch
        {
            if (File.Exists(staging))
            {
                try
                {
                    File.Delete(staging);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nothing useful to do; the partial file is named so it is obvious.
                }
            }

            throw;
        }
    }

    private void ApplySidecar(EncodeJob job, string tempOutputPath)
    {
        var extension = Path.GetExtension(tempOutputPath).TrimStart('.');
        var destination = BuildSidecarPath(
            job.SourcePath,
            Config.SidecarDirectory,
            extension,
            File.Exists);

        MoveAcrossVolumes(tempOutputPath, destination, overwrite: false);

        job.OutputPath = destination;
        job.OutputSizeBytes = new FileInfo(destination).Length;

        _logger.LogInformation("[MediaOptimizer] Sidecar output written to {Path}", destination);
    }

    private void ApplyAlternateVersion(EncodeJob job, string tempOutputPath)
    {
        // Jellyfin groups alternate versions by placing them in the same folder with a
        // " - <label>" suffix, which the library scanner recognises without any database
        // surgery. The scan that follows the refresh links them to the same item.
        var extension = Path.GetExtension(tempOutputPath).TrimStart('.');
        var dir = Path.GetDirectoryName(job.SourcePath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);

        var label = DescribeVersion(job.Request);
        var destination = Path.Combine(dir, FormattableString.Invariant($"{baseName} - {label}.{extension}"));

        var counter = 2;
        while (File.Exists(destination))
        {
            destination = Path.Combine(dir, FormattableString.Invariant($"{baseName} - {label} ({counter}).{extension}"));
            counter++;
        }

        var watchDir = Path.GetDirectoryName(destination) ?? dir;
        _reconciler.ReportChangeBegin(watchDir);
        try
        {
            MoveAcrossVolumes(tempOutputPath, destination, overwrite: false);
        }
        finally
        {
            _reconciler.ReportChangeComplete(watchDir, refreshPath: true);
        }

        job.OutputPath = destination;
        job.OutputSizeBytes = new FileInfo(destination).Length;

        _logger.LogInformation("[MediaOptimizer] Alternate version written to {Path}", destination);
    }

    private async Task ApplyReplaceAsync(EncodeJob job, string tempOutputPath, CancellationToken cancellationToken)
    {
        var sourcePath = job.SourcePath;
        var extension = Path.GetExtension(tempOutputPath);
        var sourceExtension = Path.GetExtension(sourcePath);
        var sameExtension = string.Equals(extension, sourceExtension, StringComparison.OrdinalIgnoreCase);

        var finalPath = sameExtension
            ? sourcePath
            : Path.Combine(
                Path.GetDirectoryName(sourcePath) ?? ".",
                Path.GetFileNameWithoutExtension(sourcePath) + extension);

        var quarantineDir = GetQuarantineDirectory();
        var quarantinePath = Path.Combine(
            quarantineDir,
            FormattableString.Invariant($"{job.Id:N}-{Path.GetFileName(sourcePath)}"));

        var watchDir = Path.GetDirectoryName(sourcePath) ?? sourcePath;
        _reconciler.ReportChangeBegin(watchDir);

        try
        {
            // The original is moved aside rather than deleted, so a revert is always possible
            // for as long as the retention window lasts.
            MoveAcrossVolumes(sourcePath, quarantinePath, overwrite: false);
            job.QuarantinePath = quarantinePath;
            job.QuarantineExpiresAt = DateTime.UtcNow.AddDays(Math.Max(0, Config.QuarantineRetentionDays));

            try
            {
                MoveAcrossVolumes(tempOutputPath, finalPath, overwrite: true);
            }
            catch
            {
                // Put the original back before surfacing the failure; never leave a gap.
                _logger.LogError("[MediaOptimizer] Failed to move the new file into place; restoring the original");
                MoveAcrossVolumes(quarantinePath, sourcePath, overwrite: true);
                job.QuarantinePath = null;
                job.QuarantineExpiresAt = null;
                throw;
            }

            job.OutputPath = finalPath;
            job.OutputSizeBytes = new FileInfo(finalPath).Length;

            if (!sameExtension)
            {
                var moved = _reconciler.MoveCompanionFiles(sourcePath, finalPath);
                if (moved.Count > 0)
                {
                    _logger.LogInformation("[MediaOptimizer] Moved {Count} companion file(s) alongside the new media file", moved.Count);
                }
            }
        }
        finally
        {
            _reconciler.ReportChangeComplete(watchDir, refreshPath: false);
        }

        // Only after the file is safely in place does the database get repointed.
        await _reconciler.RepointAsync(job.ItemId, finalPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[MediaOptimizer] Replaced {Source} with {Final}; original quarantined at {Quarantine}",
            sourcePath,
            finalPath,
            quarantinePath);
    }

    private static string DescribeVersion(EncodeRequest request)
    {
        if (request.TargetHeight is > 0)
        {
            return request.TargetHeight.Value.ToString(CultureInfo.InvariantCulture) + "p";
        }

        if (!string.IsNullOrEmpty(request.VideoCodec))
        {
            return request.VideoCodec.Replace("lib", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        }

        return "Optimized";
    }
}
