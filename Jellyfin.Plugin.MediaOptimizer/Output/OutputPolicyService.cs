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

    /// <summary>
    /// Gets the best directory to encode into for a given source. Writing next to the source
    /// makes the final move a rename rather than a whole-file copy.
    /// </summary>
    /// <param name="sourcePath">The file being converted.</param>
    /// <returns>An existing, writable directory.</returns>
    string GetWorkDirectoryFor(string sourcePath);

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

    /// <summary>Extension given to a kept original. Not a media extension, so Jellyfin ignores it.</summary>
    internal const string OriginalSuffix = ".mooriginal";

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
    public string GetWorkDirectoryFor(string sourcePath)
    {
        if (Config.EncodeBesideMedia && !string.IsNullOrEmpty(sourcePath))
        {
            var directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(directory) && IsWritable(directory))
            {
                return directory;
            }
        }

        return GetTempDirectory();
    }

    /// <summary>Checks that a directory can be written to before choosing it.</summary>
    /// <param name="directory">Directory to test.</param>
    /// <returns>Whether a file can be created there.</returns>
    internal static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".mediaoptimizer-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
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
            case OutputPolicy.ReplaceAndDelete:
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

        var deleteNow = job.OutputPolicy == OutputPolicy.ReplaceAndDelete;
        var keptPath = deleteNow ? null : BuildKeptOriginalPath(sourcePath);

        var watchDir = Path.GetDirectoryName(sourcePath) ?? sourcePath;
        _reconciler.ReportChangeBegin(watchDir);

        try
        {
            if (deleteNow)
            {
                // Still move it aside rather than deleting outright: if putting the new file in
                // place fails, the original must still be there to restore.
                var scratch = sourcePath + ".mo-replacing";
                MoveAcrossVolumes(sourcePath, scratch, overwrite: true);

                try
                {
                    MoveAcrossVolumes(tempOutputPath, finalPath, overwrite: true);
                }
                catch
                {
                    _logger.LogError("[MediaOptimizer] Could not move the new file into place; restoring the original");
                    MoveAcrossVolumes(scratch, sourcePath, overwrite: true);
                    throw;
                }

                File.Delete(scratch);
            }
            else
            {
                // Renaming within the same directory is atomic and instant, however large the file.
                MoveAcrossVolumes(sourcePath, keptPath!, overwrite: false);
                job.QuarantinePath = keptPath;
                job.QuarantineExpiresAt = DateTime.UtcNow.AddDays(Math.Max(0, Config.QuarantineRetentionDays));

                try
                {
                    MoveAcrossVolumes(tempOutputPath, finalPath, overwrite: true);
                }
                catch
                {
                    _logger.LogError("[MediaOptimizer] Could not move the new file into place; restoring the original");
                    MoveAcrossVolumes(keptPath!, sourcePath, overwrite: true);
                    job.QuarantinePath = null;
                    job.QuarantineExpiresAt = null;
                    throw;
                }
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

        await _reconciler.RepointAsync(job.ItemId, finalPath, cancellationToken).ConfigureAwait(false);
        WriteReplacementLog(job, sourcePath, finalPath, keptPath);

        _logger.LogInformation(
            "[MediaOptimizer] Replaced {Source} with {Final}; original {Disposition}",
            sourcePath,
            finalPath,
            keptPath is null ? "deleted" : "kept at " + keptPath);
    }

    /// <summary>
    /// Builds the path a replaced original is kept at. Beside the media by default, using an
    /// extension Jellyfin's scanner ignores so the library never picks it up as a second copy.
    /// </summary>
    /// <param name="sourcePath">The original file.</param>
    /// <returns>Where to keep it.</returns>
    internal string BuildKeptOriginalPath(string sourcePath)
    {
        if (Config.KeepOriginalsBesideMedia)
        {
            var directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(directory) && IsWritable(directory))
            {
                var candidate = sourcePath + OriginalSuffix;
                var counter = 2;
                while (File.Exists(candidate))
                {
                    candidate = sourcePath + "." + counter.ToString(CultureInfo.InvariantCulture) + OriginalSuffix;
                    counter++;
                }

                return candidate;
            }
        }

        return Path.Combine(
            GetQuarantineDirectory(),
            FormattableString.Invariant($"{Guid.NewGuid():N}-{Path.GetFileName(sourcePath)}"));
    }

    /// <summary>
    /// Appends a plain-text record of every replacement, so there is a readable trail independent
    /// of the plugin's own job history.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="sourcePath">The original path.</param>
    /// <param name="finalPath">Where the new file ended up.</param>
    /// <param name="keptPath">Where the original was kept, or null when it was deleted.</param>
    private void WriteReplacementLog(EncodeJob job, string sourcePath, string finalPath, string? keptPath)
    {
        try
        {
            var logPath = Path.Combine(_appPaths.DataPath, "mediaoptimizer", "replacements.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:u}\t{1}\t{2} -> {3}\t{4} -> {5}\toriginal: {6}",
                DateTime.UtcNow,
                job.ItemName,
                Path.GetFileName(sourcePath),
                Path.GetFileName(finalPath),
                FormatSize(job.SourceSizeBytes),
                FormatSize(job.OutputSizeBytes),
                keptPath is null
                    ? "deleted"
                    : FormattableString.Invariant($"kept until {job.QuarantineExpiresAt:yyyy-MM-dd} at {keptPath}"));

            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not append to the replacement log");
        }
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is not > 0)
        {
            return "unknown";
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:F2} GiB", bytes.Value / 1024d / 1024d / 1024d);
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
