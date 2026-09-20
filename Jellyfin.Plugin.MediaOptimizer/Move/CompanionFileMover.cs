using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Move;

/// <summary>
/// Takes the files that belong to a media file — artwork, .nfo, external subtitles — along with
/// it when it moves to another folder.
/// <para>
/// This lives with the move feature rather than with the replace path, and the distinction is
/// the whole reason it exists. Jellyfin finds every one of these by the media file's name
/// <em>without</em> its extension, so a replacement that turns Film.mkv into Film.mp4 needs
/// nothing moved: the companions are already named correctly and already in the right folder.
/// A move to another drive is the opposite case — the name is unchanged and the folder is not —
/// and leaving them behind strips a film of its poster, its metadata and its subtitles with
/// nothing in the log to say why.
/// </para>
/// </summary>
public static class CompanionFileMover
{
    private static readonly string[] CompanionExtensions =
    [
        ".nfo", ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup"
    ];

    private static readonly string[] CompanionSuffixes =
    [
        "-poster.jpg", "-poster.png", "-fanart.jpg", "-fanart.png",
        "-thumb.jpg", "-thumb.png", "-banner.jpg", "-logo.png"
    ];

    /// <summary>
    /// Moves every companion of <paramref name="oldPath"/> to sit beside <paramref name="newPath"/>.
    /// </summary>
    /// <param name="oldPath">Where the media file was.</param>
    /// <param name="newPath">Where the media file now is.</param>
    /// <param name="logger">Logger, for the ones that could not be moved.</param>
    /// <returns>The companion files that were moved, at their new paths.</returns>
    public static IReadOnlyList<string> MoveAlongside(string oldPath, string newPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var moved = new List<string>();

        var oldDir = Path.GetDirectoryName(oldPath);
        var newDir = Path.GetDirectoryName(newPath);
        var oldBase = Path.GetFileNameWithoutExtension(oldPath);
        var newBase = Path.GetFileNameWithoutExtension(newPath);

        // Nothing to do when the companions would end up exactly where they already are.
        if (string.IsNullOrEmpty(oldDir)
            || string.IsNullOrEmpty(newDir)
            || (oldBase == newBase && string.Equals(oldDir, newDir, StringComparison.Ordinal)))
        {
            return moved;
        }

        foreach (var candidate in EnumerateCompanions(oldDir, oldBase))
        {
            var fileName = Path.GetFileName(candidate);
            var newName = newBase + fileName[oldBase.Length..];
            var destination = Path.Combine(newDir, newName);

            // A file already there is not ours to replace: it belongs to whatever is in that
            // folder now, and the move is not worth overwriting it for.
            if (File.Exists(destination))
            {
                continue;
            }

            try
            {
                // The destination is usually on another drive, where a rename is not possible.
                OutputPolicyService.MoveAcrossVolumes(candidate, destination, overwrite: false);
                moved.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The media file has already moved, so this is worth saying out loud, but it is
                // not worth failing the move over: the film plays, it is missing its poster.
                logger.LogWarning(ex, "[MediaOptimizer] Could not move companion file {Path}", candidate);
            }
        }

        return moved;
    }

    private static IEnumerable<string> EnumerateCompanions(string directory, string baseName)
    {
        foreach (var ext in CompanionExtensions)
        {
            var exact = Path.Combine(directory, baseName + ext);
            if (File.Exists(exact))
            {
                yield return exact;
            }

            // Language-tagged subtitles: Movie.en.srt, Movie.en.forced.srt
            string[] matches;
            try
            {
                matches = Directory.GetFiles(directory, baseName + ".*" + ext);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var m in matches)
            {
                yield return m;
            }
        }

        foreach (var suffix in CompanionSuffixes)
        {
            var candidate = Path.Combine(directory, baseName + suffix);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }
}
