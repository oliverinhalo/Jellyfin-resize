using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The rules that stop the plugin making a promise it cannot keep, or producing a "smaller"
/// file that is several times bigger.
/// </summary>
public class LosslessPolicyTests
{
    private sealed class Caps : ICapabilityService
    {
        private readonly Capabilities _caps = new Capabilities
        {
            VideoEncoders =
            [
                new EncoderOption { Name = "libx265", Codec = "hevc", DisplayName = "x265", Supports10Bit = true, Presets = ["medium"] },
                new EncoderOption { Name = "ffv1", Codec = "ffv1", DisplayName = "FFV1", Supports10Bit = true },
                new EncoderOption { Name = "hevc_vaapi", Codec = "hevc", DisplayName = "HEVC VAAPI", IsHardware = true, Supports10Bit = true }
            ],
            AudioEncoders =
            [
                new EncoderOption { Name = "flac", Codec = "flac", DisplayName = "FLAC" },
                new EncoderOption { Name = "libopus", Codec = "opus", DisplayName = "Opus" }
            ],
            AllowHevcEncoding = true,
            AllowAv1Encoding = true,
            VaapiDevice = "/dev/dri/renderD128"
        };

        public Task<Capabilities> GetAsync(CancellationToken ct) => Task.FromResult(_caps);

        public Task<bool> HasEncoderAsync(string encoder, CancellationToken ct) => Task.FromResult(true);
    }

    private static EncodePlanner Planner() => new EncodePlanner(new Caps(), NullLogger<EncodePlanner>.Instance);

    private static FileAnalysis Source(string videoCodec, bool losslessVideo, string audioCodec = "aac", bool losslessAudio = false) =>
        new FileAnalysis
        {
            ItemId = Guid.NewGuid(),
            Name = "clip",
            Path = "/media/clip.mkv",
            Container = "mkv",
            SizeBytes = 1_000_000_000,
            DurationSeconds = 3600,
            IsEligible = true,
            IsWritable = true,
            Video = new VideoTrackInfo
            {
                Index = 0,
                Codec = videoCodec,
                Width = 1920,
                Height = 1080,
                BitDepth = 8,
                FrameRate = 24f,
                Range = "SDR",
                IsLosslessCodec = losslessVideo,
                Bitrate = new BitrateInfo { Bps = 2_000_000, Source = ValueSource.Measured }
            },
            Audio =
            [
                new AudioTrackInfo
                {
                    Index = 1, TypeIndex = 0, Codec = audioCodec, Channels = 6,
                    IsLossless = losslessAudio,
                    Bitrate = new BitrateInfo { Bps = 640_000, Source = ValueSource.Measured }
                }
            ]
        };

    [Fact]
    public async Task Lossless_video_encode_is_refused_from_a_lossy_source()
    {
        // The single most common misconception the plugin has to defend against.
        var analysis = Source("hevc", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            RateControl = RateControlMode.Lossless,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Warnings, w => w.Code == "LOSSLESS_FROM_LOSSY" && w.Level == WarningLevel.Blocker);
    }

