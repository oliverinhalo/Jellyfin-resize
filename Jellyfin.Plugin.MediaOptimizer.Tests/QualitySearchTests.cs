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
/// Choosing a quality number by measuring instead of guessing. The search is only worth having if
/// it lands on the *smallest* file that still meets the target and never reports a target it did
/// not actually reach, so those are the two things tested hardest.
/// </summary>
public class QualitySearchTests
{
    /// <summary>A planner that always agrees; the search is not testing the planner.</summary>
    private sealed class Planner : IEncodePlanner
    {
        public List<int?> Planned { get; } = new List<int?>();

        public Task<PlanResult> PlanAsync(
            FileAnalysis analysis,
            EncodeRequest request,
            string outputPath,
            CancellationToken cancellationToken)
        {
            Planned.Add(request.Quality);
            return Task.FromResult(new PlanResult { Arguments = ["-i", "in.mkv", "-y", outputPath] });
        }
    }

    /// <summary>
    /// A stand-in encoder whose score falls as the quality number rises, which is what the number
    /// means. The worst-sample score can be made to disagree with the single sample the search
    /// uses, because that disagreement is the interesting case.
    /// </summary>
    private sealed class Sampler : ISampleEncoder
    {
        private readonly Func<int, double> _score;
        private readonly Func<int, double>? _worst;

        public Sampler(Func<int, double> score, Func<int, double>? worst = null)
        {
            _score = score;
            _worst = worst;
        }

        public List<(int Quality, int MaxSamples)> Calls { get; } = new List<(int, int)>();

        public Task<SampleMeasurement> MeasureAsync(
            FileAnalysis analysis,
            PlanResult plan,
            string workDirectory,
            string? qualityMetric,
            int maxSamples,
            CancellationToken cancellationToken)
        {
            // The plan carries the quality the search asked for, through the arguments the fake
            // planner echoed; simpler here to read it back off the last planned value.
            var quality = LastQuality;
            Calls.Add((quality, maxSamples));

            var average = _score(quality);
            var worst = _worst is null ? average : _worst(quality);

            return Task.FromResult(new SampleMeasurement
            {
                Samples = maxSamples == 0 ? 3 : maxSamples,
                SampledSeconds = maxSamples == 0 ? 24d : 8d * maxSamples,
                BytesPerSecond = 500_000d,
                LowBytesPerSecond = 450_000d,
                HighBytesPerSecond = 550_000d,
                QualityMetric = qualityMetric,
                QualityScore = average,
                WorstQualityScore = worst
            });
        }

        public int LastQuality { get; set; }
    }

    /// <summary>Ties the fake planner and the fake sampler together: the planner sees the quality first.</summary>
    private sealed class RecordingPlanner : IEncodePlanner
    {
        private readonly Sampler _sampler;

        public RecordingPlanner(Sampler sampler) => _sampler = sampler;

        public Task<PlanResult> PlanAsync(
            FileAnalysis analysis,
            EncodeRequest request,
            string outputPath,
            CancellationToken cancellationToken)
        {
            _sampler.LastQuality = request.Quality ?? 0;
            return Task.FromResult(new PlanResult { Arguments = ["-i", "in.mkv", "-y", outputPath] });
        }
    }

