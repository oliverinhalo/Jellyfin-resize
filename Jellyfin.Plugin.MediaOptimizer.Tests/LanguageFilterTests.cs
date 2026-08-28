using System;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Dropping audio tracks is the largest bit-exact saving available, but getting it wrong means
/// silently deleting the only audio a file has.
/// </summary>
public class LanguageFilterTests
{
    [Theory]
    [InlineData("en", "eng")]
    [InlineData("eng", "eng")]
    [InlineData("English", "eng")]
    [InlineData("en-GB", "eng")]
    [InlineData("pt_BR", "por")]
    [InlineData("fre", "fra")]
    [InlineData("und", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Any_spelling_of_a_language_reduces_to_one_code(string? input, string? expected) =>
        Assert.Equal(expected, LanguageMatcher.Normalize(input));

    [Fact]
    public void A_keep_list_accepts_whatever_the_user_types()
    {
        var parsed = LanguageMatcher.ParseList("English, fr; de  ja");
        Assert.Equal(["eng", "fra", "deu", "jpn"], parsed);
    }

    [Fact]
    public void An_empty_keep_list_keeps_everything() =>
        Assert.True(LanguageMatcher.ShouldKeep("jpn", LanguageMatcher.ParseList(""), keepUntagged: false));

    [Fact]
    public void Untagged_tracks_are_kept_by_default()
    {
        var keep = LanguageMatcher.ParseList("eng");
        Assert.True(LanguageMatcher.ShouldKeep(null, keep, keepUntagged: true));
        Assert.False(LanguageMatcher.ShouldKeep(null, keep, keepUntagged: false));
    }

    private static Capabilities Caps() => new Capabilities
    {
        VideoEncoders = [new EncoderOption { Name = "libx265", Codec = "hevc", DisplayName = "x265", Supports10Bit = true, Presets = ["medium"] }],
        AudioEncoders = [new EncoderOption { Name = "libopus", Codec = "opus", DisplayName = "Opus" }],
        AllowHevcEncoding = true
    };

    private static FileAnalysis MultiLanguage() => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "Film",
        Path = "/media/film.mkv",
        SizeBytes = 5_000_000_000,
        DurationSeconds = 7000,
        IsEligible = true,
        IsWritable = true,
        OverallBitrate = new BitrateInfo { Bps = 6_000_000 },
        Video = new VideoTrackInfo { Index = 0, Codec = "h264", Width = 1920, Height = 1080, Bitrate = new BitrateInfo { Bps = 5_000_000 } },
        Audio =
        [
            new AudioTrackInfo { Index = 1, TypeIndex = 0, Codec = "eac3", Language = "eng", IsDefault = true, Channels = 6, Bitrate = new BitrateInfo { Bps = 640_000 } },
            new AudioTrackInfo { Index = 2, TypeIndex = 1, Codec = "eac3", Language = "fra", Channels = 6, Bitrate = new BitrateInfo { Bps = 640_000 } },
            new AudioTrackInfo { Index = 3, TypeIndex = 2, Codec = "eac3", Language = "spa", Channels = 6, Bitrate = new BitrateInfo { Bps = 640_000 } },
            new AudioTrackInfo { Index = 4, TypeIndex = 3, Codec = "ac3", Language = "eng", Title = "Director's Commentary", Channels = 2, Bitrate = new BitrateInfo { Bps = 192_000 } }
        ],
        Subtitles =
        [
            new SubtitleTrackInfo { Index = 5, Codec = "subrip", Language = "eng" },
            new SubtitleTrackInfo { Index = 6, Codec = "subrip", Language = "fra" }
        ]
    };

    [Fact]
    public void Only_the_wanted_languages_are_kept()
    {
        var analysis = MultiLanguage();
        var config = new PluginConfiguration { KeepAudioLanguages = "eng" };

        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, Caps(), config);

        var kept = request.AudioTracks.Where(t => t.Action != AudioAction.Drop).Select(t => t.Index).ToList();
        Assert.Contains(1, kept);
        Assert.Contains(4, kept);      // English commentary survives unless commentary is filtered too
        Assert.DoesNotContain(2, kept);
        Assert.DoesNotContain(3, kept);
    }

