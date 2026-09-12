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

    /// <summary>
    /// Builds a clip whose first audio track is lossy and whose second is lossless, which is how a
    /// remux with a commentary track in front of the main audio is laid out.
    /// </summary>
    /// <returns>The path of the generated file.</returns>
    private async Task<string> CreateTwoAudioSourceAsync()
    {
        var path = Path.Combine(_dir, "twoaudio.mkv");
        string[] args =
        [
            "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=4",
            "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
            "-c:a:0", "aac", "-c:a:1", "flac",
            path
        ];

        var result = await Runner.RunAsync(_ffmpeg!, args, CancellationToken.None);
        Assert.True(result.Success, "Could not build the two-audio test source: " + result.StandardError);
        return path;
    }

    /// <summary>
    /// Builds a clip that looks like a Blu-ray remux to the muxer: a text subtitle track that MP4
    /// cannot store as-is.
    /// </summary>
    private async Task<string> CreateSubtitledSourceAsync()
    {
        var srt = Path.Combine(_dir, "subs.srt");
        await File.WriteAllTextAsync(
            srt,
            "1\n00:00:00,000 --> 00:00:02,000\nhello\n\n2\n00:00:02,000 --> 00:00:04,000\nworld\n\n");

        var path = Path.Combine(_dir, "subbed.mkv");
        string[] args =
        [
            "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
            "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-c:s", "copy",
            path
        ];

        var result = await Runner.RunAsync(_ffmpeg!, args, CancellationToken.None);
        Assert.True(result.Success, "Could not build the subtitled test source: " + result.StandardError);
        return path;
    }

    /// <summary>
    /// The regression test for the bug that killed two 20 GiB jobs. The plan copied the source's
    /// SubRip track into MP4, FFmpeg refused to write the header, and the encode died with
    /// "Error sending frames to consumers: Invalid argument" having produced zero frames.
    /// <para>
    /// Only a real muxer can prove this is fixed, so this test runs the planned arguments rather
    /// than inspecting them.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Mp4_output_with_text_subtitles_actually_muxes()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSubtitledSourceAsync();
        var analysis = AnalysisFor(source, "aac", audioLossless: false);
        analysis.Subtitles = [new SubtitleTrackInfo { Index = 2, Codec = "subrip", Language = "eng" }];
        var output = Path.Combine(_dir, "subbed.mp4");

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.Standard,
            Container = "mp4",
            Video = VideoAction.Encode,
            VideoCodec = "libx264",
            Preset = "ultrafast",
            RateControl = RateControlMode.ConstantQuality,
            Quality = 30,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner("libx264", "aac").PlanAsync(analysis, request, output, CancellationToken.None);
        Assert.True(plan.IsRunnable, "Plan was blocked: " + string.Join("; ", plan.Warnings.Select(w => w.Message)));

        var result = await Runner.RunEncodeAsync(
            plan.Arguments, analysis.DurationSeconds, _ => { }, false, CancellationToken.None);

        Assert.True(result.Success, "ffmpeg failed: " + result.StandardError);
        Assert.True(new FileInfo(output).Length > 0, "ffmpeg wrote an empty file.");

        // The subtitles must survive the container change, not just avoid crashing it.
        var probe = await Runner.RunAsync(
            _ffprobe!,
            ["-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "csv=p=0", output],
            CancellationToken.None);
        Assert.Contains("mov_text", probe.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same bug: MP4 has no box for lossless Blu-ray audio, so a plan that
    /// copies it must be stopped before it runs rather than failing hours in.
    /// </summary>
    [SkippableFact]
    public async Task Audio_that_mp4_cannot_hold_is_blocked_and_would_indeed_fail()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateSourceAsync("flac");
        var analysis = AnalysisFor(source, "truehd", audioLossless: true);
        var output = Path.Combine(_dir, "blocked.mp4");

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.Standard,
            Container = "mp4",
            Video = VideoAction.Encode,
            VideoCodec = "libx264",
            Preset = "ultrafast",
            Quality = 30,
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        var plan = await Planner("libx264", "aac").PlanAsync(analysis, request, output, CancellationToken.None);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Warnings, w => w.Code == "AUDIO_CONTAINER_INCOMPATIBLE");
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
        Assert.Contains(plan.LosslessAudioChecks, c => c.SourceStreamIndex == 1 && c.OutputAudioIndex == 0);

        var samples = new List<EncodeProgress>();
        var result = await Runner.RunEncodeAsync(
            plan.Arguments, analysis.DurationSeconds, samples.Add, false, CancellationToken.None);

        Assert.True(result.Success, "ffmpeg failed: " + result.StandardError);
        Assert.NotEmpty(samples);
        Assert.True(samples[^1].OutTimeSeconds > 0d, "Progress parsing produced no output time.");

        var verifier = new VerificationService(Runner, NullLogger<VerificationService>.Instance);
        var verification = await verifier.VerifyAsync(
            source, output, analysis.DurationSeconds, deepScan: true, plan.LosslessAudioChecks, CancellationToken.None);

        Assert.True(verification.Passed, "Verification failed: " + verification.FailureReason);
        Assert.True(verification.LosslessVerified, "The audio should have hashed identically.");
    }

    /// <summary>
    /// A file whose first audio track is copied and whose second is the lossless one. The
    /// bit-exactness check compares each source track against a position in the output, and
    /// assuming the Nth lossless track is the Nth output track is only true when no copied track
    /// sits in front of it. It did here, so the FLAC track was compared against the copied AAC
    /// one, the hashes differed, and a job that was genuinely bit-exact was failed and thrown away.
    /// </summary>
    [SkippableFact]
    public async Task Lossless_track_behind_a_copied_track_still_verifies()
    {
        Skip.IfNot(HasFfmpeg, "ffmpeg is not installed.");

        var source = await CreateTwoAudioSourceAsync();
        var output = Path.Combine(_dir, "mixed.mkv");

        var analysis = AnalysisFor(source, "aac", audioLossless: false);
        analysis.Audio =
        [
            new AudioTrackInfo { Index = 1, TypeIndex = 0, Codec = "aac", Channels = 1, SampleRate = 48000, IsLossless = false },
            new AudioTrackInfo { Index = 2, TypeIndex = 1, Codec = "flac", Channels = 1, SampleRate = 48000, IsLossless = true }
        ];

        var request = new EncodeRequest
        {
            ItemId = analysis.ItemId,
            Strategy = OptimizationStrategy.LosslessOnly,
            Container = "mkv",
            Video = VideoAction.Copy,
            AudioTracks =
            [
                new AudioTrackRequest { Index = 1, Action = AudioAction.Copy },
                new AudioTrackRequest { Index = 2, Action = AudioAction.Encode, Codec = "flac" }
            ]
        };

        var plan = await Planner("libx264", "flac").PlanAsync(analysis, request, output, CancellationToken.None);
        Assert.True(plan.IsRunnable, string.Join("; ", plan.Warnings.Select(w => w.Message)));

        // The lossless track is the second audio stream of the output, not the first.
        var check = Assert.Single(plan.LosslessAudioChecks);
        Assert.Equal(2, check.SourceStreamIndex);
        Assert.Equal(1, check.OutputAudioIndex);

        var result = await Runner.RunEncodeAsync(plan.Arguments, 4d, null, false, CancellationToken.None);
        Assert.True(result.Success, "ffmpeg failed: " + result.StandardError);

        var verifier = new VerificationService(Runner, NullLogger<VerificationService>.Instance);
        var verification = await verifier.VerifyAsync(
            source, output, 4d, deepScan: false, plan.LosslessAudioChecks, CancellationToken.None);

        Assert.True(verification.Passed, "Verification failed: " + verification.FailureReason);
        Assert.True(verification.LosslessVerified, "The FLAC track is bit-exact and must verify as such.");
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
            source, output, 4d, deepScan: false, [new LosslessAudioCheck(1, 0)], CancellationToken.None);

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
