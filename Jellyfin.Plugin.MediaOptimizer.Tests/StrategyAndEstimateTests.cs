using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Modelled on a real file that exposed the estimator predicting growth for every default:
/// a 5 GiB 4K HEVC Main 10 remux at 6.82 Mb/s whose container tags the video stream with the
/// overall bitrate.
/// </summary>
public class StrategyAndEstimateTests
{
    private static Capabilities Caps() => new Capabilities
    {
        VideoEncoders =
        [
            new EncoderOption { Name = "libx264", Codec = "h264", DisplayName = "x264", Supports10Bit = true, Presets = ["medium"] },
            new EncoderOption { Name = "libx265", Codec = "hevc", DisplayName = "x265", Supports10Bit = true, Presets = ["medium"] }
        ],
        AudioEncoders =
        [
            new EncoderOption { Name = "libopus", Codec = "opus", DisplayName = "Opus" },
            new EncoderOption { Name = "flac", Codec = "flac", DisplayName = "FLAC" }
        ],
        AllowHevcEncoding = true,
        AllowAv1Encoding = false
    };

    private static FileAnalysis Coco() => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "Coco",
        Path = "/media/coco.mkv",
        Container = "mkv",
        SizeBytes = 5_368_709_120L,
        DurationSeconds = 6300,
        IsEligible = true,
        IsWritable = true,
        OverallBitrate = new BitrateInfo { Bps = 6_820_000, Source = ValueSource.Measured },
        Video = new VideoTrackInfo
        {
            Index = 0, Codec = "hevc", Profile = "Main 10", Width = 3840, Height = 2160,
            BitDepth = 10, FrameRate = 23.976f, Range = "SDR",
            // The container reports the overall rate for the video stream. Anchoring naively on
            // this and then adding audio on top is what produced the "+6%" prediction.
            Bitrate = new BitrateInfo { Bps = 6_820_000, Source = ValueSource.Measured }
        },
        Audio =
        [
            new AudioTrackInfo
            {
                Index = 1, TypeIndex = 0, Codec = "aac", Channels = 6, ChannelLayout = "5.1",
                SampleRate = 48000, Bitrate = new BitrateInfo { Bps = 320_000, Source = ValueSource.Measured }
            }
        ],
        Subtitles = Enumerable.Range(0, 8)
            .Select(i => new SubtitleTrackInfo { Index = 2 + i, Codec = "hdmv_pgs_subtitle", IsGraphical = true })
            .ToList()
    };

    private static FileAnalysis OldH264()
    {
        var a = Coco();
        a.Video!.Codec = "h264";
        a.Video.Profile = "High";
        a.Video.Width = 1920;
        a.Video.Height = 1080;
        a.Video.BitDepth = 8;
        return a;
    }

    private static SizeEstimator Estimator() => new SizeEstimator(new InMemoryJobStore());

    private static EstimateResult EstimateFor(FileAnalysis analysis, EncodeRequest request) =>
        Estimator().Estimate(analysis, request, new PlanResult());

    [Fact]
    public void No_preset_ever_predicts_a_bigger_file()
    {
        var analysis = Coco();
        var caps = Caps();
        var config = new PluginConfiguration();

        foreach (var strategy in new[]
                 {
                     OptimizationStrategy.Standard,
                     OptimizationStrategy.Medium,
                     OptimizationStrategy.HighReduction
                 })
        {
            var request = StrategyResolver.Resolve(analysis, strategy, caps, config);
            var estimate = EstimateFor(analysis, request);

            Assert.True(
                estimate.EstimatedSizeBytes <= estimate.CurrentSizeBytes,
                $"{strategy} predicted {estimate.EstimatedSizeBytes} from {estimate.CurrentSizeBytes}");
        }
    }

    [Fact]
    public void Reductions_are_ordered_standard_then_medium_then_high()
    {
        var analysis = Coco();
        var caps = Caps();
        var config = new PluginConfiguration();

        long SizeOf(OptimizationStrategy s) =>
            EstimateFor(analysis, StrategyResolver.Resolve(analysis, s, caps, config)).EstimatedSizeBytes;

        var standard = SizeOf(OptimizationStrategy.Standard);
        var medium = SizeOf(OptimizationStrategy.Medium);
        var high = SizeOf(OptimizationStrategy.HighReduction);

        Assert.True(medium < standard, $"medium ({medium}) should beat standard ({standard})");
        Assert.True(high < medium, $"high ({high}) should beat medium ({medium})");
    }

    [Fact]
    public void Medium_steps_the_resolution_down_exactly_one_rung()
    {
        var request = StrategyResolver.Resolve(Coco(), OptimizationStrategy.Medium, Caps(), new PluginConfiguration());
        Assert.Equal(1440, request.TargetHeight);

        var hd = OldH264();
        var hdRequest = StrategyResolver.Resolve(hd, OptimizationStrategy.Medium, Caps(), new PluginConfiguration());
        Assert.Equal(720, hdRequest.TargetHeight);
    }

    [Theory]
    [InlineData(2160, 1440)]
    [InlineData(1440, 1080)]
    [InlineData(1080, 720)]
    [InlineData(720, 480)]
    [InlineData(480, null)]
    public void The_resolution_ladder_walks_one_rung_at_a_time(int from, int? expected) =>
        Assert.Equal(expected, StrategyResolver.OneStepDown(from));

    [Fact]
    public void An_already_efficient_file_is_steered_to_a_resolution_change()
    {
        // Re-encoding HEVC to HEVC at the same size buys nothing, so Standard is the wrong default.
        Assert.Equal(OptimizationStrategy.Medium, StrategyResolver.Recommend(Coco(), Caps()));
    }

    [Fact]
    public void An_old_h264_file_is_steered_to_a_codec_change()
    {
        Assert.Equal(OptimizationStrategy.Standard, StrategyResolver.Recommend(OldH264(), Caps()));
    }

    [Fact]
    public void Re_encoding_h264_to_hevc_predicts_a_real_saving()
    {
        var analysis = OldH264();
        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, Caps(), new PluginConfiguration());
        var estimate = EstimateFor(analysis, request);

        Assert.True(estimate.SavingFraction > 0.25d,
            $"expected a meaningful saving, got {estimate.SavingFraction:P0}");
    }

    [Fact]
    public void A_pointless_re_encode_says_so_instead_of_silently_saving_nothing()
    {
        var analysis = Coco();
        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, Caps(), new PluginConfiguration());
        var estimate = EstimateFor(analysis, request);

        Assert.NotNull(estimate.SavingNote);
        Assert.Contains("resolution", estimate.SavingNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_encode_time_is_offered_until_this_server_has_actually_been_measured()
    {
        var analysis = Coco();
        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Medium, Caps(), new PluginConfiguration());
        var estimate = EstimateFor(analysis, request);

        // The previous build invented "about 11h 40m" from a hard-coded speed table.
        Assert.Null(estimate.EstimatedSeconds);
        Assert.Equal("unmeasured", estimate.TimeBasis);
    }

    [Fact]
    public void Once_a_job_has_run_the_time_estimate_uses_that_measurement()
    {
        var store = new InMemoryJobStore();
        store.Add(new EncodeJob
        {
            Status = JobStatus.Completed,
            PixelsPerSecond = 40_000_000d,
            FinishedAt = DateTime.UtcNow,
            Request = new EncodeRequest { UseHardware = false }
        });

        var analysis = Coco();
        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Medium, Caps(), new PluginConfiguration());
        var estimate = new SizeEstimator(store).Estimate(analysis, request, new PlanResult());

        Assert.NotNull(estimate.EstimatedSeconds);
        Assert.Equal("measured on this server", estimate.TimeBasis);
    }

    [Fact]
    public void A_file_with_image_subtitles_is_not_forced_into_mp4()
    {
        // MP4 cannot carry PGS, so defaulting this file to MP4 would silently drop all 8 tracks.
        var config = new PluginConfiguration { DefaultContainer = "mp4" };
        var request = StrategyResolver.Resolve(Coco(), OptimizationStrategy.Standard, Caps(), config);
        Assert.Equal("mkv", request.Container);
    }

    [Fact]
    public void A_file_without_awkward_subtitles_uses_the_configured_default_container()
    {
        var analysis = Coco();
        analysis.Subtitles = Array.Empty<SubtitleTrackInfo>();
        analysis.AttachmentCount = 0;

        var config = new PluginConfiguration { DefaultContainer = "mp4" };
        var request = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, Caps(), config);
        Assert.Equal("mp4", request.Container);
    }

    [Fact]
    public void High_reduction_trims_audio_but_standard_leaves_it_alone()
    {
        var analysis = Coco();
        var caps = Caps();
        var config = new PluginConfiguration();

        var standard = StrategyResolver.Resolve(analysis, OptimizationStrategy.Standard, caps, config);
        Assert.All(standard.AudioTracks, t => Assert.Equal(AudioAction.Copy, t.Action));

        var high = StrategyResolver.Resolve(analysis, OptimizationStrategy.HighReduction, caps, config);
        Assert.Contains(high.AudioTracks, t => t.Action == AudioAction.Encode);
    }

    /// <summary>A job store that keeps everything in memory, for tests.</summary>
    private sealed class InMemoryJobStore : IJobStore
    {
        private readonly List<EncodeJob> _jobs = new List<EncodeJob>();

        public void Add(EncodeJob job) => _jobs.Add(job);

        public void Update(EncodeJob job)
        {
        }

        public EncodeJob? Get(Guid id) => _jobs.FirstOrDefault(j => j.Id == id);

        public IReadOnlyList<EncodeJob> GetAll() => _jobs;

        public IReadOnlyList<EncodeJob> GetActive() => _jobs.Where(j => j.IsActive).ToList();

        public EncodeJob? TakeNextQueued() => null;

        public bool HasActiveJobForItem(Guid itemId) => false;

        public bool Remove(Guid id) => false;

        public IReadOnlyList<EncodeJob> ReconcileInterrupted() => Array.Empty<EncodeJob>();
    }
}
