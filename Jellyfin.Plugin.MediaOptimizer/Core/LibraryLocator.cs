using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>One of the server's libraries, reduced to the two facts needed to place a file in it.</summary>
public class LibraryLocation
{
    /// <summary>Initializes a new instance of the <see cref="LibraryLocation"/> class.</summary>
    public LibraryLocation()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LibraryLocation"/> class.</summary>
    /// <param name="name">The library's display name.</param>
    /// <param name="paths">The folders it is made of.</param>
    public LibraryLocation(string name, IReadOnlyList<string> paths)
    {
        Name = name;
        Paths = paths;
    }

    /// <summary>Gets or sets the library's display name, as shown in Jellyfin.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the folders the library is made of.</summary>
    public IReadOnlyList<string> Paths { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Works out which library a file belongs to from its path.
/// <para>
/// Deliberately done by path rather than by walking the item's parents: a rule that says "only the
/// TV library" has to mean the same thing every time it runs, and a path prefix is something a
/// person can check for themselves against Dashboard → Libraries. Kept pure so the matching rules
/// — trailing separators, nested libraries, case — are testable without a server.
/// </para>
/// </summary>
public static class LibraryLocator
{
    /// <summary>Finds the library a path sits in.</summary>
    /// <param name="path">The media file's path.</param>
    /// <param name="libraries">The server's libraries.</param>
    /// <returns>The library's name, or null when the path is in none of them.</returns>
    public static string? NameFor(string? path, IReadOnlyList<LibraryLocation> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? best = null;
        var bestLength = -1;

        foreach (var library in libraries)
        {
            foreach (var folder in library.Paths)
            {
                if (!IsInside(path, folder))
                {
                    continue;
                }

                // A library nested inside another -- "/media" and "/media/kids" both configured --
                // belongs to the more specific one, which is the only reading that lets someone
                // write a rule for the nested library at all.
                var length = Normalise(folder).Length;
                if (length > bestLength)
                {
                    bestLength = length;
                    best = library.Name;
                }
            }
        }

        return best;
    }

    /// <summary>Whether a path sits inside a folder, respecting directory boundaries.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether the file is in that folder or below it.</returns>
    internal static bool IsInside(string path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var root = Normalise(folder);
        var candidate = Normalise(path);

        if (root.Length == 0 || candidate.Length <= root.Length)
        {
            return false;
        }

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Without this, "/media/Movies" would claim "/media/Movies 2/film.mkv".
        var next = candidate[root.Length];
        return next == '/' || next == '\\';
    }

    /// <summary>Trims a path to a comparable form: no trailing separator, consistent separators.</summary>
    /// <param name="value">The path.</param>
    /// <returns>The normalised path.</returns>
    private static string Normalise(string value)
    {
        var trimmed = value.Trim();

        // Windows paths arrive with backslashes and Jellyfin stores some with forward slashes;
        // comparing them raw makes a library's own files look as though they are outside it.
        if (Path.DirectorySeparatorChar == '\\')
        {
            trimmed = trimmed.Replace('/', '\\');
        }

        return trimmed.TrimEnd('/', '\\');
    }
}
