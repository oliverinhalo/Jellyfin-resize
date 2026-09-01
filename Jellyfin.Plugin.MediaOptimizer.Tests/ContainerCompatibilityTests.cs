using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Regression cover for the failure that killed two 20 GiB Blu-ray remuxes: the plan mapped the
/// source subtitle tracks into MP4 with <c>-c:s copy</c>. MP4 has no box for SubRip, so FFmpeg
/// refused to write the header, the video filter died with <c>Invalid argument</c>, and the job
/// failed having encoded zero frames after the user waited for it.
/// </summary>
public class ContainerCompatibilityTests
{
    private sealed class Caps : ICapabilityService
    {
        private readonly Capabilities _caps = new Capabilities
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

        public Task<Capabilities> GetAsync(CancellationToken ct) => Task.FromResult(_caps);

        public Task<bool> HasEncoderAsync(string encoder, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>A Blu-ray remux: lossless audio, text subtitles, no bitmap subtitles.</summary>
    private static FileAnalysis Remux(string audioCodec = "truehd", string subtitleCodec = "subrip") =>
        new FileAnalysis
        {
            ItemId = Guid.NewGuid(),
            Name = "Tetris",
            Path = "/media/Tetris.mkv",
            Container = "mkv",
            SizeBytes = 22_236_073_984L,
            DurationSeconds = 6600,
            IsEligible = true,
            IsWritable = true,
            OverallBitrate = new BitrateInfo { Bps = 26_950_000, Source = ValueSource.Measured },
            Video = new VideoTrackInfo
            {
                Index = 0, Codec = "h264", Profile = "High", Width = 1920, Height = 1080,
                BitDepth = 8, FrameRate = 23.976f, Range = "SDR", PixelFormat = "yuv420p",
                Bitrate = new BitrateInfo { Bps = 24_000_000, Source = ValueSource.Measured }
            },
            Audio =
            [
                new AudioTrackInfo
                {
                    Index = 1, TypeIndex = 0, Codec = audioCodec, Channels = 6, ChannelLayout = "5.1",
                    SampleRate = 48000, IsLossless = true,
                    Bitrate = new BitrateInfo { Bps = 3_000_000, Source = ValueSource.Measured }
                }
            ],
            Subtitles =
            [
                new SubtitleTrackInfo
                {
                    Index = 2, Codec = subtitleCodec, Language = "eng",
                    IsGraphical = ContainerCompatibility.IsGraphicalSubtitle(subtitleCodec)
                }
            ]
        };

    private static async Task<List<string>> ArgsFor(FileAnalysis analysis, EncodeRequest request)
    {
        var plan = await new EncodePlanner(new Caps(), NullLogger<EncodePlanner>.Instance)
            .PlanAsync(analysis, request, "/media/out.tmp", CancellationToken.None);

        Assert.DoesNotContain(plan.Warnings, w => w.Level == WarningLevel.Blocker);
        return plan.Arguments.ToList();
    }

    /// <summary>The exact argument pair that made FFmpeg fail: a text subtitle copied into MP4.</summary>
    [Fact]
    public async Task Text_subtitles_are_converted_not_copied_into_mp4()
    {
        var analysis = Remux(audioCodec: "aac");
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mp4",
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            Quality = 24,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var args = await ArgsFor(analysis, request);

        var subCodecIndex = args.IndexOf("-c:s:0");
        Assert.True(subCodecIndex >= 0, "the subtitle track should still be carried: " + string.Join(' ', args));
        Assert.Equal("mov_text", args[subCodecIndex + 1]);
        Assert.DoesNotContain("-c:s", args);
    }

    [Fact]
    public async Task Text_subtitles_are_copied_untouched_into_mkv()
    {
        var analysis = Remux(audioCodec: "aac");
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            Quality = 24,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var args = await ArgsFor(analysis, request);

        var subCodecIndex = args.IndexOf("-c:s:0");
        Assert.True(subCodecIndex >= 0);
        Assert.Equal("copy", args[subCodecIndex + 1]);
    }

    [Fact]
    public async Task Copying_truehd_into_mp4_is_blocked_rather_than_attempted()
    {
        var analysis = Remux();
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mp4",
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            Quality = 24,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await new EncodePlanner(new Caps(), NullLogger<EncodePlanner>.Instance)
            .PlanAsync(analysis, request, "/media/out.tmp", CancellationToken.None);

        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Blocker && w.Code == "AUDIO_CONTAINER_INCOMPATIBLE");
    }

