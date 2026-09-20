using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MediaOptimizer.Move;

/// <summary>
/// Works out where a file lands when it is moved to another drive. Pure string work, kept apart
/// from the file system so the rules that decide a destination can be tested directly.
/// </summary>
public static class MovePathPlanner
{
    /// <summary>Strips trailing separators so two spellings of one folder compare equal.</summary>
    /// <param name="path">A folder path.</param>
    /// <returns>The path without a trailing separator.</returns>
    public static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();

        // A drive root ("D:\", "/") is the one case where the trailing separator is the path.
        var root = TryGetRoot(trimmed);
        if (!string.IsNullOrEmpty(root) && string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.TrimEnd('/', '\\');
    }

    /// <summary>Tells whether a path sits inside a folder.</summary>
    /// <param name="path">The path to test.</param>
    /// <param name="directory">The folder it might be under.</param>
    /// <returns>True when <paramref name="path"/> is inside <paramref name="directory"/>.</returns>
    public static bool IsUnder(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var normalizedDir = NormalizeDirectory(directory);
        var normalizedPath = path.Trim();

        if (normalizedPath.Length <= normalizedDir.Length
            || !normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "/media/movies-4k" must not count as living under "/media/movies".
        var next = normalizedPath[normalizedDir.Length];
        return next is '/' or '\\'
            || normalizedDir.EndsWith('/')
            || normalizedDir.EndsWith('\\');
    }

    /// <summary>
    /// Picks the library folder a file belongs to. The longest match wins, so a nested library
    /// folder is preferred over the parent it sits inside.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="roots">Every known library folder.</param>
    /// <returns>The folder containing the file, or null when none do.</returns>
    public static string? ResolveRoot(string path, IEnumerable<string> roots)
    {
        return roots
            .Where(r => IsUnder(path, r))
            .Select(NormalizeDirectory)
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Builds the destination for one file. The folder layout below the library folder is kept,
    /// so "Movies/Dune (2021)/Dune.mkv" arrives as "Movies2/Dune (2021)/Dune.mkv" and Jellyfin
    /// still finds the artwork and the .nfo next to it.
    /// <para>
    /// A file that is not under any library folder has no layout to preserve, so it lands
    /// directly in the destination folder.
    /// </para>
    /// </summary>
    /// <param name="sourcePath">The file being moved.</param>
    /// <param name="sourceRoot">The library folder it currently lives under, or null.</param>
    /// <param name="destinationRoot">The folder it is moving into.</param>
    /// <returns>The full destination path.</returns>
    public static string BuildDestinationPath(string sourcePath, string? sourceRoot, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var destination = NormalizeDirectory(destinationRoot);

        if (!string.IsNullOrWhiteSpace(sourceRoot) && IsUnder(sourcePath, sourceRoot))
        {
            var root = NormalizeDirectory(sourceRoot);
            var relative = sourcePath.Trim()[root.Length..].TrimStart('/', '\\');

            // Path.Combine keeps whichever separator the relative part already uses, which is what
            // makes a Windows path moved on Windows come out as a Windows path.
            return Path.Combine(destination, relative);
        }

        return Path.Combine(destination, Path.GetFileName(sourcePath.Trim()));
    }

    /// <summary>
    /// Decides whether to attempt an instant rename before falling back to copying every byte.
    /// <para>
    /// On Unix, rename fails cleanly across file systems, so trying it first costs nothing. On
    /// Windows it silently turns into a blocking whole-file copy with no progress at all, so a
    /// move to a different drive letter goes straight to the copy loop, which can report progress
    /// and be cancelled.
    /// </para>
    /// </summary>
    /// <param name="sourcePath">Source file.</param>
    /// <param name="destinationPath">Destination file.</param>
    /// <param name="windowsSemantics">Whether the host moves files the way Windows does.</param>
    /// <returns>True when a rename is worth attempting.</returns>
    public static bool CanTryInstantRename(string sourcePath, string destinationPath, bool windowsSemantics)
    {
        if (!windowsSemantics)
        {
            return true;
        }

        var sourceRoot = TryGetRoot(sourcePath);
        var destinationRoot = TryGetRoot(destinationPath);

        return !string.IsNullOrEmpty(sourceRoot)
            && !string.IsNullOrEmpty(destinationRoot)
            && string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Names the drive a path lives on, for display and for the same-drive test. Falls back to
    /// the path's root when the host does not use drive letters.
    /// </summary>
    /// <param name="path">Any path.</param>
    /// <returns>The drive or root, or an empty string when it cannot be determined.</returns>
    public static string TryGetRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();

        // Path.GetPathRoot only understands the host's own convention, and the dashboard shows
        // Windows paths on a Linux test run often enough to be worth handling directly.
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return trimmed[..2] + Path.DirectorySeparatorChar;
        }

        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            // A UNC share: the share itself is the closest thing to a drive.
            var parts = trimmed.TrimStart('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? trimmed[..2] + parts[0] + trimmed[1] + parts[1]
                : trimmed;
        }

        try
        {
            return Path.GetPathRoot(trimmed) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Names the temporary file a cross-drive copy is written to. It is hidden by a leading dot
    /// and carries an extension Jellyfin's scanner ignores, so a half-copied film is never picked
    /// up as a library item.
    /// </summary>
    /// <param name="destinationPath">Where the file is going.</param>
    /// <param name="jobId">The job doing the move.</param>
    /// <returns>The path to copy into.</returns>
    public static string BuildStagingPath(string destinationPath, Guid jobId)
    {
        var directory = Path.GetDirectoryName(destinationPath) ?? ".";
        return Path.Combine(
            directory,
            FormattableString.Invariant($".mo-move-{jobId:N}.momoving"));
    }
}
