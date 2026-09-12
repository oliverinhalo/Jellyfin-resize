using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// What this server can do, and how long that answer is good for. The probe costs several
/// processes, so it is cached — and everything here is about the ways a cached answer can be
/// wrong: shared with a caller who writes to it, or describing a server that has since changed.
/// </summary>
public class CapabilityServiceTests
{
    /// <summary>An ffmpeg that lists two encoders, and counts how often it is asked.</summary>
    private sealed class Runner : IFfmpegRunner
    {
        public string FfmpegPath { get; set; } = "/usr/lib/jellyfin-ffmpeg/ffmpeg";

        public string FfprobePath => "/usr/lib/jellyfin-ffmpeg/ffprobe";

        public int EncoderListings { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            if (arguments.Contains("-encoders"))
            {
                EncoderListings++;
                return Task.FromResult(new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput =
                        "Encoders:\n"
                        + " V..... = Video\n"
                        + " V....D libx264              H.264 / AVC\n"
                        + " V....D libx265              H.265 / HEVC\n"
                        + " A....D libopus              Opus\n"
                        + " A....D aac                  AAC\n"
                        + " A....D flac                 FLAC\n",
                    StandardError = string.Empty
                });
            }

            if (arguments.Contains("-filters"))
            {
                return Task.FromResult(new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = " ... ssim              Calculate the SSIM.\n",
                    StandardError = string.Empty
                });
            }

            return Task.FromResult(new ProcessResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
        }

        public Task<ProcessResult> RunEncodeAsync(
            IReadOnlyList<string> arguments,
            double? totalDurationSeconds,
            Action<EncodeProgress>? onProgress,
            bool lowPriority,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>A stand-in for the server: its settings can change between calls, as they do.</summary>
    private sealed class Server : IServerEncodingContext
    {
        public ServerEncodingSettings Settings { get; set; } = new ServerEncodingSettings();

        public int SettingsReads { get; private set; }

        public string? EncoderVersion => "7.1";

        public ServerEncodingSettings GetSettings()
        {
            SettingsReads++;
            return Settings;
        }

        public bool SupportsEncoder(string encoder) => true;
    }

    private static (CapabilityService Service, Runner Runner, Server Server) Build()
    {
        var runner = new Runner();
        var server = new Server();
        return (
            new CapabilityService(runner, server, NullLogger<CapabilityService>.Instance),
            runner,
            server);
    }

    [Fact]
    public async Task It_reports_the_encoders_ffmpeg_listed()
    {
        var (service, _, _) = Build();

        var caps = await service.GetAsync(CancellationToken.None);

        Assert.Contains(caps.VideoEncoders, e => e.Name == "libx265");
        Assert.Contains(caps.AudioEncoders, e => e.Name == "libopus");
        Assert.Null(caps.ProbeError);
        Assert.Equal("7.1", caps.FfmpegVersion);
    }

    /// <summary>
    /// The API stamps "may this user convert?" onto the answer it is about to send. Handing out
    /// the cached instance let one request's answer change another's — a non-administrator's
    /// request could leave the cache saying nobody may convert, or worse, saying they may. It is
    /// the same class of bug as the paused flag that used to be static.
    /// </summary>
    [Fact]
    public async Task One_callers_answer_cannot_change_another_callers()
    {
        var (service, _, _) = Build();

        var first = await service.GetAsync(CancellationToken.None);
        first.CanConvert = true;
        first.AllowHevcEncoding = false;

        var second = await service.GetAsync(CancellationToken.None);

        Assert.False(second.CanConvert);
        Assert.True(second.AllowHevcEncoding);
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// The codec list comes from ffmpeg and the permissions come from Jellyfin, and those change
    /// on entirely different timescales: an administrator ticking "allow HEVC encoding" should not
    /// have to restart the server before this plugin stops warning that it is off.
    /// </summary>
    [Fact]
    public async Task Server_settings_are_read_again_without_re_probing_ffmpeg()
    {
        var (service, runner, server) = Build();

        server.Settings = new ServerEncodingSettings { AllowHevcEncoding = false, AllowAv1Encoding = false };
        var before = await service.GetAsync(CancellationToken.None);
        Assert.False(before.AllowHevcEncoding);

        server.Settings = new ServerEncodingSettings
        {
            AllowHevcEncoding = true,
            AllowAv1Encoding = true,
            HardwareAcceleration = "vaapi",
            VaapiDevice = "/dev/dri/renderD128"
        };

        var after = await service.GetAsync(CancellationToken.None);

        Assert.True(after.AllowHevcEncoding);
        Assert.Equal("vaapi", after.HardwareAcceleration);
        Assert.Equal("/dev/dri/renderD128", after.VaapiDevice);

        // And the expensive half was not repeated to find that out.
        Assert.Equal(1, runner.EncoderListings);
        Assert.True(server.SettingsReads >= 2);
    }

    /// <summary>
    /// The cached answer describes one ffmpeg binary. An administrator who fixes the path under
    /// Dashboard → Playback, or an upgrade that moves it, gets a different one — and the plugin
    /// has to notice without being restarted.
    /// </summary>
    [Fact]
    public async Task A_different_ffmpeg_path_is_probed_again()
    {
        var (service, runner, _) = Build();

        await service.GetAsync(CancellationToken.None);
        await service.GetAsync(CancellationToken.None);
        Assert.Equal(1, runner.EncoderListings);

        runner.FfmpegPath = "/opt/ffmpeg/bin/ffmpeg";
        var caps = await service.GetAsync(CancellationToken.None);

        Assert.Equal(2, runner.EncoderListings);
        Assert.Equal("/opt/ffmpeg/bin/ffmpeg", caps.FfmpegPath);
    }

    /// <summary>The quality metric is detected from the filters this build actually has.</summary>
    [Fact]
    public async Task The_quality_metric_comes_from_the_filters_ffmpeg_reports()
    {
        var (service, _, _) = Build();

        var caps = await service.GetAsync(CancellationToken.None);

        Assert.Equal(QualityProbe.Ssim, caps.QualityMetric);
    }

    /// <summary>
    /// Every field has to be copied, or a snapshot silently loses something — which for this type
    /// would mean a dialog told there is no encoder, or no quality metric, at random.
    /// </summary>
    [Fact]
    public void Copying_a_capability_set_copies_everything_on_it()
    {
        var original = new Jellyfin.Plugin.MediaOptimizer.Models.Capabilities
        {
            FfmpegPath = "/usr/bin/ffmpeg",
            FfmpegVersion = "7.1",
            VideoEncoders = [new Jellyfin.Plugin.MediaOptimizer.Models.EncoderOption { Name = "libx265" }],
            AudioEncoders = [new Jellyfin.Plugin.MediaOptimizer.Models.EncoderOption { Name = "flac" }],
            Containers = ["mkv"],
            HardwareAcceleration = "qsv",
            VaapiDevice = "/dev/dri/renderD128",
            AllowHevcEncoding = false,
            AllowAv1Encoding = false,
            QualityMetric = QualityProbe.Vmaf,
            CanConvert = true,
            ProbeError = "something"
        };

        var copy = original.Clone();

        foreach (var property in typeof(Jellyfin.Plugin.MediaOptimizer.Models.Capabilities)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead)
            {
                continue;
            }

            Assert.Equal(property.GetValue(original), property.GetValue(copy));
        }
    }
}
