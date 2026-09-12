using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// "How much worse will it look?" has always been answered with a preset name. These check the
/// machinery that answers it with a number instead — and, with a real ffmpeg, that the number
/// actually moves in the right direction when the encode gets worse.
/// </summary>
public class QualityProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;

    public QualityProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ffmpeg = Which("ffmpeg");
        _ffprobe = Which("ffprobe");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Scratch space.
        }

        GC.SuppressFinalize(this);
    }

    private bool HasFfmpeg => _ffmpeg is not null && _ffprobe is not null;

    private FfmpegRunner Runner => new FfmpegRunner(_ffmpeg!, _ffprobe!, NullLogger<FfmpegRunner>.Instance);

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("VMAF", "[Parsed_libvmaf_0 @ 0x7f85c0] VMAF score: 96.402831", 96.402831)]
    [InlineData("VMAF", "VMAF score: 53", 53d)]
    [InlineData("SSIM", "[Parsed_ssim_0 @ 0x55] SSIM Y:0.9786 (16.7) U:0.99 (20) V:0.99 (21) All:0.982531 (17.6)", 0.982531)]
    public void The_score_is_read_out_of_ffmpegs_own_report(string metric, string output, double expected)
    {
        Assert.Equal(expected, QualityProbe.ParseScore(metric, output));
    }

    [Theory]
    [InlineData("VMAF", "frame=  100 fps=25 q=-0.0 size=N/A")]
    [InlineData("SSIM", "Error opening filters")]
    [InlineData("VMAF", "")]
    [InlineData("VMAF", null)]
    public void A_report_with_no_score_in_it_produces_none(string metric, string? output)
    {
        Assert.Null(QualityProbe.ParseScore(metric, output));
    }

    /// <summary>
    /// The two metrics are on different scales, and a plain "98" would invite somebody to read an
    /// SSIM of 0.98 as excellent when it is not the same claim at all.
    /// </summary>
    [Fact]
    public void The_verdict_respects_the_scale_of_the_metric_that_produced_it()
    {
        // 98 is an excellent VMAF score. 0.98 is a decent SSIM, not an excellent one — and a
        // VMAF of 0.98 would be catastrophic. The same digits mean three different things.
        Assert.Equal("indistinguishable from the source", QualityProbe.Describe("VMAF", 98d));
        Assert.Equal("very hard to tell apart from the source", QualityProbe.Describe("SSIM", 0.98d));
        Assert.Equal("clearly degraded", QualityProbe.Describe("VMAF", 0.98d));
        Assert.Equal("indistinguishable from the source", QualityProbe.Describe("SSIM", 0.995d));
    }

    [Theory]
    [InlineData("VMAF", 99d, "indistinguishable")]
    [InlineData("VMAF", 94d, "very hard to tell apart")]
    [InlineData("VMAF", 90d, "slightly softer")]
    [InlineData("VMAF", 84d, "noticeably softer")]
    [InlineData("VMAF", 75d, "visibly worse")]
    [InlineData("VMAF", 40d, "clearly degraded")]
    public void Every_band_has_words_for_it(string metric, double score, string expected)
    {
        Assert.Contains(expected, QualityProbe.Describe(metric, score), StringComparison.Ordinal);
    }

    /// <summary>
    /// SSIM runs 0-1, where everything interesting happens in the third and fourth decimal.
    /// Formatting it the way VMAF is formatted turns 0.9825 and 0.9950 — two different verdicts —
    /// into the same "1.0", and most builds of jellyfin-ffmpeg have no VMAF, so that is the
    /// common case rather than the exotic one.
    /// </summary>
    [Fact]
    public void A_score_is_formatted_on_its_own_scale()
    {
        Assert.Equal("96.4", QualityProbe.FormatScore("VMAF", 96.402831d));
        Assert.Equal("0.9825", QualityProbe.FormatScore("SSIM", 0.982531d));

        // The two SSIM scores below are different verdicts, and must not print the same.
        Assert.NotEqual(
            QualityProbe.FormatScore("SSIM", 0.9825d),
            QualityProbe.FormatScore("SSIM", 0.9950d));

        Assert.NotEqual(
            QualityProbe.Describe("SSIM", 0.9825d),
            QualityProbe.Describe("SSIM", 0.9950d));
    }

    /// <summary>
    /// The real test: a heavily compressed encode must score worse than a light one, and a
    /// downscale-and-back must score worse still. A metric that does not move with the thing it
    /// measures is a number with no meaning, which is worse than no number at all.
    /// </summary>
    [SkippableFact]
    public async Task A_worse_encode_actually_scores_worse()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = Path.Combine(_dir, "source.mkv");
        var good = Path.Combine(_dir, "good.mkv");
        var bad = Path.Combine(_dir, "bad.mkv");

        var build = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=3",
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", source],
            CancellationToken.None);
        Assert.True(build.Success, build.StandardError);

        foreach (var (path, crf) in new[] { (good, "20"), (bad, "48") })
        {
            var encode = await Runner.RunAsync(
                _ffmpeg!,
                ["-nostdin", "-v", "error", "-y", "-i", source, "-c:v", "libx264", "-preset", "veryfast",
                 "-crf", crf, "-pix_fmt", "yuv420p", path],
                CancellationToken.None);
            Assert.True(encode.Success, encode.StandardError);
        }

        var probe = new QualityProbe(Runner, NullLogger<QualityProbe>.Instance);
        var metric = await DetectMetricAsync();

        var goodScore = await probe.CompareAsync(source, 0d, good, 0d, 3d, 320, 240, metric, CancellationToken.None);
        var badScore = await probe.CompareAsync(source, 0d, bad, 0d, 3d, 320, 240, metric, CancellationToken.None);

        Assert.True(goodScore.Succeeded, goodScore.FailureReason);
        Assert.True(badScore.Succeeded, badScore.FailureReason);
        Assert.True(
            goodScore.Score > badScore.Score,
            FormattableString.Invariant($"A CRF 20 encode scored {goodScore.Score} and a CRF 48 encode {badScore.Score}."));
    }

    /// <summary>
    /// A conversion that changes the resolution still has to be comparable: the viewer watches the
    /// smaller file on the same screen, so that is the comparison to make.
    /// </summary>
    [SkippableFact]
    public async Task A_downscaled_encode_can_still_be_compared_with_its_source()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = Path.Combine(_dir, "source.mkv");
        var small = Path.Combine(_dir, "small.mkv");

        var build = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x480:rate=25:duration=3",
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", source],
            CancellationToken.None);
        Assert.True(build.Success, build.StandardError);

        var encode = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-i", source, "-vf", "scale=-2:240", "-c:v", "libx264",
             "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p", small],
            CancellationToken.None);
        Assert.True(encode.Success, encode.StandardError);

        var probe = new QualityProbe(Runner, NullLogger<QualityProbe>.Instance);
        // The encode is 240 lines tall and the source 480: comparing them means scaling the
        // encode back up, which is what the viewer's screen does anyway.
        var measurement = await probe.CompareAsync(
            source, 0d, small, 0d, 3d, 640, 480, await DetectMetricAsync(), CancellationToken.None);

        Assert.True(measurement.Succeeded, measurement.FailureReason);
        Assert.True(measurement.Score > 0d);
    }

    /// <summary>
    /// The comparison has to work every time, not most times. The first version of this filter
    /// graph used scale2ref and no "shortest", and on ffmpeg 7 that deadlocked outright often
    /// enough to show up in fifteen runs — hanging the process rather than failing, which in the
    /// plugin would have been a stuck queue. Repetition is the only way to see that.
    /// </summary>
    [SkippableFact]
    public async Task The_comparison_is_not_merely_usually_reliable()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = Path.Combine(_dir, "repeat-source.mkv");
        var encoded = Path.Combine(_dir, "repeat-encoded.mkv");

        var build = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2",
             "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", source],
            CancellationToken.None);
        Assert.True(build.Success, build.StandardError);

        // Deliberately a different length from the reference window, which is the case that hung.
        var encode = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-i", source, "-t", "1.5", "-vf", "scale=-2:120",
             "-c:v", "libx264", "-preset", "ultrafast", "-crf", "34", encoded],
            CancellationToken.None);
        Assert.True(encode.Success, encode.StandardError);

        var probe = new QualityProbe(Runner, NullLogger<QualityProbe>.Instance);
        var metric = await DetectMetricAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var measurement = await probe.CompareAsync(
                source, 0d, encoded, 0d, 1.5d, 320, 240, metric, CancellationToken.None);

            Assert.True(
                measurement.Succeeded,
                FormattableString.Invariant($"Attempt {attempt + 1} produced no score: {measurement.FailureReason}"));
        }
    }

    /// <summary>A copied video stream is the same pictures, so the sampler must not spend a minute proving it.</summary>
    [SkippableFact]
    public async Task A_stream_copy_is_not_compared_at_all()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = Path.Combine(_dir, "copy-source.mkv");
        var build = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=60",
             "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p", source],
            CancellationToken.None);
        Assert.True(build.Success, build.StandardError);

        var analysis = new FileAnalysis
        {
            ItemId = Guid.NewGuid(),
            Name = "clip",
            Path = source,
            Container = "mkv",
            SizeBytes = new FileInfo(source).Length,
            DurationSeconds = 60d,
            IsEligible = true,
            Video = new VideoTrackInfo { Index = 0, Codec = "h264", Width = 320, Height = 240, Range = "SDR" }
        };

        var plan = new PlanResult
        {
            Arguments = ["-i", source, "-map", "0:v:0", "-c:v", "copy", "-y", Path.Combine(_dir, "out.mkv")],
            OutputExtension = "mkv",
            VideoIsCopied = true
        };

        var probe = new CountingProbe();
        var encoder = new SampleEncoder(Runner, probe, NullLogger<SampleEncoder>.Instance);

        var measurement = await encoder.MeasureAsync(analysis, plan, _dir, QualityProbe.Vmaf, 0, CancellationToken.None);

        Assert.True(measurement.Succeeded, measurement.FailureReason);
        Assert.Equal(0, probe.Comparisons);
        Assert.Null(measurement.QualityScore);
    }

    private async Task<string> DetectMetricAsync()
    {
        var filters = await Runner.RunAsync(_ffmpeg!, ["-hide_banner", "-filters"], CancellationToken.None);
        return (filters.StandardOutput + filters.StandardError).Contains(" libvmaf ", StringComparison.Ordinal)
            ? QualityProbe.Vmaf
            : QualityProbe.Ssim;
    }

    private sealed class CountingProbe : IQualityProbe
    {
        public int Comparisons { get; private set; }

        public Task<QualityMeasurement> CompareAsync(
            string referencePath,
            double referenceStartSeconds,
            string encodedPath,
            double encodedStartSeconds,
            double seconds,
            int? referenceWidth,
            int? referenceHeight,
            string metric,
            CancellationToken cancellationToken)
        {
            Comparisons++;
            return Task.FromResult(new QualityMeasurement { Metric = metric, Score = 100d });
        }
    }
}
