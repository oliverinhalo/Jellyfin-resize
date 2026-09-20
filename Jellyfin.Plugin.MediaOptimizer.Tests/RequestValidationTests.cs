using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The request arrives as an HTTP body, so it is whatever the caller sent. A nonsensical number
/// has to come back as a sentence naming the setting, not as an ffmpeg usage error — or worse, as
/// a job that runs for hours and then fails.
/// </summary>
public class RequestValidationTests
{
    private sealed class Caps : ICapabilityService
    {
        private readonly Capabilities _caps = new Capabilities
        {
            VideoEncoders =
            [
                new EncoderOption { Name = "libx265", Codec = "hevc", DisplayName = "x265", Supports10Bit = true, Presets = ["medium"] }
            ],
            AudioEncoders = [new EncoderOption { Name = "libopus", Codec = "opus", DisplayName = "Opus" }],
            AllowHevcEncoding = true,
            AllowAv1Encoding = true
        };

        public Task<Capabilities> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_caps);

        public Task<bool> HasEncoderAsync(string encoder, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static FileAnalysis Source() => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "clip",
        Path = "/media/clip.mkv",
        Container = "mkv",
        SizeBytes = 4_000_000_000,
        DurationSeconds = 3600,
        IsEligible = true,
        IsWritable = true,
        Video = new VideoTrackInfo
        {
            Index = 0, Codec = "h264", Width = 1920, Height = 1080, BitDepth = 8,
            FrameRate = 24f, Range = "SDR",
            Bitrate = new BitrateInfo { Bps = 8_000_000, Source = ValueSource.Measured }
        },
        Audio =
        [
            new AudioTrackInfo { Index = 1, TypeIndex = 0, Codec = "ac3", Channels = 6, SampleRate = 48000 }
        ]
    };

    private static async Task<PlanResult> PlanAsync(Action<EncodeRequest> configure)
    {
        var analysis = Source();
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            RateControl = RateControlMode.ConstantQuality,
            Quality = 28,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        configure(request);

        var planner = new EncodePlanner(new Caps(), NullLogger<EncodePlanner>.Instance);
        return await planner.PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);
    }

    private static void AssertBlocked(PlanResult plan, string code)
    {
        Assert.False(plan.IsRunnable, "Expected " + code + " to block the plan.");
        Assert.Contains(plan.Warnings, w => w.Code == code && w.Level == WarningLevel.Blocker);
        Assert.Empty(plan.Arguments);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(500)]
    public async Task A_quality_no_encoder_accepts_is_refused(int quality)
    {
        AssertBlocked(await PlanAsync(r => r.Quality = quality), "QUALITY_RANGE");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1080)]
    [InlineData(100000)]
    public async Task A_height_that_is_not_a_resolution_is_refused(int height)
    {
        AssertBlocked(await PlanAsync(r => r.TargetHeight = height), "HEIGHT_RANGE");
    }

    [Fact]
    public async Task A_bit_depth_that_does_not_exist_is_refused()
    {
        AssertBlocked(await PlanAsync(r => r.BitDepth = 9), "BIT_DEPTH_RANGE");
    }

    [Fact]
    public async Task An_unwatchable_video_bitrate_is_refused()
    {
        AssertBlocked(
            await PlanAsync(r =>
            {
                r.RateControl = RateControlMode.AverageBitrate;
                r.VideoBitrateBps = 4_000;
            }),
            "BITRATE_RANGE");
    }

    [Fact]
    public async Task An_impossible_audio_bitrate_is_refused()
    {
        AssertBlocked(
            await PlanAsync(r => r.AudioTracks =
            [
                new AudioTrackRequest { Index = 1, Action = AudioAction.Encode, Codec = "libopus", BitrateBps = 900_000_000 }
            ]),
            "AUDIO_BITRATE_RANGE");
    }

    [Fact]
    public async Task An_impossible_channel_count_is_refused()
    {
        AssertBlocked(
            await PlanAsync(r => r.AudioTracks =
            [
                new AudioTrackRequest { Index = 1, Action = AudioAction.Encode, Codec = "libopus", Channels = 99 }
            ]),
            "AUDIO_CHANNEL_RANGE");
    }

    /// <summary>The ordinary case has to survive the new gate untouched.</summary>
    [Fact]
    public async Task Reasonable_settings_still_plan()
    {
        var plan = await PlanAsync(r =>
        {
            r.Quality = 26;
            r.TargetHeight = 720;
            r.BitDepth = 10;
        });

        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));
        Assert.Contains("scale=-2:720", string.Join(' ', plan.Arguments), StringComparison.Ordinal);
    }

    /// <summary>
    /// The blocker has to say which setting is wrong. A message that only says "invalid request"
    /// leaves the user with a form full of numbers and no idea which one to change.
    /// </summary>
    [Fact]
    public async Task The_message_names_the_offending_value()
    {
        var plan = await PlanAsync(r => r.Quality = 500);
        var blocker = plan.Warnings.First(w => w.Level == WarningLevel.Blocker);

        Assert.Contains("Quality", blocker.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("500", blocker.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv420p", 8)]
    [InlineData("yuv444p12le", 12)]
    [InlineData("p010le", 10)]
    [InlineData("nv12", 8)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Bit_depth_is_read_out_of_the_pixel_format(string? pixelFormat, int? expected)
    {
        Assert.Equal(expected, MediaProbeService.BitDepthFromPixelFormat(pixelFormat));
    }

    /// <summary>
    /// H.264 and HEVC usually omit bits_per_raw_sample, so a 10-bit file read through the ffprobe
    /// fallback looked like unknown depth — and unknown became 8-bit, throwing away a bit of
    /// picture the user never agreed to lose.
    /// </summary>
    [Fact]
    public void A_ten_bit_source_with_no_explicit_depth_is_still_ten_bit()
    {
        var json = System.Text.Json.JsonDocument.Parse("""
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,
                     "pix_fmt":"yuv420p10le","avg_frame_rate":"24/1"}]}
        """);

        var analysis = new FileAnalysis { ItemId = Guid.NewGuid(), Name = "clip", Path = "/media/clip.mkv" };
        MediaProbeService.BuildTracksFromFfprobe(analysis, json.RootElement.GetProperty("streams"));

        Assert.NotNull(analysis.Video);
        Assert.Equal(10, analysis.Video!.BitDepth);
        Assert.Equal("yuv420p10le", analysis.Video.PixelFormat);
    }

    /// <summary>The pixel format chosen for the encode has to keep the depth it was given.</summary>
    [Theory]
    [InlineData(10, "yuv420p", "yuv420p10le")]
    [InlineData(8, "yuv420p10le", "yuv420p")]
    [InlineData(10, "yuv444p10le", "yuv444p10le")]
    public void The_encode_pixel_format_follows_the_chosen_depth(int depth, string source, string expected)
    {
        Assert.Equal(expected, EncodePlanner.PixelFormatFor(depth, source));
    }
}