    [Fact]
    public void Commentary_can_be_dropped_separately_from_language()
    {
        var config = new PluginConfiguration { KeepAudioLanguages = "eng", DropCommentaryTracks = true };
        var request = StrategyResolver.Resolve(MultiLanguage(), OptimizationStrategy.Standard, Caps(), config);

        var kept = request.AudioTracks.Where(t => t.Action != AudioAction.Drop).Select(t => t.Index).ToList();
        Assert.Equal([1], kept);
    }

    [Theory]
    [InlineData("Director's Commentary", true)]
    [InlineData("Commentary with the cast", true)]
    [InlineData("Audio Description", true)]
    [InlineData("English 5.1", false)]
    [InlineData(null, false)]
    public void Commentary_is_recognised_from_the_track_title(string? title, bool expected) =>
        Assert.Equal(expected, StrategyResolver.IsCommentary(title));

    [Fact]
    public void A_file_is_never_left_with_no_audio_at_all()
    {
        // The keep-list matches nothing in this file; dropping everything would be worse than
        // keeping a language the user does not speak.
        var analysis = MultiLanguage();
        var config = new PluginConfiguration { KeepAudioLanguages = "jpn", KeepUntaggedTracks = false };

        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, Caps(), config);

        var kept = request.AudioTracks.Where(t => t.Action != AudioAction.Drop).ToList();
        Assert.Single(kept);
        Assert.Equal(1, kept[0].Index);   // the default track
    }

    [Fact]
    public void Subtitles_are_filtered_by_their_own_list()
    {
        var config = new PluginConfiguration { KeepSubtitleLanguages = "eng" };
        var request = StrategyResolver.Resolve(MultiLanguage(), OptimizationStrategy.Standard, Caps(), config);

        Assert.NotNull(request.KeepSubtitleIndexes);
        Assert.Equal([5], request.KeepSubtitleIndexes!);
    }

    [Fact]
    public void Dropping_languages_produces_a_real_saving()
    {
        var analysis = MultiLanguage();
        var caps = Caps();
        var estimator = new SizeEstimator(new EmptyStore());

        var keepAll = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, caps, new PluginConfiguration());
        var englishOnly = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, caps,
            new PluginConfiguration { KeepAudioLanguages = "eng", DropCommentaryTracks = true });

        var withAll = estimator.Estimate(analysis, keepAll, new PlanResult());
        var withEnglish = estimator.Estimate(analysis, englishOnly, new PlanResult());

        Assert.True(withEnglish.EstimatedSizeBytes < withAll.EstimatedSizeBytes,
            $"english-only ({withEnglish.EstimatedSizeBytes}) should be smaller than all languages ({withAll.EstimatedSizeBytes})");
    }

    [Fact]
    public void No_language_filter_leaves_every_track_alone()
    {
        var request = StrategyResolver.Resolve(MultiLanguage(), OptimizationStrategy.Standard, Caps(), new PluginConfiguration());
        Assert.All(request.AudioTracks, t => Assert.NotEqual(AudioAction.Drop, t.Action));
        Assert.Null(request.KeepSubtitleIndexes);
    }

    private sealed class EmptyStore : Jellyfin.Plugin.MediaOptimizer.Jobs.IJobStore
    {
        public void Add(EncodeJob job)
        {
        }

        public void Update(EncodeJob job)
        {
        }

        public EncodeJob? Get(Guid id) => null;

        public System.Collections.Generic.IReadOnlyList<EncodeJob> GetAll() => Array.Empty<EncodeJob>();

        public System.Collections.Generic.IReadOnlyList<EncodeJob> GetActive() => Array.Empty<EncodeJob>();

        public EncodeJob? TakeNextQueued() => null;

        public bool HasActiveJobForItem(Guid itemId) => false;

        public bool Remove(Guid id) => false;

        public System.Collections.Generic.IReadOnlyList<EncodeJob> ReconcileInterrupted() => Array.Empty<EncodeJob>();
    }
}
