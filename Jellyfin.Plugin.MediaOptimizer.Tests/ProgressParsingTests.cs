using Jellyfin.Plugin.MediaOptimizer.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

public class ProgressParsingTests
{
    [Fact]
    public void Parses_a_complete_progress_block()
    {
        var progress = new EncodeProgress();

        Assert.False(FfmpegRunner.TryApplyProgressLine("frame=1234", progress));
        Assert.False(FfmpegRunner.TryApplyProgressLine("out_time_us=45000000", progress));
        Assert.False(FfmpegRunner.TryApplyProgressLine("total_size=8388608", progress));
        Assert.False(FfmpegRunner.TryApplyProgressLine("speed=2.5x", progress));

        // Only the "progress" key ends a block and signals a usable sample.
        Assert.True(FfmpegRunner.TryApplyProgressLine("progress=continue", progress));

        Assert.Equal(1234, progress.Frame);
        Assert.Equal(45d, progress.OutTimeSeconds, 3);
        Assert.Equal(8388608, progress.TotalSize);
        Assert.Equal(2.5d, progress.Speed);
    }

    [Fact]
    public void Treats_out_time_ms_as_microseconds()
    {
        // ffmpeg's out_time_ms is a long-standing misnomer: the value is microseconds.
        var progress = new EncodeProgress();
        FfmpegRunner.TryApplyProgressLine("out_time_ms=60000000", progress);
        Assert.Equal(60d, progress.OutTimeSeconds, 3);
    }

    [Fact]
    public void Ignores_junk_and_negative_times()
    {
        var progress = new EncodeProgress();
        Assert.False(FfmpegRunner.TryApplyProgressLine("not a key value pair", progress));
        Assert.False(FfmpegRunner.TryApplyProgressLine(string.Empty, progress));

        // ffmpeg emits N/A and negative sentinels before the first frame lands.
        FfmpegRunner.TryApplyProgressLine("out_time_us=-9223372036854775807", progress);
        FfmpegRunner.TryApplyProgressLine("speed=N/A", progress);
        Assert.Equal(0d, progress.OutTimeSeconds);
        Assert.Null(progress.Speed);
    }

    [Fact]
    public void Parses_the_real_encoder_listing_format()
    {
        const string Output = """
             V....D av1                  Alliance for Open Media AV1
             V....D libx264              libx264 H.264 / AVC
             VF...D libx265              libx265 H.265 / HEVC
             A....D aac                  AAC (Advanced Audio Coding)
             A....D libopus              libopus Opus
            ------
            """;

        var encoders = CapabilityService.ParseEncoderList(Output);

        Assert.Contains("libx264", encoders);
        Assert.Contains("libx265", encoders);
        Assert.Contains("libopus", encoders);
        Assert.DoesNotContain("------", encoders);
    }
}
