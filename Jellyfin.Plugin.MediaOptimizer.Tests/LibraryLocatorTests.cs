using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Placing a file in a library by its path. A rule confined to one library has to mean the same
/// thing every time it runs, and getting this wrong in either direction is bad: claiming a film is
/// in the TV library converts something a rule was written to leave alone.
/// </summary>
public class LibraryLocatorTests
{
    private static readonly LibraryLocation[] Libraries =
    [
        new LibraryLocation("Films", ["/media/movies", "/mnt/archive/films/"]),
        new LibraryLocation("TV", ["/media/tv"]),
        new LibraryLocation("Kids", ["/media/tv/kids"])
    ];

    [Theory]
    [InlineData("/media/movies/A Film (2019)/film.mkv", "Films")]
    [InlineData("/mnt/archive/films/Another.mkv", "Films")]
    [InlineData("/media/tv/Show/S01E01.mkv", "TV")]
    public void A_file_is_placed_in_the_library_it_sits_under(string path, string expected)
    {
        Assert.Equal(expected, LibraryLocator.NameFor(path, Libraries));
    }

    /// <summary>
    /// A library configured inside another belongs to the more specific one — otherwise a rule
    /// could never be written for the nested library at all.
    /// </summary>
    [Fact]
    public void A_nested_library_wins_over_the_one_containing_it()
    {
        Assert.Equal("Kids", LibraryLocator.NameFor("/media/tv/kids/Show/ep.mkv", Libraries));
    }

    /// <summary>
    /// Prefix matching without respecting directory boundaries would have "/media/movies" claim
    /// everything in "/media/movies 2".
    /// </summary>
    [Theory]
    [InlineData("/media/movies 2/film.mkv")]
    [InlineData("/media/movieshd/film.mkv")]
    [InlineData("/media/tvshows/ep.mkv")]
    public void A_folder_that_merely_starts_the_same_is_not_the_same_folder(string path)
    {
        Assert.Null(LibraryLocator.NameFor(path, Libraries));
    }

    [Fact]
    public void A_trailing_separator_in_the_library_path_makes_no_difference()
    {
        var withSlash = new[] { new LibraryLocation("Films", ["/media/movies/"]) };
        var without = new[] { new LibraryLocation("Films", ["/media/movies"]) };

        Assert.Equal("Films", LibraryLocator.NameFor("/media/movies/a.mkv", withSlash));
        Assert.Equal("Films", LibraryLocator.NameFor("/media/movies/a.mkv", without));
    }

    [Fact]
    public void A_file_in_no_library_belongs_to_none()
    {
        Assert.Null(LibraryLocator.NameFor("/downloads/a.mkv", Libraries));
        Assert.Null(LibraryLocator.NameFor(null, Libraries));
        Assert.Null(LibraryLocator.NameFor("   ", Libraries));
        Assert.Null(LibraryLocator.NameFor("/media/movies/a.mkv", Array.Empty<LibraryLocation>()));
    }

    /// <summary>The library folder itself is not a file in the library.</summary>
    [Fact]
    public void The_library_folder_itself_is_not_a_member_of_itself()
    {
        Assert.Null(LibraryLocator.NameFor("/media/movies", Libraries));
        Assert.Null(LibraryLocator.NameFor("/media/movies/", Libraries));
    }

    [Fact]
    public void A_library_with_no_folders_claims_nothing()
    {
        var empty = new[] { new LibraryLocation("Empty", Array.Empty<string>()) };

        Assert.Null(LibraryLocator.NameFor("/media/movies/a.mkv", empty));
    }

    // --- and the filter that uses it --------------------------------------------------------

    private static AutomationRule Rule(string? library) => new AutomationRule
    {
        Name = "Just the TV",
        Enabled = true,
        AddedMoreThanDaysAgo = null,
        LibraryName = library
    };

    private static RuleCandidate Candidate(string? library) => new RuleCandidate
    {
        ItemId = Guid.NewGuid(),
        Name = "An episode",
        ItemType = "Episode",
        Container = "mkv",
        VideoCodec = "h264",
        Height = 1080,
        SizeBytes = 4L * 1024 * 1024 * 1024,
        LibraryName = library,
        DateCreated = DateTime.UtcNow.AddYears(-1)
    };

    private static readonly DateTime Now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("TV", "TV", true)]
    [InlineData("TV", "tv", true)]
    [InlineData(" TV ", "TV", true)]
    [InlineData("TV", "Films", false)]
    [InlineData(null, "Films", true)]
    public void A_rule_confined_to_a_library_takes_only_that_library(string? ruleLibrary, string candidateLibrary, bool expected)
    {
        Assert.Equal(
            expected,
            RuleMatcher.Evaluate(Rule(ruleLibrary), Candidate(candidateLibrary), Now).Matches);
    }

    /// <summary>
    /// A file the plugin could not place — outside every library folder, or a library list it
    /// could not read — is not quietly treated as being in the named one.
    /// </summary>
    [Fact]
    public void A_file_that_could_not_be_placed_is_not_assumed_to_be_in_the_named_library()
    {
        var decision = RuleMatcher.Evaluate(Rule("TV"), Candidate(null), Now);

        Assert.False(decision.Matches);
        Assert.Contains("not in any library", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_names_both_libraries()
    {
        var decision = RuleMatcher.Evaluate(Rule("TV"), Candidate("Films"), Now);

        Assert.Contains("Films", decision.Reason!, StringComparison.Ordinal);
        Assert.Contains("TV", decision.Reason!, StringComparison.Ordinal);
    }
}
