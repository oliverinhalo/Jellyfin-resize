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
/// Rules about the shape of the argument vector itself. FFmpeg keeps only the last occurrence of
/// an option, so an argument the planner emits twice is not merely untidy: one of the two is
/// silently discarded, and which one is lost depends on the order they happened to be added in.
/// </summary>
public class ArgumentVectorTests
{
    private sealed class Caps : ICapabilityService
    {
        private readonly Capabilities _caps = new Capabilities
        {
            VideoEncoders =
            [
                new EncoderOption
                {
                    Name = "libx265", Codec = "hevc", DisplayName = "x265",
                    Supports10Bit = true, Presets = ["medium", "slow", "veryslow"]
                },
                new EncoderOption
                {
                    Name = "hevc_nvenc", Codec = "hevc", DisplayName = "HEVC NVENC", IsHardware = true,
                    Supports10Bit = true, Presets = ["p1", "p4", "p7"]
                },
                new EncoderOption { Name = "ffv1", Codec = "ffv1", DisplayName = "FFV1", Supports10Bit = true }
            ],
            AudioEncoders = [new EncoderOption { Name = "flac", Codec = "flac", DisplayName = "FLAC" }],
            AllowHevcEncoding = true,
            AllowAv1Encoding = true
        };

        public Task<Capabilities> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_caps);

        public Task<bool> HasEncoderAsync(string encoder, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private static EncodePlanner Planner() => new EncodePlanner(new Caps(), NullLogger<EncodePlanner>.Instance);

    private static FileAnalysis Source(string videoCodec, bool losslessVideo, string range = "SDR") =>
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
                Width = 3840,
                Height = 2160,
                BitDepth = 10,
                PixelFormat = "yuv420p10le",
                FrameRate = 24f,
                Range = range,
                RangeType = range,
                ColorPrimaries = "bt2020",
                ColorTransfer = "smpte2084",
                ColorSpace = "bt2020nc",
                IsLosslessCodec = losslessVideo,
                Bitrate = new BitrateInfo { Bps = 40_000_000, Source = ValueSource.Measured }
            },
            Audio = []
        };

    private static int Occurrences(IReadOnlyList<string> args, string option) =>
        args.Count(a => string.Equals(a, option, StringComparison.Ordinal));

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A lossless x265 encode of an HDR source asked for lossless=1 and for the HDR10 signalling,
    /// each through its own -x265-params. FFmpeg kept the second and dropped the first, so the
    /// encode was quietly not lossless at all.
    /// </summary>
    [Fact]
    public async Task Lossless_hdr_x265_keeps_both_sets_of_encoder_parameters()
    {
        var analysis = Source("ffv1", losslessVideo: true, range: "HDR10");
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            RateControl = RateControlMode.Lossless
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));
        Assert.Equal(1, Occurrences(plan.Arguments, "-x265-params"));

        var parameters = ValueAfter(plan.Arguments, "-x265-params");
        Assert.Contains("lossless=1", parameters, StringComparison.Ordinal);
        Assert.Contains("hdr10=1", parameters, StringComparison.Ordinal);
        Assert.Contains("repeat-headers=1", parameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hdr_x265_encode_emits_one_parameter_block()
    {
        var analysis = Source("hevc", losslessVideo: false, range: "HDR10");
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            RateControl = RateControlMode.ConstantQuality,
            Quality = 24,
            Preset = "slow"
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.Equal(1, Occurrences(plan.Arguments, "-x265-params"));
        Assert.Equal(1, Occurrences(plan.Arguments, "-preset"));
        Assert.Equal("slow", ValueAfter(plan.Arguments, "-preset"));
    }

    /// <summary>
    /// The hardware path sets its own quality-oriented preset. When the user has chosen one, only
    /// theirs may appear — two -preset arguments meant whichever came last won, which is a coin
    /// toss dressed up as a setting.
    /// </summary>
    [Fact]
    public async Task Hardware_encode_does_not_emit_two_presets()
    {
        var analysis = Source("h264", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "hevc_nvenc",
            RateControl = RateControlMode.ConstantQuality,
            Quality = 28,
            Preset = "p4",
            UseHardware = true
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.Equal(1, Occurrences(plan.Arguments, "-preset"));
        Assert.Equal("p4", ValueAfter(plan.Arguments, "-preset"));
    }

    [Fact]
    public async Task Hardware_encode_without_a_chosen_preset_still_gets_the_quality_one()
    {
        var analysis = Source("h264", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "hevc_nvenc",
            RateControl = RateControlMode.ConstantQuality,
            Quality = 28,
            UseHardware = true
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.Equal(1, Occurrences(plan.Arguments, "-preset"));
        Assert.Equal("p7", ValueAfter(plan.Arguments, "-preset"));
    }

    /// <summary>
    /// A stream whose packets arrive far apart fills the muxer's interleaving buffer and dies with
    /// "Too many packets buffered for output stream" — after however many hours it took to get
    /// there. The headroom costs nothing on a file that does not need it.
    /// </summary>
    [Fact]
    public async Task Every_plan_gives_the_muxer_enough_queue_to_interleave()
    {
        var analysis = Source("h264", losslessVideo: false);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Copy
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);

        Assert.Equal(1, Occurrences(plan.Arguments, "-max_muxing_queue_size"));
    }

    /// <summary>
    /// Nothing in a plan may be specified twice: the second silently wins and the first is a lie
    /// about what will run. This walks the whole vector rather than naming options one at a time,
    /// so an option added later is covered without anyone remembering to add a test.
    /// </summary>
    [Theory]
    [InlineData("libx265", RateControlMode.ConstantQuality, "HDR10")]
    [InlineData("libx265", RateControlMode.Lossless, "HDR10")]
    [InlineData("hevc_nvenc", RateControlMode.ConstantQuality, "SDR")]
    [InlineData("libx265", RateControlMode.ConstantQuality, "SDR")]
    public async Task No_single_valued_option_is_specified_twice(
        string encoder,
        RateControlMode rateControl,
        string range)
    {
        var analysis = Source(rateControl == RateControlMode.Lossless ? "ffv1" : "hevc", rateControl == RateControlMode.Lossless, range);
        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = encoder,
            RateControl = rateControl,
            Quality = rateControl == RateControlMode.ConstantQuality ? 26 : null,
            UseHardware = encoder.EndsWith("nvenc", StringComparison.Ordinal)
        };

        var plan = await Planner().PlanAsync(analysis, request, "/tmp/out.mkv", CancellationToken.None);
        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));

        // -map is legitimately repeated, once per stream; everything else takes one value.
        var repeatable = new HashSet<string>(StringComparer.Ordinal) { "-map" };

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var arg in plan.Arguments)
        {
            if (!arg.StartsWith('-') || arg.Length < 2 || char.IsDigit(arg[1]) || repeatable.Contains(arg))
            {
                continue;
            }

            seen[arg] = seen.GetValueOrDefault(arg) + 1;
        }

        var duplicated = seen.Where(p => p.Value > 1).Select(p => p.Key).ToList();
        Assert.True(
            duplicated.Count == 0,
            "These options are passed more than once, so ffmpeg keeps only the last: "
            + string.Join(", ", duplicated)
            + "\nFull vector: " + string.Join(' ', plan.Arguments));
    }
}
