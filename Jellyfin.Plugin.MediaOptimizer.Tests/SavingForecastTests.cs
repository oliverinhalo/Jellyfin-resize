using System;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The library worklist: which files are worth starting with. Ordering by size alone is worse than
/// useless, because the biggest file in most libraries is a remux that is already efficiently
/// encoded and has almost nothing to give up.
/// </summary>
public class SavingForecastTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void An_old_h264_file_is_worth_re_encoding_at_the_same_resolution()
    {
        var forecast = SavingForecast.For(10 * Gib, 1080, "h264");

        Assert.NotNull(forecast.SavingBytes);
        Assert.Equal(OptimizationStrategy.Standard, forecast.Strategy);
        Assert.Contains("HEVC instead of H.264", forecast.Basis!, StringComparison.Ordinal);

        // HEVC needs roughly 62% of H.264's bitrate, so a little under 40% of the file.
        Assert.InRange(forecast.SavingBytes!.Value, (long)(10 * Gib * 0.3), (long)(10 * Gib * 0.45));
    }

    /// <summary>
    /// An MPEG-2 DVD rip is the extreme case, and the one most worth surfacing: the codec is two
    /// decades old and the saving is enormous.
    /// </summary>
    [Fact]
    public void An_mpeg2_file_shows_the_largest_gain()
    {
        var mpeg2 = SavingForecast.For(8 * Gib, 576, "mpeg2video");
        var h264 = SavingForecast.For(8 * Gib, 576, "h264");

        Assert.NotNull(mpeg2.SavingBytes);
        Assert.NotNull(h264.SavingBytes);
        Assert.True(mpeg2.SavingBytes > h264.SavingBytes, "MPEG-2 has far more to gain than H.264.");
        Assert.Contains("MPEG-2", mpeg2.Basis!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 4K HEVC remux cannot be shrunk by re-encoding it to HEVC again. The only lever left is
    /// resolution, and the forecast has to say so rather than promising a codec saving.
    /// </summary>
    [Fact]
    public void An_efficient_4k_file_is_only_worth_it_for_the_resolution()
    {
        var forecast = SavingForecast.For(60 * Gib, 2160, "hevc");

        Assert.NotNull(forecast.SavingBytes);
        Assert.Equal(OptimizationStrategy.Medium, forecast.Strategy);
        Assert.Contains("1440p instead of 2160p", forecast.Basis!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An AV1 file at the bottom of the resolution ladder has nothing left to give. Putting it on
    /// a worklist would be inviting someone to waste an evening on it.
    /// </summary>
    [Fact]
    public void A_file_with_nothing_to_gain_is_left_off_the_worklist()
    {
        Assert.Null(SavingForecast.For(2 * Gib, 480, "av1").SavingBytes);
        Assert.Null(SavingForecast.For(2 * Gib, 480, "hevc").SavingBytes);
    }

    [Fact]
    public void A_file_of_unknown_size_forecasts_nothing()
    {
        Assert.Null(SavingForecast.For(null, 2160, "h264").SavingBytes);
        Assert.Null(SavingForecast.For(0, 2160, "h264").SavingBytes);
    }

    [Fact]
    public void An_unknown_codec_is_treated_as_h264_rather_than_guessed_at()
    {
        // EfficiencyOf treats anything it does not recognise as H.264, so an unknown codec cannot
        // be claimed to have a saving that a codec change would not produce.
        var unknown = SavingForecast.For(10 * Gib, 1080, "somethingnew");

        Assert.NotNull(unknown.SavingBytes);
        Assert.Equal(SavingForecast.For(10 * Gib, 1080, "h264").SavingBytes, unknown.SavingBytes);
    }

    /// <summary>
    /// The worklist and the dialog must agree about what a resolution change buys, or the page
    /// that sent you to a file contradicts the page you land on.
    /// </summary>
    [Fact]
    public void The_resolution_model_matches_the_one_the_estimate_uses()
    {
        var analysis = new FileAnalysis
        {
            ItemId = Guid.NewGuid(),
            SizeBytes = 60 * Gib,
            DurationSeconds = 7200,
            IsEligible = true,
            Video = new VideoTrackInfo
            {
                Index = 0, Codec = "hevc", Width = 3840, Height = 2160, BitDepth = 10,
                FrameRate = 24f, Range = "SDR",
                Bitrate = new BitrateInfo { Bps = (long)(60 * Gib * 8d / 7200d), Source = ValueSource.Measured }
            }
        };

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Video = VideoAction.Encode,
            VideoCodec = "libx265",
            RateControl = RateControlMode.ConstantQuality,
            TargetHeight = 1440
        };

        var perFile = SizeEstimator.EstimateVideoBitrate(analysis, request) * analysis.DurationSeconds!.Value / 8d;
        var worklist = 60 * Gib - SavingForecast.For(60 * Gib, 2160, "hevc").SavingBytes!.Value;

        // Within 10%: the worklist works from the whole file and the estimate from the video
        // stream, but they must not disagree about the shape of the answer.
        Assert.InRange(perFile, worklist * 0.9d, worklist * 1.1d);
    }
}
