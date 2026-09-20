using System;
using System.IO;
using Jellyfin.Plugin.MediaOptimizer.Move;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The rules that decide where a moved file lands. Getting these wrong scatters a library across
/// a drive or, worse, sends a file somewhere the library can no longer find it, so every branch
/// is pinned here rather than exercised only through a real move.
/// </summary>
public class MovePathTests
{
    private static string Slash(string path) => path.Replace('\\', '/');

    [Fact]
    public void Folder_layout_below_the_library_folder_is_preserved()
    {
        var destination = MovePathPlanner.BuildDestinationPath(
            "/media/movies/Dune (2021)/Dune.mkv",
            "/media/movies",
            "/mnt/disk2/movies");

        Assert.Equal("/mnt/disk2/movies/Dune (2021)/Dune.mkv", Slash(destination));
    }

    [Fact]
    public void A_trailing_separator_on_either_folder_changes_nothing()
    {
        var destination = MovePathPlanner.BuildDestinationPath(
            "/media/movies/Dune (2021)/Dune.mkv",
            "/media/movies/",
            "/mnt/disk2/movies/");

        Assert.Equal("/mnt/disk2/movies/Dune (2021)/Dune.mkv", Slash(destination));
    }

    [Fact]
    public void Windows_paths_keep_their_layout_and_their_drive()
    {
        var destination = MovePathPlanner.BuildDestinationPath(
            @"D:\media\movies\Dune (2021)\Dune.mkv",
            @"D:\media\movies",
            @"E:\media\movies");

        Assert.Equal(@"E:/media/movies/Dune (2021)/Dune.mkv", Slash(destination));
    }

    [Fact]
    public void A_file_outside_every_library_folder_lands_in_the_destination_itself()
    {
        var destination = MovePathPlanner.BuildDestinationPath(
            "/elsewhere/Dune.mkv",
            null,
            "/mnt/disk2/movies");

        Assert.Equal("/mnt/disk2/movies/Dune.mkv", Slash(destination));
    }

    [Fact]
    public void A_drive_root_is_a_usable_library_folder()
    {
        var destination = MovePathPlanner.BuildDestinationPath(
            @"D:\Dune (2021)\Dune.mkv",
            @"D:\",
            @"E:\media");

        Assert.Equal(@"E:/media/Dune (2021)/Dune.mkv", Slash(destination));
    }

    [Theory]
    [InlineData("/media/movies/Dune.mkv", "/media/movies", true)]
    [InlineData("/media/movies/Dune.mkv", "/media/movies/", true)]
    [InlineData("/media/movies-4k/Dune.mkv", "/media/movies", false)]
    [InlineData("/media/movies", "/media/movies", false)]
    [InlineData(@"D:\media\Dune.mkv", @"D:\", true)]
    [InlineData("/other/Dune.mkv", "/media/movies", false)]
    public void Containment_does_not_confuse_a_sibling_folder_for_a_parent(string path, string directory, bool expected)
    {
        Assert.Equal(expected, MovePathPlanner.IsUnder(path, directory));
    }

    [Fact]
    public void The_most_specific_library_folder_claims_the_file()
    {
        var root = MovePathPlanner.ResolveRoot(
            "/media/movies/4k/Dune.mkv",
            ["/media", "/media/movies", "/media/movies/4k", "/media/shows"]);

        Assert.Equal("/media/movies/4k", root);
    }

    [Fact]
    public void A_file_under_no_library_folder_resolves_to_nothing()
    {
        Assert.Null(MovePathPlanner.ResolveRoot("/elsewhere/Dune.mkv", ["/media/movies"]));
    }

    [Fact]
    public void On_windows_a_different_drive_letter_goes_straight_to_the_copy_loop()
    {
        Assert.False(MovePathPlanner.CanTryInstantRename(@"D:\a\f.mkv", @"E:\b\f.mkv", windowsSemantics: true));
        Assert.True(MovePathPlanner.CanTryInstantRename(@"D:\a\f.mkv", @"D:\b\f.mkv", windowsSemantics: true));
    }

    [Fact]
    public void On_unix_a_rename_is_always_worth_attempting_because_it_fails_cleanly()
    {
        Assert.True(MovePathPlanner.CanTryInstantRename("/media/a/f.mkv", "/mnt/b/f.mkv", windowsSemantics: false));
    }

    [Theory]
    [InlineData(@"D:\media\movies", @"D:\")]
    [InlineData(@"d:\media", @"d:\")]
    [InlineData(@"\\nas\media\movies", @"\\nas\media")]
    public void A_drive_or_share_is_recognised_whatever_the_host_runs(string path, string expected)
    {
        Assert.Equal(
            expected.Replace('\\', Path.DirectorySeparatorChar),
            MovePathPlanner.TryGetRoot(path).Replace('\\', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void The_staging_file_is_hidden_and_carries_an_extension_the_scanner_ignores()
    {
        var id = Guid.NewGuid();
        var staging = MovePathPlanner.BuildStagingPath("/mnt/disk2/movies/Dune (2021)/Dune.mkv", id);

        Assert.Equal("/mnt/disk2/movies/Dune (2021)", Slash(Path.GetDirectoryName(staging)!));
        Assert.StartsWith(".mo-move-", Path.GetFileName(staging), StringComparison.Ordinal);
        Assert.EndsWith(".momoving", staging, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mnt/a\n/mnt/b", 2)]
    [InlineData("/mnt/a; /mnt/b ;", 2)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Extra_destinations_can_be_written_one_per_line_or_separated_by_semicolons(string? configured, int expected)
    {
        Assert.Equal(expected, MediaMoveService.ParseExtraTargets(configured).Count);
    }
}
