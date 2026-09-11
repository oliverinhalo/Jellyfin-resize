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

    [Fact]
    public void Skips_the_legend_ffmpeg_prints_above_the_table()
    {
        // The legend's rows look exactly like encoder rows apart from the "=" in the second
        // column. Picking them up would offer codecs named "Video" and "Audio".
        const string Output = """
            Encoders:
             V..... = Video
             A..... = Audio
             S..... = Subtitle
             .F.... = Frame-level multithreading
             ..S... = Slice-level multithreading
             ...X.. = Codec is experimental
             ....B. = Supports draw_horiz_band
             .....D = Supports direct rendering method 1
             ------
             V....D libx264              libx264 H.264 / AVC
            """;

        var encoders = CapabilityService.ParseEncoderList(Output);

        Assert.Single(encoders);
        Assert.Contains("libx264", encoders);
    }

    [Fact]
    public void Reads_encoder_names_containing_dashes_and_dots()
    {
        const string Output = """
             V....D libaom-av1           libaom AV1 (codec av1)
             V....D libvpx-vp9           libvpx VP9 (codec vp9)
             A....D libfdk_aac           Fraunhofer FDK AAC (codec aac)
            """;

        var encoders = CapabilityService.ParseEncoderList(Output);

        Assert.Contains("libaom-av1", encoders);
        Assert.Contains("libvpx-vp9", encoders);
        Assert.Contains("libfdk_aac", encoders);
    }

    [Fact]
    public void Survives_an_ffmpeg_that_prints_an_extra_flag_column()
    {
        // FFmpeg has added capability flags before. A listing this parser does not recognise
        // leaves the plugin with an empty codec dropdown and nothing it can convert, so the flag
        // block is matched by shape rather than pinned to today's exact six letters.
        const string Output = """
             V....DE libx264             libx264 H.264 / AVC
             A....DE libopus             libopus Opus
            """;

        var encoders = CapabilityService.ParseEncoderList(Output);

        Assert.Contains("libx264", encoders);
        Assert.Contains("libopus", encoders);
    }
}