    /// <summary>
    /// The presets are what actually ran on the two failed files, so they are what must not be
    /// able to produce an unwritable combination.
    /// </summary>
    [Theory]
    [InlineData(OptimizationStrategy.Standard)]
    [InlineData(OptimizationStrategy.Medium)]
    [InlineData(OptimizationStrategy.HighReduction)]
    public async Task No_preset_on_a_remux_produces_a_plan_ffmpeg_would_reject(OptimizationStrategy strategy)
    {
        var analysis = Remux();
        var caps = await new Caps().GetAsync(CancellationToken.None);
        var config = new PluginConfiguration { DefaultContainer = "mp4" };

        var request = StrategyResolver.Resolve(analysis, strategy, caps, config);
        var args = await ArgsFor(analysis, request);

        // Whatever container it settled on, every kept stream must be one that container can hold.
        foreach (var track in analysis.Audio)
        {
            var req = request.AudioTracks.First(a => a.Index == track.Index);
            var outgoing = req.Action == AudioAction.Copy ? track.Codec : req.Codec;
            Assert.True(
                ContainerCompatibility.CanCopyAudio(request.Container, outgoing),
                $"{strategy}: {outgoing} cannot go into {request.Container}");
        }

        Assert.DoesNotContain("-c:s", args);
    }

    [Fact]
    public void A_remux_with_lossless_audio_defaults_to_mkv_not_mp4()
    {
        var analysis = Remux();
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mp4",
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        StrategyResolver.ApplyContainerCompatibility(analysis, request);

        Assert.Equal("mkv", request.Container);

        // The change must be explained, not silent: the user set MP4 as their default.
        Assert.NotNull(request.ContainerSwitchReason);
        Assert.Contains("TRUEHD", request.ContainerSwitchReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Mp4_survives_when_the_incompatible_track_is_being_re_encoded()
    {
        var analysis = Remux();
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mp4",
            AudioTracks =
            [
                new AudioTrackRequest { Index = 1, Action = AudioAction.Encode, Codec = "libopus" }
            ]
        };

        StrategyResolver.ApplyContainerCompatibility(analysis, request);

        Assert.Equal("mp4", request.Container);
        Assert.Null(request.ContainerSwitchReason);
    }

    [Theory]
    [InlineData("mp4", "truehd", false)]
    [InlineData("mp4", "mlp", false)]
    [InlineData("mp4", "pcm_bluray", false)]
    [InlineData("mp4", "dts", true)]
    [InlineData("mp4", "flac", true)]
    [InlineData("mp4", "eac3", true)]
    [InlineData("mkv", "truehd", true)]
    [InlineData("mkv", "pcm_bluray", true)]
    public void Audio_copy_rules_match_what_the_muxers_accept(string container, string codec, bool expected) =>
        Assert.Equal(expected, ContainerCompatibility.CanCopyAudio(container, codec));

    [Theory]
    [InlineData("mp4", "subrip", "mov_text")]
    [InlineData("mp4", "ass", "mov_text")]
    [InlineData("mp4", "mov_text", "copy")]
    [InlineData("mp4", "hdmv_pgs_subtitle", null)]
    [InlineData("mp4", "dvd_subtitle", null)]
    [InlineData("mkv", "subrip", "copy")]
    [InlineData("mkv", "hdmv_pgs_subtitle", "copy")]
    [InlineData("mkv", "mov_text", "srt")]
    public void Subtitle_codec_rules_match_what_the_muxers_accept(string container, string codec, string? expected) =>
        Assert.Equal(expected, ContainerCompatibility.SubtitleCodecFor(container, codec));

    [Theory]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("PGSSUB")]
    [InlineData("dvd_subtitle")]
    [InlineData("dvbsub")]
    [InlineData("xsub")]
    public void Every_bitmap_subtitle_spelling_is_recognised(string codec) =>
        Assert.True(ContainerCompatibility.IsGraphicalSubtitle(codec));

    /// <summary>
    /// The message the user sees when a dry run stops a job has to name the real problem. This is
    /// the exact stderr the two failed 20 GiB jobs produced.
    /// </summary>
    [Fact]
    public void The_failure_summary_names_the_offending_codec_not_the_encoder_banner()
    {
        const string Stderr = """
            x265 [info]: HEVC encoder version 3.5
            x265 [info]: build info [Linux][GCC 12.2.0][64 bit] 8bit+10bit+12bit
            [mp4 @ 0x55d1] Could not find tag for codec subrip in stream #2, codec not currently supported in container
            [out#0/mp4 @ 0x55d1] Could not write header (incorrect codec parameters ?): Invalid argument
            [vf#0:0 @ 0x55d2] Error sending frames to consumers: Invalid argument
            frame=    0 fps=0.0 q=0.0 Lsize=       0KiB time=N/A bitrate=N/A speed=N/A
            """;

        var summary = JobQueueService.Summarise(Stderr);

        Assert.Contains("subrip", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("x265", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0x55d1", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_summary_copes_with_nothing_useful_in_the_output()
    {
        Assert.Equal("FFmpeg gave no reason.", JobQueueService.Summarise(null));
        Assert.Equal("FFmpeg gave no reason.", JobQueueService.Summarise("   "));
    }

    [Theory]
    [InlineData("subrip")]
    [InlineData("ass")]
    [InlineData("mov_text")]
    [InlineData(null)]
    public void Text_subtitles_are_not_mistaken_for_bitmaps(string? codec) =>
        Assert.False(ContainerCompatibility.IsGraphicalSubtitle(codec));
}