    [Fact]
    public async Task Lossless_video_encode_is_allowed_from_a_lossless_source()
    {
        var analysis = Source("ffv1", losslessVideo: true);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.LosslessOnly,
            Video = VideoAction.Encode,
            VideoCodec = "ffv1",
            RateControl = RateControlMode.Lossless,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));
        Assert.True(plan.IsLossless, "FFV1 to FFV1 with copied audio is bit-exact.");
    }

    [Fact]
    public async Task Lossless_mode_refuses_to_rescale()
    {
        var analysis = Source("ffv1", losslessVideo: true);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Video = VideoAction.Encode,
            VideoCodec = "ffv1",
            RateControl = RateControlMode.Lossless,
            TargetHeight = 720,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Warnings, w => w.Code == "LOSSLESS_RESCALE");
    }

    [Fact]
    public async Task Lossless_strategy_blocks_a_lossy_audio_re_encode()
    {
        var analysis = Source("hevc", losslessVideo: false, audioCodec: "truehd", losslessAudio: true);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.LosslessOnly,
            Video = VideoAction.Copy,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Encode, Codec = "libopus", BitrateBps = 192000 }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Warnings, w => w.Code == "NOT_LOSSLESS");
    }

    [Fact]
    public async Task Copying_video_and_flac_encoding_lossless_audio_counts_as_lossless()
    {
        var analysis = Source("hevc", losslessVideo: false, audioCodec: "truehd", losslessAudio: true);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.LosslessOnly,
            Video = VideoAction.Copy,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Encode, Codec = "flac" }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));
        Assert.True(plan.IsLossless);
        Assert.Contains(plan.LosslessAudioChecks, c => c.SourceStreamIndex == 1 && c.OutputAudioIndex == 0);
    }

    [Fact]
    public async Task Dolby_vision_blocks_a_video_re_encode_until_it_is_explicitly_accepted()
    {
        var analysis = Source("hevc", losslessVideo: false);
        analysis.Video!.IsDolbyVision = true;
        analysis.Video.Range = "Dolby Vision";
        analysis.Video.RangeType = "DOVI";

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            Quality = 24,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var blocked = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);
        Assert.False(blocked.IsRunnable);
        Assert.Contains(blocked.Warnings, w => w.Code == "DOLBY_VISION_LOSS" && w.Level == WarningLevel.Blocker);

        request.AcceptDolbyVisionLoss = true;
        var accepted = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);
        Assert.True(accepted.IsRunnable);
        Assert.Contains(accepted.Warnings, w => w.Code == "DOLBY_VISION_ACCEPTED" && w.Level == WarningLevel.Warning);
    }

    [Fact]
    public async Task Vaapi_encoding_sets_a_device_and_uploads_frames_to_the_gpu()
    {
        var analysis = Source("h264", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Video = VideoAction.Encode,
            VideoCodec = "hevc_vaapi",
            TargetHeight = 720,
            Quality = 25,
            UseHardware = true,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);
        var args = plan.Arguments.ToList();

        // -vaapi_device has to precede the input, and the filter chain has to end on the GPU.
        Assert.Equal("-vaapi_device", args[0]);
        Assert.Equal("/dev/dri/renderD128", args[1]);
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.StartsWith("scale=", vf, StringComparison.Ordinal);
        Assert.EndsWith("format=nv12,hwupload", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_size_leaves_room_for_the_audio()
    {
        var analysis = Source("hevc", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            RateControl = RateControlMode.TargetSize,
            TargetSizeBytes = 2L * 1024 * 1024 * 1024,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var bitrate = EncodePlanner.ComputeTargetVideoBitrate(analysis, request);
        Assert.NotNull(bitrate);

        // Total budget minus the 640 kb/s audio track.
        var totalBudget = request.TargetSizeBytes.Value * 8d / analysis.DurationSeconds!.Value;
        Assert.True(bitrate!.Value < totalBudget - 600_000);
    }

    [Fact]
    public void Target_size_smaller_than_the_audio_is_rejected()
    {
        var analysis = Source("hevc", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            RateControl = RateControlMode.TargetSize,
            TargetSizeBytes = 1024 * 1024,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        Assert.Null(EncodePlanner.ComputeTargetVideoBitrate(analysis, request));
    }

    [Fact]
    public async Task Mp4_output_warns_that_pgs_subtitles_and_attachments_cannot_come_along()
    {
        var analysis = Source("hevc", losslessVideo: false);
        analysis.AttachmentCount = 3;
        analysis.Subtitles = [new SubtitleTrackInfo { Index = 2, Codec = "hdmv_pgs_subtitle", IsGraphical = true }];

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mp4",
            Video = VideoAction.Copy,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mp4", CancellationToken.None);

        Assert.Contains(plan.Warnings, w => w.Code == "SUBTITLE_INCOMPATIBLE");
        Assert.Contains(plan.Warnings, w => w.Code == "ATTACHMENTS_LOST");
    }
}
