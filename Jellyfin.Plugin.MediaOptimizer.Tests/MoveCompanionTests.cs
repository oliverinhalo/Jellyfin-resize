using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Artwork, .nfo files and external subtitles are found by basename next to the media file. Left
/// behind by a move to another drive, a film silently loses its poster, its metadata and its
/// subtitles — with nothing in the log to say why.
/// </summary>
public class MoveCompanionTests : IDisposable
{
    private readonly string _dir;

    public MoveCompanionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-companions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Test scratch space.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Moving companion files touches the file system and the logger and nothing else, so the
    /// Jellyfin services are not built here. A future change that makes the method reach for one
    /// of them will fail loudly in this test, which is the right place to notice.
    /// </summary>
    /// <returns>A reconciler usable for companion moves only.</returns>
    private static LibraryReconciler NewReconciler() =>
        new LibraryReconciler(null!, null!, null!, null!, null!, NullLogger<LibraryReconciler>.Instance);

    private string Touch(string relative, string contents = "x")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Companions_follow_a_file_that_keeps_its_name_but_changes_folder()
    {
        Touch("old/film.mkv");
        Touch("old/film.nfo");
        Touch("old/film.en.srt");
        Touch("old/film-poster.jpg");
        Touch("old/unrelated.txt");
        Directory.CreateDirectory(Path.Combine(_dir, "new"));

        var moved = NewReconciler().MoveCompanionFiles(
            Path.Combine(_dir, "old", "film.mkv"),
            Path.Combine(_dir, "new", "film.mkv"));

        Assert.Equal(3, moved.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "new", "film.nfo")));
        Assert.True(File.Exists(Path.Combine(_dir, "new", "film.en.srt")));
        Assert.True(File.Exists(Path.Combine(_dir, "new", "film-poster.jpg")));

        Assert.False(File.Exists(Path.Combine(_dir, "old", "film.nfo")));
        Assert.True(File.Exists(Path.Combine(_dir, "old", "unrelated.txt")));
    }

    [Fact]
    public void Nothing_is_touched_when_the_file_is_not_actually_going_anywhere()
    {
        Touch("old/film.mkv");
        Touch("old/film.nfo");

        var moved = NewReconciler().MoveCompanionFiles(
            Path.Combine(_dir, "old", "film.mkv"),
            Path.Combine(_dir, "old", "film.mkv"));

        Assert.Empty(moved);
        Assert.True(File.Exists(Path.Combine(_dir, "old", "film.nfo")));
    }

    [Fact]
    public void A_companion_already_present_at_the_destination_is_left_alone()
    {
        Touch("old/film.mkv");
        Touch("old/film.nfo", "the one being moved");
        Touch("new/film.nfo", "the one already there");

        var moved = NewReconciler().MoveCompanionFiles(
            Path.Combine(_dir, "old", "film.mkv"),
            Path.Combine(_dir, "new", "film.mkv"));

        Assert.Empty(moved);
        Assert.Equal("the one already there", File.ReadAllText(Path.Combine(_dir, "new", "film.nfo")));
        Assert.Equal("the one being moved", File.ReadAllText(Path.Combine(_dir, "old", "film.nfo")));
    }

    [Fact]
    public void Changing_only_the_extension_leaves_the_companions_where_they_are()
    {
        // Jellyfin finds them by basename, and a conversion from .mkv to .mp4 does not change it,
        // so the poster and the .nfo are already in exactly the right place.
        Touch("old/film.mkv");
        Touch("old/film.nfo");

        var moved = NewReconciler().MoveCompanionFiles(
            Path.Combine(_dir, "old", "film.mkv"),
            Path.Combine(_dir, "old", "film.mp4"));

        Assert.Empty(moved);
        Assert.Equal(
            new[] { "film.mkv", "film.nfo" },
            Directory.GetFiles(Path.Combine(_dir, "old")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_rename_that_changes_the_basename_carries_its_companions()
    {
        Touch("old/film.mkv");
        Touch("old/film.nfo");

        var moved = NewReconciler().MoveCompanionFiles(
            Path.Combine(_dir, "old", "film.mkv"),
            Path.Combine(_dir, "old", "film - Optimized.mkv"));

        Assert.Single(moved);
        Assert.True(File.Exists(Path.Combine(_dir, "old", "film - Optimized.nfo")));
        Assert.False(File.Exists(Path.Combine(_dir, "old", "film.nfo")));
    }
}
