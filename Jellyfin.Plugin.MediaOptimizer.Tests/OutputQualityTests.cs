using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The check that runs after the encode rather than before it.
/// <para>
/// Everything else this plugin measures, it measures from samples taken before the conversion
/// exists. This one looks at the finished file, in the moment when the original is still untouched
/// and a bad conversion can still be refused — so the things that matter are that it lines the two
/// files up correctly, that the verdict is the worst stretch rather than the average, and that a
/// floor nobody can measure against fails closed instead of passing everything.
/// </para>
/// </summary>
public class OutputQualityTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;

    public OutputQualityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-outq-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A probe that answers with whatever scores it was given, and records the asking.</summary>
    private sealed class StubProbe : IQualityProbe
    {
        private readonly Queue<double?> _scores;

        public StubProbe(params double?[] scores)
        {
            _scores = new Queue<double?>(scores);
        }

        public List<(double ReferenceStart, double EncodedStart, double Seconds)> Calls { get; } = [];

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
            Calls.Add((referenceStartSeconds, encodedStartSeconds, seconds));

            var next = _scores.Count > 0 ? _scores.Dequeue() : null;

            return Task.FromResult(next is null
                ? new QualityMeasurement { Metric = metric, FailureReason = "no score" }
                : new QualityMeasurement { Metric = metric, Score = next.Value });
        }
    }

    private static FileAnalysis Analysis(double duration, string path) => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "A Film",
        Path = path,
        Container = "mkv",
        DurationSeconds = duration,
        Video = new VideoTrackInfo { Index = 0, Codec = "h264", Width = 1920, Height = 1080 }
    };

    private static OutputQualityService Service(IQualityProbe probe) =>
        new OutputQualityService(probe, NullLogger<OutputQualityService>.Instance);

    // ---------------------------------------------------------------- where it looks

    /// <summary>Three stretches, spread across the film rather than clustered at the start.</summary>
    [Fact]
    public void A_full_length_film_is_compared_at_three_points_across_it()
    {
        var segments = OutputQualityService.SegmentsFor(7200d);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.Equal(OutputQualityService.SegmentSeconds, s.Seconds));
        Assert.True(segments[0].Start < segments[1].Start && segments[1].Start < segments[2].Start);
        Assert.True(segments[0].Start > 600d, "The first point is well into the film, past the titles.");
    }

    /// <summary>
    /// A comparison window that runs off the end of the file is the case that used to hang the
    /// filter graph: the reference stops and the encoded stream is still waiting for frames.
    /// </summary>
    [Fact]
    public void No_comparison_runs_off_the_end_of_the_file()
    {
        foreach (var duration in new[] { 46d, 60d, 91d, 3600d })
        {
            foreach (var (start, seconds) in OutputQualityService.SegmentsFor(duration))
            {
                Assert.True(
                    start + seconds <= duration + 0.001d,
                    FormattableString.Invariant($"A {duration}s file was compared from {start}s for {seconds}s."));
                Assert.True(start >= 0d);
            }
        }
    }

    /// <summary>A short file is cheaper to compare in one pass than to seek into three times.</summary>
    [Fact]
    public void A_short_file_is_compared_in_one_go()
    {
        var segments = OutputQualityService.SegmentsFor(30d);

        Assert.Single(segments);
        Assert.Equal(0d, segments[0].Start);
        Assert.Equal(20d, segments[0].Seconds);

        // And a file shorter than that limit is compared entirely, not for longer than it is.
        Assert.Equal(12d, OutputQualityService.SegmentsFor(12d)[0].Seconds);
    }

    /// <summary>A file that does not say how long it is cannot be lined up at all.</summary>
    [Fact]
    public void A_file_of_unknown_length_is_not_compared()
    {
        Assert.Empty(OutputQualityService.SegmentsFor(0d));
    }

    // ---------------------------------------------------------------- what it measures

    /// <summary>
    /// The one thing that makes this different from measuring a sample: the finished file is the
    /// whole film, so the same moment has to be sought out in both. Asking for second 1080 of the
    /// original and second 0 of the output compares unrelated frames and reports a perfect
    /// conversion as ruined — silently, and on the strength of it, refuses it.
    /// </summary>
    [Fact]
    public async Task The_same_moment_is_sought_out_in_both_files()
    {
        var probe = new StubProbe(96d, 95d, 94d);
        var path = Path.Combine(_dir, "film.mkv");
        File.WriteAllText(path, "x");

        await Service(probe).MeasureAsync(Analysis(7200d, path), path, QualityProbe.Vmaf, CancellationToken.None);

        Assert.Equal(3, probe.Calls.Count);
        Assert.All(probe.Calls, c => Assert.Equal(c.ReferenceStart, c.EncodedStart));
        Assert.All(probe.Calls, c => Assert.True(c.EncodedStart > 0d, "Every comparison seeks into the output."));
    }

    /// <summary>
    /// The worst stretch is the verdict. An average hides the one scene that fell apart, which is
    /// the only thing anybody would have wanted to know.
    /// </summary>
    [Fact]
    public async Task The_worst_stretch_is_what_the_job_reports()
    {
        var probe = new StubProbe(97d, 81d, 96d);
        var path = Path.Combine(_dir, "worst.mkv");
        File.WriteAllText(path, "x");

        var quality = await Service(probe)
            .MeasureAsync(Analysis(3600d, path), path, QualityProbe.Vmaf, CancellationToken.None);

        Assert.True(quality.Succeeded, quality.FailureReason);
        Assert.Equal(81d, quality.WorstScore);
        Assert.Equal(3, quality.Samples);
        Assert.InRange(quality.MeanScore!.Value, 91d, 92d);

        var summary = OutputQualityService.Summarise(quality)!;
        Assert.Contains("81.0", summary, StringComparison.Ordinal);
        Assert.Contains("VMAF", summary, StringComparison.Ordinal);
        Assert.Contains("noticeably softer", summary, StringComparison.Ordinal);
    }

    /// <summary>One comparison that fails is not a measurement that failed.</summary>
    [Fact]
    public async Task A_comparison_that_fails_does_not_throw_away_the_ones_that_worked()
    {
        var probe = new StubProbe(94d, null, 92d);
        var path = Path.Combine(_dir, "partial.mkv");
        File.WriteAllText(path, "x");

        var quality = await Service(probe)
            .MeasureAsync(Analysis(3600d, path), path, QualityProbe.Vmaf, CancellationToken.None);

        Assert.True(quality.Succeeded, quality.FailureReason);
        Assert.Equal(2, quality.Samples);
        Assert.Equal(92d, quality.WorstScore);
    }

    /// <summary>And when none of them worked, the reason is carried rather than a made-up score.</summary>
    [Fact]
    public async Task A_measurement_that_produced_nothing_says_so_instead_of_scoring_zero()
    {
        var probe = new StubProbe(null, null, null);
        var path = Path.Combine(_dir, "none.mkv");
        File.WriteAllText(path, "x");

        var quality = await Service(probe)
            .MeasureAsync(Analysis(3600d, path), path, QualityProbe.Ssim, CancellationToken.None);

        Assert.False(quality.Succeeded);
        Assert.Null(quality.WorstScore);
        Assert.NotNull(quality.FailureReason);
    }

    /// <summary>A file with no video has nothing to compare, and is not an error.</summary>
    [Fact]
    public async Task A_file_with_no_video_is_not_compared()
    {
        var probe = new StubProbe(90d);
        var path = Path.Combine(_dir, "audio.mka");
        File.WriteAllText(path, "x");

        var analysis = Analysis(3600d, path);
        analysis.Video = null;

        var quality = await Service(probe).MeasureAsync(analysis, path, QualityProbe.Ssim, CancellationToken.None);

        Assert.False(quality.Succeeded);
        Assert.Empty(probe.Calls);
    }

    // ---------------------------------------------------------------- what it refuses

    /// <summary>With no floor set, nothing is refused however it measured.</summary>
    [Fact]
    public void Without_a_floor_nothing_is_refused()
    {
        var terrible = new OutputQuality { Metric = QualityProbe.Vmaf, WorstScore = 30d, Samples = 3 };

        Assert.Null(OutputQualityService.Refuse(terrible, QualityFloor.Off));
        Assert.Null(OutputQualityService.Refuse(null, QualityFloor.Off));
    }

    /// <summary>
    /// A conversion below the floor is refused, and the message has to carry the three things
    /// somebody needs: what it measured, what was asked for, and that their file is intact.
    /// </summary>
    [Fact]
    public void A_conversion_below_the_floor_is_refused_with_the_numbers()
    {
        var soft = new OutputQuality { Metric = QualityProbe.Vmaf, WorstScore = 84.2d, Samples = 3 };

        var refusal = OutputQualityService.Refuse(soft, QualityFloor.SlightlySofter);

        Assert.NotNull(refusal);
        Assert.Contains("84.2", refusal!, StringComparison.Ordinal);
        Assert.Contains("88.0", refusal, StringComparison.Ordinal);
        Assert.Contains("The original has not been touched", refusal, StringComparison.Ordinal);
    }

    /// <summary>And one that clears it is not.</summary>
    [Fact]
    public void A_conversion_that_clears_the_floor_proceeds()
    {
        var good = new OutputQuality { Metric = QualityProbe.Vmaf, WorstScore = 94d, Samples = 3 };

        Assert.Null(OutputQualityService.Refuse(good, QualityFloor.SlightlySofter));
        Assert.Null(OutputQualityService.Refuse(good, QualityFloor.VeryClose));

        // And the floors are ordered: a result that clears a lower one need not clear a higher.
        var middling = new OutputQuality { Metric = QualityProbe.Vmaf, WorstScore = 90d, Samples = 3 };
        Assert.Null(OutputQualityService.Refuse(middling, QualityFloor.SlightlySofter));
        Assert.NotNull(OutputQualityService.Refuse(middling, QualityFloor.VeryClose));
    }

    /// <summary>
    /// The floors are in the metric's own units. A score of 0.97 is a fine SSIM and a catastrophic
    /// VMAF, so a floor compared against the wrong scale would either refuse everything or nothing.
    /// </summary>
    [Fact]
    public void A_floor_is_applied_on_the_scale_of_the_metric_that_measured()
    {
        var ssim = new OutputQuality { Metric = QualityProbe.Ssim, WorstScore = 0.97d, Samples = 3 };
        var vmaf = new OutputQuality { Metric = QualityProbe.Vmaf, WorstScore = 0.97d, Samples = 3 };

        Assert.Null(OutputQualityService.Refuse(ssim, QualityFloor.NoticeablySofter));
        Assert.NotNull(OutputQualityService.Refuse(vmaf, QualityFloor.NoticeablySofter));

        Assert.Equal(
            QualityProbe.FloorFor(QualityProbe.Ssim, QualityVerdict.VeryClose),
            OutputQualityService.ThresholdFor(QualityProbe.Ssim, QualityFloor.VeryClose));
    }

    /// <summary>
    /// A floor that cannot be checked refuses the conversion. The promise the setting makes is
    /// that nothing worse than X replaces an original, and "we could not tell" does not keep it —
    /// so it fails safe, with the original intact and a message naming the fix.
    /// </summary>
    [Fact]
    public void A_floor_that_cannot_be_measured_refuses_rather_than_waving_it_through()
    {
        var unmeasured = new OutputQuality { FailureReason = "this FFmpeg build has no SSIM filter" };

        var refusal = OutputQualityService.Refuse(unmeasured, QualityFloor.NoticeablySofter);

        Assert.NotNull(refusal);
        Assert.Contains("could not be measured", refusal!, StringComparison.Ordinal);
        Assert.Contains("has no SSIM filter", refusal, StringComparison.Ordinal);
        Assert.Contains("The original has not been touched", refusal, StringComparison.Ordinal);

        // And it says what to do about it, because a job that fails with no way forward is worse
        // than one that does not run.
        Assert.Contains("turn the quality floor off", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A copied video stream is the same pictures and a hash-verified lossless conversion is the
    /// same file. Both clear every floor there is without a comparison, and measuring them would
    /// be a minute spent proving arithmetic.
    /// </summary>
    [Fact]
    public void A_conversion_that_cannot_have_changed_the_picture_clears_every_floor()
    {
        var identical = new OutputQuality
        {
            IdenticalByConstruction = true,
            FailureReason = "the video was copied rather than re-encoded — the same pictures"
        };

        Assert.Null(OutputQualityService.Refuse(identical, QualityFloor.VeryClose));
        Assert.Equal(identical.FailureReason, OutputQualityService.Summarise(identical));
    }

    // ---------------------------------------------------------------- with a real ffmpeg

    /// <summary>
    /// The measurement that proves the seeking is right: a stream copy of the source is the same
    /// pictures, so comparing the two at a point well into the file has to come back at the top of
    /// the scale. If the two seeks landed on different frames it would score like a bad encode,
    /// and every conversion measured this way would be reported — and with a floor set, refused —
    /// as ruined. Nothing else here can catch that.
    /// </summary>
    [SkippableFact]
    public async Task Comparing_a_copy_of_the_source_scores_at_the_top_of_the_scale()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = Path.Combine(_dir, "source.mkv");
        var copy = Path.Combine(_dir, "copy.mkv");

        // Sixty seconds of moving, detailed content, so the frames at 9s, 30s and 51s are all
        // different from each other: a misaligned comparison cannot accidentally look right.
        var build = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi",
             "-i", "testsrc2=size=320x240:rate=25:duration=60",
             "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "25",
             "-pix_fmt", "yuv420p", source],
            CancellationToken.None);
        Assert.True(build.Success, build.StandardError);

        var remux = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-i", source, "-c", "copy", copy],
            CancellationToken.None);
        Assert.True(remux.Success, remux.StandardError);

        var metric = await DetectMetricAsync();
        var service = Service(new QualityProbe(Runner, NullLogger<QualityProbe>.Instance));

        var analysis = Analysis(60d, source);
        analysis.Video!.Width = 320;
        analysis.Video.Height = 240;

        var quality = await service.MeasureAsync(analysis, copy, metric, CancellationToken.None);

        Assert.True(quality.Succeeded, quality.FailureReason);
        Assert.Equal(3, quality.Samples);
        Assert.Equal(
            QualityVerdict.Indistinguishable,
            QualityProbe.VerdictFor(metric, quality.WorstScore!.Value));

        // Which also means the floor lets it through, including the strictest one.
        Assert.Null(OutputQualityService.Refuse(quality, QualityFloor.VeryClose));
    }

    /// <summary>
    /// And the end-to-end claim the setting makes: a conversion that came out badly measures badly
    /// on the finished file and is refused, while a good one is not. Both encodes are whole files,
    /// measured the way a real job is measured.
    /// </summary>
    [SkippableFact]
    public async Task A_bad_conversion_is_refused_and_a_good_one_is_not()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = Path.Combine(_dir, "gate-source.mkv");
        var good = Path.Combine(_dir, "gate-good.mkv");
        var bad = Path.Combine(_dir, "gate-bad.mkv");

        var build = await Runner.RunAsync(
            _ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi",
             "-i", "testsrc2=size=320x240:rate=25:duration=60",
             "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-g", "25",
             "-pix_fmt", "yuv420p", source],
            CancellationToken.None);
        Assert.True(build.Success, build.StandardError);

        foreach (var (path, crf) in new[] { (good, "20"), (bad, "51") })
        {
            var encode = await Runner.RunAsync(
                _ffmpeg!,
                ["-nostdin", "-v", "error", "-y", "-i", source, "-c:v", "libx264", "-preset", "veryfast",
                 "-crf", crf, "-pix_fmt", "yuv420p", path],
                CancellationToken.None);
            Assert.True(encode.Success, encode.StandardError);
        }

        var metric = await DetectMetricAsync();
        var service = Service(new QualityProbe(Runner, NullLogger<QualityProbe>.Instance));
        var analysis = Analysis(60d, source);
        analysis.Video!.Width = 320;
        analysis.Video.Height = 240;

        var goodQuality = await service.MeasureAsync(analysis, good, metric, CancellationToken.None);
        var badQuality = await service.MeasureAsync(analysis, bad, metric, CancellationToken.None);

        Assert.True(goodQuality.Succeeded, goodQuality.FailureReason);
        Assert.True(badQuality.Succeeded, badQuality.FailureReason);
        Assert.True(
            goodQuality.WorstScore > badQuality.WorstScore,
            FormattableString.Invariant(
                $"A CRF 20 encode measured {goodQuality.WorstScore} and a CRF 51 one {badQuality.WorstScore}."));

        var refusal = OutputQualityService.Refuse(badQuality, QualityFloor.NoticeablySofter);
        Assert.NotNull(refusal);
        Assert.Contains("The original has not been touched", refusal!, StringComparison.Ordinal);

        Assert.Null(OutputQualityService.Refuse(goodQuality, QualityFloor.NoticeablySofter));
    }

    private async Task<string> DetectMetricAsync()
    {
        var filters = await Runner.RunAsync(_ffmpeg!, ["-hide_banner", "-filters"], CancellationToken.None);
        return (filters.StandardOutput + filters.StandardError).Contains(" libvmaf ", StringComparison.Ordinal)
            ? QualityProbe.Vmaf
            : QualityProbe.Ssim;
    }
}
