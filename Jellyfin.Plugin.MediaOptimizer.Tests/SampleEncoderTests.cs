using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The bookkeeping around the sampled estimate. The measurement itself is proven against a real
/// ffmpeg in <see cref="FfmpegIntegrationTests"/>; what is checked here is everything that decides
/// what the measured numbers <em>mean</em> — which samples counted, and what the clock was
/// measuring while they ran.
/// </summary>
public class SampleEncoderTests : IDisposable
{
    private readonly string _dir;

    public SampleEncoderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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

    /// <summary>
    /// Stands in for ffmpeg: writes a file of a known size, takes a known amount of time, and can
    /// be told to fail its first call.
    /// </summary>
    private sealed class StubRunner : IFfmpegRunner
    {
        private readonly int _failuresFirst;
        private readonly int _failureMilliseconds;
        private readonly int _successMilliseconds;
        private int _calls;

        public StubRunner(int failuresFirst, int failureMilliseconds, int successMilliseconds)
        {
            _failuresFirst = failuresFirst;
            _failureMilliseconds = failureMilliseconds;
            _successMilliseconds = successMilliseconds;
        }

        public List<IReadOnlyList<string>> Invocations { get; } = new List<IReadOnlyList<string>>();

        public string FfmpegPath => "/usr/bin/ffmpeg";

        public string FfprobePath => "/usr/bin/ffprobe";

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessResult> RunEncodeAsync(
            IReadOnlyList<string> arguments,
            double? totalDurationSeconds,
            Action<EncodeProgress>? onProgress,
            bool lowPriority,
            CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            var call = Interlocked.Increment(ref _calls);

            if (call <= _failuresFirst)
            {
                Thread.Sleep(_failureMilliseconds);
                return Task.FromResult(new ProcessResult { ExitCode = 1, StandardError = "stub failure" });
            }

            Thread.Sleep(_successMilliseconds);
            File.WriteAllBytes(arguments[^1], new byte[80_000]);
            return Task.FromResult(new ProcessResult { ExitCode = 0 });
        }
    }

    private static FileAnalysis Source(double durationSeconds) => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "A Film",
        Path = "/media/film.mkv",
        Container = "mkv",
        SizeBytes = 8_000_000_000,
        DurationSeconds = durationSeconds,
        IsEligible = true,
        Video = new VideoTrackInfo { Index = 0, Codec = "h264", Width = 1920, Height = 1080, Range = "SDR" }
    };

    private static PlanResult Plan() => new PlanResult
    {
        Arguments = ["-i", "/media/film.mkv", "-map", "0:v:0", "-c:v", "libx265", "-y", "/work/out.mkv"],
        OutputExtension = "mkv"
    };

    /// <summary>
    /// A sample that failed still took time. Counting it against the seconds that were actually
    /// encoded reports the job as slower than it is — under a label that says "measured", which is
    /// exactly the sort of number this whole feature exists to replace.
    /// </summary>
    [Fact]
    public async Task A_failed_sample_does_not_slow_down_the_measured_speed()
    {
        var runner = new StubRunner(failuresFirst: 1, failureMilliseconds: 500, successMilliseconds: 40);
        var encoder = new SampleEncoder(runner, NullLogger<SampleEncoder>.Instance);

        var measurement = await encoder.MeasureAsync(Source(7200d), Plan(), _dir, null, CancellationToken.None);

        Assert.True(measurement.Succeeded, measurement.FailureReason);
        Assert.Equal(2, measurement.Samples);
        Assert.Equal(16d, measurement.SampledSeconds);

        // Two samples of eight seconds each encoded in about 80ms: hundreds of times realtime.
        // Had the failed sample's half second been counted, this would be around 25.
        Assert.NotNull(measurement.SpeedFactor);
        Assert.True(
            measurement.SpeedFactor > 80d,
            FormattableString.Invariant($"Speed was {measurement.SpeedFactor}, which includes time spent on a sample that produced nothing."));
    }

    [Fact]
    public async Task Every_sample_failing_is_reported_as_a_failure_rather_than_a_measurement()
    {
        var runner = new StubRunner(failuresFirst: 99, failureMilliseconds: 1, successMilliseconds: 1);
        var encoder = new SampleEncoder(runner, NullLogger<SampleEncoder>.Instance);

        var measurement = await encoder.MeasureAsync(Source(7200d), Plan(), _dir, null, CancellationToken.None);

        Assert.False(measurement.Succeeded);
        Assert.Contains("would fail too", measurement.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Samples_are_taken_from_three_places_in_the_file()
    {
        var runner = new StubRunner(failuresFirst: 0, failureMilliseconds: 1, successMilliseconds: 1);
        var encoder = new SampleEncoder(runner, NullLogger<SampleEncoder>.Instance);

        await encoder.MeasureAsync(Source(7200d), Plan(), _dir, null, CancellationToken.None);

        var offsets = runner.Invocations
            .Select(a => a[a.IndexOf("-ss", StringComparer.Ordinal) + 1])
            .ToList();

        Assert.Equal(["1440.000", "3600.000", "5760.000"], offsets);

        // -ss must precede -i, or ffmpeg decodes and discards everything up to the sample point,
        // which on a two-hour file is the entire cost of the exercise.
        foreach (var arguments in runner.Invocations)
        {
            Assert.True(
                arguments.IndexOf("-ss", StringComparer.Ordinal) < arguments.IndexOf("-i", StringComparer.Ordinal),
                "-ss has to come before -i to seek rather than decode.");
        }
    }

    /// <summary>A sample that would run past the end of the file would measure fewer seconds
    /// than it claims, and so report a rate that is too high.</summary>
    [Fact]
    public void No_sample_runs_off_the_end_of_the_file()
    {
        foreach (var duration in new[] { 46d, 60d, 120d, 7200d })
        {
            foreach (var offset in SampleEncoder.SampleOffsets(duration))
            {
                Assert.True(offset + 8d <= duration, FormattableString.Invariant($"{offset} + 8 > {duration}"));
            }
        }
    }

    [Fact]
    public async Task Nothing_is_left_behind_in_the_working_directory()
    {
        var runner = new StubRunner(failuresFirst: 0, failureMilliseconds: 1, successMilliseconds: 1);
        var encoder = new SampleEncoder(runner, NullLogger<SampleEncoder>.Instance);

        await encoder.MeasureAsync(Source(7200d), Plan(), _dir, null, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_dir));
    }
}

/// <summary>Extension used only by the tests above, to find an argument by name.</summary>
internal static class ArgumentListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> arguments, string value, StringComparer comparer)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (comparer.Equals(arguments[i], value))
            {
                return i;
            }
        }

        return -1;
    }
}
