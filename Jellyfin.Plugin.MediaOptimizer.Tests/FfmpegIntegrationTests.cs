using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Runs the real pipeline against a real ffmpeg on a generated file. These are the tests that
/// actually substantiate the plugin's lossless claim; the rest only check bookkeeping.
/// </summary>
public class FfmpegIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;

    public FfmpegIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-tests-" + Guid.NewGuid().ToString("N"));
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
            // Test scratch space; nothing depends on it being removed.
        }

        GC.SuppressFinalize(this);
    }

    private bool HasFfmpeg => _ffmpeg is not null && _ffprobe is not null;

    private FfmpegRunner Runner => new FfmpegRunner(_ffmpeg!, _ffprobe!, NullLogger<FfmpegRunner>.Instance);

    private static string? Which(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator);

        foreach (var dir in paths)
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

    /// <summary>Builds a short clip with a video stream and one lossless FLAC audio stream.</summary>
    private async Task<string> CreateSourceAsync(string audioCodec = "flac")
    {
        var path = Path.Combine(_dir, "source.mkv");
        string[] args =
        [
            "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
            "-c:a", audioCodec,
            path
        ];

        var result = await Runner.RunAsync(_ffmpeg!, args, CancellationToken.None);
        Assert.True(result.Success, "Could not build the test source: " + result.StandardError);
        return path;
    }

    private static FileAnalysis AnalysisFor(string path, string audioCodec, bool audioLossless) => new FileAnalysis
    {
        ItemId = Guid.NewGuid(),
        Name = "Test clip",
        Path = path,
        Container = "mkv",
        SizeBytes = new FileInfo(path).Length,
        DurationSeconds = 4d,
        IsEligible = true,
        IsWritable = true,
        Video = new VideoTrackInfo
        {
            Index = 0,
            Codec = "h264",
            Width = 320,
            Height = 240,
            BitDepth = 8,
            PixelFormat = "yuv420p",
            FrameRate = 25f,
            Range = "SDR"
        },
        Audio =
        [
            new AudioTrackInfo
            {
                Index = 1,
                TypeIndex = 0,
                Codec = audioCodec,
                Channels = 1,
                SampleRate = 48000,
                IsLossless = audioLossless
            }
        ]
    };

    private static EncodePlanner Planner(params string[] encoders)
    {
        var caps = new Capabilities
        {
            VideoEncoders = encoders
                .Where(e => e.StartsWith("libx", StringComparison.Ordinal))
                .Select(e => new EncoderOption
                {
                    Name = e,
                    Codec = e == "libx265" ? "hevc" : "h264",
                    DisplayName = e,
                    Supports10Bit = true,
                    Presets = ["ultrafast", "medium"]
                }).ToList(),
            AudioEncoders = encoders
                .Where(e => e is "flac" or "aac" or "libopus")
                .Select(e => new EncoderOption { Name = e, Codec = e, DisplayName = e })
                .ToList(),
            AllowHevcEncoding = true,
            AllowAv1Encoding = true
        };

        return new EncodePlanner(new StubCapabilities(caps), NullLogger<EncodePlanner>.Instance);
    }

    private sealed class StubCapabilities : ICapabilityService
    {
        private readonly Capabilities _caps;

        public StubCapabilities(Capabilities caps) => _caps = caps;

        public Task<Capabilities> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_caps);

        public Task<bool> HasEncoderAsync(string encoder, CancellationToken cancellationToken) =>
            Task.FromResult(
                _caps.VideoEncoders.Any(e => e.Name == encoder) || _caps.AudioEncoders.Any(e => e.Name == encoder));
    }

    [SkippableFact]
    public async Task Lossless_flac_conversion_is_bit_exact_and_verifies()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var analysis = AnalysisFor(source, "flac", audioLossless: true);
        var output = Path.Combine(_dir, "out.mkv");

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.LosslessOnly,
            Container = "mkv",
            Video = VideoAction.Copy,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Encode, Codec = "flac" }]
        };

        var plan = await Planner("libx264", "flac").PlanAsync(analysis, request, output, CancellationToken.None);

        Assert.True(plan.IsRunnable, "Plan was blocked: " + string.Join("; ", plan.Warnings.Select(w => w.Message)));
        Assert.True(plan.IsLossless, "Copying video and re-encoding lossless audio to FLAC must count as lossless.");
        Assert.Contains(1, plan.LosslessAudioIndexes);

        var samples = new List<EncodeProgress>();
        var result = await Runner.RunEncodeAsync(
            plan.Arguments, analysis.DurationSeconds, samples.Add, false, CancellationToken.None);

        Assert.True(result.Success, "ffmpeg failed: " + result.StandardError);
        Assert.NotEmpty(samples);
        Assert.True(samples[^1].OutTimeSeconds > 0d, "Progress parsing produced no output time.");

        var verifier = new VerificationService(Runner, NullLogger<VerificationService>.Instance);
        var verification = await verifier.VerifyAsync(
            source, output, analysis.DurationSeconds, deepScan: true, plan.LosslessAudioIndexes, CancellationToken.None);

        Assert.True(verification.Passed, "Verification failed: " + verification.FailureReason);
        Assert.True(verification.LosslessVerified, "The audio should have hashed identically.");
    }

    [SkippableFact]
    public async Task Verification_rejects_a_lossy_track_that_claims_to_be_lossless()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        // This is the safety net: if a "lossless" job ever produces lossy audio, the gate must
        // catch it and refuse to let the result anywhere near the original file.
        var source = await CreateSourceAsync("flac");
        var output = Path.Combine(_dir, "lossy.mkv");

        var encode = await Runner.RunAsync(_ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-i", source, "-c:v", "copy", "-c:a", "aac", "-b:a", "64k", output],
            CancellationToken.None);
        Assert.True(encode.Success, encode.StandardError);

        var verifier = new VerificationService(Runner, NullLogger<VerificationService>.Instance);
        var verification = await verifier.VerifyAsync(
            source, output, 4d, deepScan: false, [1], CancellationToken.None);

        Assert.False(verification.Passed);
        Assert.False(verification.LosslessVerified);
        Assert.Contains("bit-identically", verification.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Verification_rejects_a_truncated_output()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var output = Path.Combine(_dir, "short.mkv");

        // Two seconds of a four second source: exactly the failure a duration check exists for.
        var encode = await Runner.RunAsync(_ffmpeg!,
            ["-nostdin", "-v", "error", "-y", "-i", source, "-t", "2", "-c", "copy", output],
            CancellationToken.None);
        Assert.True(encode.Success, encode.StandardError);

        var verifier = new VerificationService(Runner, NullLogger<VerificationService>.Instance);
        var verification = await verifier.VerifyAsync(
            source, output, 4d, deepScan: false, [], CancellationToken.None);

        Assert.False(verification.Passed);
        Assert.Contains("Duration mismatch", verification.FailureReason!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Planned_downscale_actually_produces_the_requested_resolution()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var analysis = AnalysisFor(source, "flac", audioLossless: true);
        var output = Path.Combine(_dir, "small.mkv");

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "libx264",
            TargetHeight = 120,
            Preset = "ultrafast",
            Quality = 30,
            RateControl = RateControlMode.ConstantQuality,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner("libx264", "flac").PlanAsync(analysis, request, output, CancellationToken.None);
        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));
        Assert.False(plan.IsLossless, "A lossy re-encode must never be reported as lossless.");

        var result = await Runner.RunEncodeAsync(
            plan.Arguments, analysis.DurationSeconds, null, false, CancellationToken.None);
        Assert.True(result.Success, "ffmpeg failed: " + result.StandardError);

        var probe = await Runner.RunAsync(_ffprobe!,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
             "-of", "csv=p=0", output],
            CancellationToken.None);

        Assert.Equal("160,120", probe.StandardOutput.Trim());
    }

    [SkippableFact]
    public async Task Upscaling_is_refused_rather_than_performed()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var analysis = AnalysisFor(source, "flac", audioLossless: true);
        var output = Path.Combine(_dir, "big.mkv");

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Encode,
            VideoCodec = "libx264",
            TargetHeight = 2160,
            Preset = "ultrafast",
            Quality = 30,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner("libx264", "flac").PlanAsync(analysis, request, output, CancellationToken.None);

        Assert.Contains(plan.Warnings, w => w.Code == "NO_UPSCALE");
        Assert.DoesNotContain("scale=", string.Join(' ', plan.Arguments), StringComparison.Ordinal);

        var result = await Runner.RunEncodeAsync(
            plan.Arguments, analysis.DurationSeconds, null, false, CancellationToken.None);
        Assert.True(result.Success, result.StandardError);

        var probe = await Runner.RunAsync(_ffprobe!,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=height", "-of", "csv=p=0", output],
            CancellationToken.None);

        Assert.Equal("240", probe.StandardOutput.Trim());
    }

    [SkippableFact]
    public async Task Dropping_an_audio_track_shrinks_the_file_without_touching_the_video()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var analysis = AnalysisFor(source, "flac", audioLossless: true);
        var output = Path.Combine(_dir, "noaudio.mkv");

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Container = "mkv",
            Video = VideoAction.Copy,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Drop }]
        };

        var plan = await Planner("libx264", "flac").PlanAsync(analysis, request, output, CancellationToken.None);
        var result = await Runner.RunEncodeAsync(plan.Arguments, 4d, null, false, CancellationToken.None);
        Assert.True(result.Success, result.StandardError);

        Assert.True(
            new FileInfo(output).Length < new FileInfo(source).Length,
            "Removing a lossless audio track should reduce the file size.");

        // The kept video stream must be byte-for-byte the same pictures it was before.
        var sourceHash = await HashVideoAsync(source);
        var outputHash = await HashVideoAsync(output);
        Assert.Equal(sourceHash, outputHash);
    }

    [SkippableFact]
    public async Task Cancelling_an_encode_kills_ffmpeg_and_leaves_the_source_alone()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var before = new FileInfo(source).Length;
        var output = Path.Combine(_dir, "cancelled.mkv");

        using var cts = new CancellationTokenSource();
        string[] args =
        [
            "-i", source, "-c:v", "libx264", "-preset", "veryslow", "-crf", "0",
            "-c:a", "copy", "-y", output
        ];

        var task = Runner.RunEncodeAsync(args, 4d, _ => cts.Cancel(), false, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(before, new FileInfo(source).Length);
    }

    private async Task<string> HashVideoAsync(string path)
    {
        var result = await Runner.RunAsync(_ffmpeg!,
            ["-nostdin", "-v", "error", "-i", path, "-map", "0:v:0", "-f", "hash", "-hash", "md5", "-"],
            CancellationToken.None);

        Assert.True(result.Success, result.StandardError);
        return result.StandardOutput.Trim();
    }
}