    private static FileAnalysis Source() => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "A Film",
        Path = "/media/A Film.mkv",
        Container = "mkv",
        SizeBytes = 20L * 1024 * 1024 * 1024,
        DurationSeconds = 7200d,
        Video = new VideoTrackInfo
        {
            Index = 0,
            Codec = "h264",
            Width = 1920,
            Height = 1080,
            BitDepth = 8,
            FrameRate = 24f,
            Bitrate = new BitrateInfo { Bps = 20_000_000, Source = ValueSource.Measured }
        },
        Audio = [new AudioTrackInfo { Index = 1, TypeIndex = 0, Codec = "ac3", Channels = 6 }]
    };

    private static EncodeRequest Request() => new EncodeRequest
    {
        Container = "mkv",
        Video = VideoAction.Encode,
        VideoCodec = "libx265",
        RateControl = RateControlMode.ConstantQuality,
        Quality = 28
    };

    private static (QualitySearch Search, Sampler Sampler) Build(
        Func<int, double> score,
        Func<int, double>? worst = null)
    {
        var sampler = new Sampler(score, worst);
        var search = new QualitySearch(
            new RecordingPlanner(sampler),
            sampler,
            new SizeEstimator(new StrategyAndEstimateTests.InMemoryJobStore()),
            NullLogger<QualitySearch>.Instance);

        return (search, sampler);
    }

    // --- the thresholds -----------------------------------------------------------------------

    /// <summary>
    /// The picker offers words and the result reports words, and they have to be the same words.
    /// A target whose own threshold described itself as something else would be the plugin
    /// disagreeing with itself in two lines of the same dialog.
    /// </summary>
    [Theory]
    [InlineData(QualityTarget.Indistinguishable, "indistinguishable from the source")]
    [InlineData(QualityTarget.VeryClose, "very hard to tell apart from the source")]
    [InlineData(QualityTarget.SlightlySofter, "slightly softer; visible only side by side")]
    public void A_target_is_described_by_the_same_words_it_is_asked_for(QualityTarget target, string expected)
    {
        foreach (var metric in new[] { QualityProbe.Vmaf, QualityProbe.Ssim })
        {
            var threshold = QualitySearch.ThresholdFor(metric, target);
            Assert.Equal(expected, QualityProbe.Describe(metric, threshold));
        }
    }

    /// <summary>SSIM 0.98 and VMAF 98 are not the same claim, and the targets must not treat them so.</summary>
    [Fact]
    public void The_two_metrics_have_their_own_scales()
    {
        Assert.True(QualitySearch.ThresholdFor(QualityProbe.Vmaf, QualityTarget.VeryClose) > 1d);
        Assert.True(QualitySearch.ThresholdFor(QualityProbe.Ssim, QualityTarget.VeryClose) < 1d);
    }

    // --- the search range ---------------------------------------------------------------------

    /// <summary>
    /// CRF 32 is a reasonable AV1 encode and a badly damaged H.264 one, so the range searched has
    /// to follow the encoder rather than being one set of numbers for everything.
    /// </summary>
    [Theory]
    [InlineData("libx264", 16, 32)]
    [InlineData("h264_nvenc", 16, 32)]
    [InlineData("libx265", 20, 36)]
    [InlineData("hevc_qsv", 20, 36)]
    [InlineData("libsvtav1", 24, 44)]
    [InlineData("av1_nvenc", 24, 44)]
    [InlineData("libvpx-vp9", 24, 42)]
    public void The_range_searched_follows_the_encoders_own_scale(string encoder, int low, int high)
    {
        Assert.Equal((low, high), QualitySearch.RangeFor(encoder));
    }

    // --- what it refuses ----------------------------------------------------------------------

    [Fact]
    public void A_server_that_cannot_measure_quality_is_told_so_rather_than_encoding_for_ten_minutes()
    {
        var refusal = QualitySearch.Refuse(Request(), null);
        Assert.NotNull(refusal);
        Assert.Contains("libvmaf", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void There_is_nothing_to_search_when_the_video_is_being_copied()
    {
        var request = Request();
        request.Video = VideoAction.Copy;

        var refusal = QualitySearch.Refuse(request, QualityProbe.Ssim);
        Assert.NotNull(refusal);
        Assert.Contains("kept exactly as it is", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void There_is_nothing_to_search_at_a_fixed_bitrate()
    {
        var request = Request();
        request.RateControl = RateControlMode.AverageBitrate;

        var refusal = QualitySearch.Refuse(request, QualityProbe.Ssim);
        Assert.NotNull(refusal);
        Assert.Contains("constant quality", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lossless_conversion_has_nothing_to_search_for()
    {
        var request = Request();
        request.RateControl = RateControlMode.Lossless;

        Assert.NotNull(QualitySearch.Refuse(request, QualityProbe.Vmaf));
    }

    [Fact]
    public async Task A_refusal_comes_back_as_a_reason_rather_than_an_answer()
    {
        var (search, sampler) = Build(q => 100d);

        var result = await search.SearchAsync(
            Source(), Request(), QualityTarget.VeryClose, null, "/tmp", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(sampler.Calls);
    }

    // --- the search itself --------------------------------------------------------------------

    /// <summary>
    /// The whole point: the largest quality number — the smallest file — that still meets the
    /// target. Here the score crosses VMAF 93 between 26 and 27, so 26 is the answer and anything
    /// lower is a bigger file than the target asked for.
    /// </summary>
    [Fact]
    public async Task It_finds_the_smallest_file_that_still_meets_the_target()
    {
        // 100 at quality 20, falling 1.5 points per step: 93 is crossed at 24.67, so 24 passes
        // and 25 does not.
        var (search, sampler) = Build(q => 100d - ((q - 20) * 1.5d));

        var result = await search.SearchAsync(
            Source(), Request(), QualityTarget.VeryClose, QualityProbe.Vmaf, "/tmp", CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(24, result.Quality);

        // And it got there by halving rather than walking: seventeen settings, five encodes.
        var searchProbes = sampler.Calls.Count(c => c.MaxSamples == 1);
        Assert.InRange(searchProbes, 4, 6);

        // The answer is then confirmed across the whole file, not on the one sample.
        Assert.Contains(sampler.Calls, c => c.MaxSamples == 0);
        Assert.Equal(24d, result.SecondsConfirmed);
    }

    /// <summary>
    /// The search decides on eight seconds from the middle of the film. If the rest of the film
    /// disagrees, the answer is wrong — so the setting it lands on is confirmed across three
    /// points and the *worst* of them has to meet the target, because an average is exactly how a
    /// bad dark scene hides.
    /// </summary>
    [Fact]
    public async Task A_setting_the_rest_of_the_film_disagrees_with_is_stepped_back()
    {
        // The middle of the film is two points better than the worst of it.
        var (search, sampler) = Build(
            score: q => 100d - ((q - 20) * 1.5d),
            worst: q => 100d - ((q - 20) * 1.5d) - 2d);

        var result = await search.SearchAsync(
            Source(), Request(), QualityTarget.VeryClose, QualityProbe.Vmaf, "/tmp", CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);

        // 24 passed on the single sample and fails on the worst one, so the answer is lower.
        Assert.True(result.Quality < 24, "The search kept a setting the whole file did not support.");
        Assert.True(result.WorstScore >= QualitySearch.ThresholdFor(QualityProbe.Vmaf, QualityTarget.VeryClose));
        Assert.Null(result.Note);
    }

    /// <summary>
    /// A source too damaged to reach the target at any setting gets a sentence saying so, with the
    /// score it did reach. Silently returning the best of a bad set would be the plugin claiming a
    /// quality it measured and rejected.
    /// </summary>
    [Fact]
    public async Task A_target_this_source_cannot_reach_is_said_plainly()
    {
        var (search, _) = Build(q => 70d);

        var result = await search.SearchAsync(
            Source(), Request(), QualityTarget.Indistinguishable, QualityProbe.Vmaf, "/tmp", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("70", result.FailureReason!, StringComparison.Ordinal);
        Assert.True(result.Probes > 0, "It should say what it cost to find that out.");
    }

    /// <summary>The result carries the measured size, because "smaller" is the point of the exercise.</summary>
    [Fact]
    public async Task The_answer_says_what_the_file_would_actually_be()
    {
        var (search, _) = Build(q => 99d);

        var result = await search.SearchAsync(
            Source(), Request(), QualityTarget.VeryClose, QualityProbe.Vmaf, "/tmp", CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(result.EstimatedSizeBytes);
        Assert.True(result.EstimatedSizeBytes > 0);
        Assert.NotNull(result.WorstScoreText);
        Assert.NotNull(result.Verdict);
        Assert.Equal(QualityProbe.Vmaf, result.Metric);
    }

    /// <summary>A score of zero is a score, not a missing measurement.</summary>
    [Fact]
    public async Task Nothing_reaching_the_target_at_all_still_reports_honestly()
    {
        var (search, _) = Build(q => 0d);

        var result = await search.SearchAsync(
            Source(), Request(), QualityTarget.SlightlySofter, QualityProbe.Ssim, "/tmp", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }
}
