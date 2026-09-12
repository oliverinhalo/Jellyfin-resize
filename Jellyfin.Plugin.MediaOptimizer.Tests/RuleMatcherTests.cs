using System;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Which files an automatic rule takes. This decides what gets rewritten while nobody is
/// watching, so every filter is checked in both directions, and every refusal has to carry a
/// reason a person can read.
/// </summary>
public class RuleMatcherTests
{
    private static readonly DateTime Now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AutomationRule Rule() => new AutomationRule
    {
        Name = "Big 4K films",
        Enabled = true,
        AddedMoreThanDaysAgo = null
    };

    private static RuleCandidate Candidate() => new RuleCandidate
    {
        ItemId = Guid.NewGuid(),
        Name = "A Film (2019)",
        ItemType = "Movie",
        Container = "mkv",
        VideoCodec = "h264",
        Height = 2160,
        SizeBytes = 40L * 1024 * 1024 * 1024,
        IsWatched = true,
        DateCreated = Now.AddYears(-1),
        HasActiveJob = false,
        PreviouslyOptimized = false
    };

    [Fact]
    public void A_plain_rule_takes_a_plain_file()
    {
        Assert.True(RuleMatcher.Evaluate(Rule(), Candidate(), Now).Matches);
    }

    [Fact]
    public void A_rule_that_is_switched_off_takes_nothing()
    {
        var rule = Rule();
        rule.Enabled = false;

        var decision = RuleMatcher.Evaluate(rule, Candidate(), Now);

        Assert.False(decision.Matches);
        Assert.Contains("switched off", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converting a conversion compounds quality loss, and an automatic rule would do it on
    /// every run forever.
    /// </summary>
    [Fact]
    public void An_item_this_plugin_already_converted_is_never_taken_again()
    {
        var candidate = Candidate();
        candidate.PreviouslyOptimized = true;

        var decision = RuleMatcher.Evaluate(Rule(), candidate, Now);

        Assert.False(decision.Matches);
        Assert.Contains("Already converted", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_already_in_the_queue_is_not_queued_twice()
    {
        var candidate = Candidate();
        candidate.HasActiveJob = true;

        Assert.False(RuleMatcher.Evaluate(Rule(), candidate, Now).Matches);
    }

    [Theory]
    [InlineData(RuleItemKinds.MoviesOnly, "Movie", true)]
    [InlineData(RuleItemKinds.MoviesOnly, "Episode", false)]
    [InlineData(RuleItemKinds.EpisodesOnly, "Episode", true)]
    [InlineData(RuleItemKinds.EpisodesOnly, "Movie", false)]
    [InlineData(RuleItemKinds.Everything, "Video", true)]
    public void The_kind_filter_selects_films_or_episodes(RuleItemKinds kinds, string itemType, bool expected)
    {
        var rule = Rule();
        rule.Kinds = kinds;
        var candidate = Candidate();
        candidate.ItemType = itemType;

        Assert.Equal(expected, RuleMatcher.Evaluate(rule, candidate, Now).Matches);
    }

    [Theory]
    [InlineData(1080, 2160, true)]
    [InlineData(2160, 2160, true)]
    [InlineData(2160, 1080, false)]
    public void The_height_floor_is_inclusive(int floor, int height, bool expected)
    {
        var rule = Rule();
        rule.MinHeight = floor;
        var candidate = Candidate();
        candidate.Height = height;

        Assert.Equal(expected, RuleMatcher.Evaluate(rule, candidate, Now).Matches);
    }

    /// <summary>
    /// A file whose resolution or size is unknown must not slip through a filter about resolution
    /// or size. Silence is not agreement.
    /// </summary>
    [Fact]
    public void An_unknown_value_does_not_satisfy_a_filter_about_it()
    {
        var rule = Rule();
        rule.MinHeight = 1080;
        rule.MinSizeMb = 4096;

        var noHeight = Candidate();
        noHeight.Height = null;
        Assert.False(RuleMatcher.Evaluate(rule, noHeight, Now).Matches);

        var noSize = Candidate();
        noSize.SizeBytes = null;
        Assert.False(RuleMatcher.Evaluate(rule, noSize, Now).Matches);
    }

    [Fact]
    public void The_size_floor_is_read_in_megabytes()
    {
        var rule = Rule();
        rule.MinSizeMb = 4096;

        var small = Candidate();
        small.SizeBytes = 2L * 1024 * 1024 * 1024;
        Assert.False(RuleMatcher.Evaluate(rule, small, Now).Matches);

        var large = Candidate();
        large.SizeBytes = 5L * 1024 * 1024 * 1024;
        Assert.True(RuleMatcher.Evaluate(rule, large, Now).Matches);
    }

    [Theory]
    [InlineData("mkv", "mkv", true)]
    [InlineData(".mkv", "mkv", true)]
    [InlineData("MKV", "mkv", true)]
    [InlineData("mp4", "mkv", false)]
    public void The_container_filter_ignores_dots_and_case(string filter, string actual, bool expected)
    {
        var rule = Rule();
        rule.Container = filter;
        var candidate = Candidate();
        candidate.Container = actual;

        Assert.Equal(expected, RuleMatcher.Evaluate(rule, candidate, Now).Matches);
    }

    [Theory]
    [InlineData("h264", "h264", true)]
    [InlineData("H264", "h264", true)]
    [InlineData("h264", "hevc", false)]
    public void The_codec_filter_ignores_case(string filter, string actual, bool expected)
    {
        var rule = Rule();
        rule.VideoCodec = filter;
        var candidate = Candidate();
        candidate.VideoCodec = actual;

        Assert.Equal(expected, RuleMatcher.Evaluate(rule, candidate, Now).Matches);
    }

    [Theory]
    [InlineData(WatchedFilter.Watched, true, true)]
    [InlineData(WatchedFilter.Watched, false, false)]
    [InlineData(WatchedFilter.Unwatched, false, true)]
    [InlineData(WatchedFilter.Unwatched, true, false)]
    [InlineData(WatchedFilter.Any, false, true)]
    public void The_watched_filter_works_in_both_directions(WatchedFilter filter, bool watched, bool expected)
    {
        var rule = Rule();
        rule.Watched = filter;
        var candidate = Candidate();
        candidate.IsWatched = watched;

        Assert.Equal(expected, RuleMatcher.Evaluate(rule, candidate, Now).Matches);
    }

    /// <summary>
    /// The grace period is what stops a rule replacing a file the evening it arrives, before
    /// anyone has watched it once or noticed the download was bad.
    /// </summary>
    [Fact]
    public void A_recently_added_file_is_left_alone_until_the_grace_period_passes()
    {
        var rule = Rule();
        rule.AddedMoreThanDaysAgo = 30;

        var fresh = Candidate();
        fresh.DateCreated = Now.AddDays(-3);
        var decision = RuleMatcher.Evaluate(rule, fresh, Now);
        Assert.False(decision.Matches);
        Assert.Contains("30 days", decision.Reason!, StringComparison.Ordinal);

        var settled = Candidate();
        settled.DateCreated = Now.AddDays(-31);
        Assert.True(RuleMatcher.Evaluate(rule, settled, Now).Matches);
    }

    [Theory]
    [InlineData(15, 1000, 500, true)]
    [InlineData(15, 1000, 900, false)]
    [InlineData(0, 1000, 1000, true)]
    [InlineData(15, 1000, 850, true)]
    public void A_saving_below_the_floor_is_not_worth_the_encode(
        int floor,
        long current,
        long estimated,
        bool expected)
    {
        var rule = Rule();
        rule.MinSavingPercent = floor;

        Assert.Equal(expected, RuleMatcher.IsSavingWorthwhile(rule, current, estimated).Matches);
    }

    [Fact]
    public void The_saving_refusal_quotes_both_numbers()
    {
        var rule = Rule();
        rule.MinSavingPercent = 25;

        var decision = RuleMatcher.IsSavingWorthwhile(rule, 1000, 950);

        Assert.False(decision.Matches);
        Assert.Contains("5%", decision.Reason!, StringComparison.Ordinal);
        Assert.Contains("25%", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>Every refusal has to say something; a blank reason is a bug in itself.</summary>
    [Fact]
    public void Every_refusal_carries_a_reason()
    {
        var rule = Rule();
        rule.Kinds = RuleItemKinds.EpisodesOnly;
        rule.MinHeight = 4320;
        rule.MinSizeMb = 1_000_000;
        rule.Container = "mp4";
        rule.VideoCodec = "av1";
        rule.Watched = WatchedFilter.Unwatched;
        rule.AddedMoreThanDaysAgo = 3650;

        foreach (var candidate in new[] { Candidate(), new RuleCandidate { ItemType = "Movie" } })
        {
            var decision = RuleMatcher.Evaluate(rule, candidate, Now);
            Assert.False(decision.Matches);
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        }
    }
}
