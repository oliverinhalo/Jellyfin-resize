using Jellyfin.Plugin.MediaOptimizer.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

public class LosslessAnalyzerTests
{
    [Theory]
    [InlineData("ffv1", true)]
    [InlineData("huffyuv", true)]
    [InlineData("rawvideo", true)]
    [InlineData("h264", false)]
    [InlineData("hevc", false)]
    [InlineData("av1", false)]
    [InlineData(null, false)]
    public void IsLosslessVideo_identifies_only_truly_lossless_codecs(string? codec, bool expected) =>
        Assert.Equal(expected, LosslessAnalyzer.IsLosslessVideo(codec));

    [Theory]
    [InlineData("truehd", null, true)]
    [InlineData("flac", null, true)]
    [InlineData("pcm_s24le", null, true)]
    [InlineData("aac", null, false)]
    [InlineData("ac3", null, false)]
    [InlineData("opus", null, false)]
    public void IsLosslessAudio_identifies_bit_exact_codecs(string codec, string? profile, bool expected) =>
        Assert.Equal(expected, LosslessAnalyzer.IsLosslessAudio(codec, profile));

    // DTS is the awkward one: only the Master Audio variant is lossless, and that lives
    // in the profile rather than the codec name.
    [Theory]
    [InlineData("DTS-HD MA", true)]
    [InlineData("DTS-HD Master Audio", true)]
    [InlineData("DTS-HD HRA", false)]
    [InlineData("DTS", false)]
    [InlineData(null, false)]
    // "Matrix" begins with the same two letters as "MA", and DTS-ES Matrix is lossy. Reading it
    // as Master Audio would have the plugin call a conversion of it bit-exact and predict the
    // output at 85% of the source, when re-encoding a lossy track to FLAC makes it several times
    // larger.
    [InlineData("DTS-ES Matrix", false)]
    [InlineData("DTS-ES", false)]
    [InlineData("DTS Express", false)]
    [InlineData("DTS-HD MA + DTS:X", true)]
    [InlineData("dts-hd ma", true)]
    public void IsLosslessAudio_distinguishes_dts_variants(string? profile, bool expected) =>
        Assert.Equal(expected, LosslessAnalyzer.IsLosslessAudio("dts", profile));

    [Theory]
    [InlineData("truehd", "TrueHD + Dolby Atmos", true)]
    [InlineData("dts", "DTS-HD MA + DTS:X", true)]
    [InlineData("truehd", "TrueHD", false)]
    [InlineData("flac", null, false)]
    public void HasObjectAudio_detects_atmos_and_dtsx(string codec, string? profile, bool expected) =>
        Assert.Equal(expected, LosslessAnalyzer.HasObjectAudio(codec, profile));

    [Fact]
    public void FlacSizeFraction_saves_most_from_uncompressed_pcm()
    {
        var pcm = LosslessAnalyzer.FlacSizeFraction("pcm_s24le", null);
        var truehd = LosslessAnalyzer.FlacSizeFraction("truehd", null);

        Assert.NotNull(pcm);
        Assert.NotNull(truehd);
        Assert.True(pcm < truehd, "FLAC should save more from raw PCM than from TrueHD.");
        Assert.True(pcm > 0d && truehd < 1d);
    }

    [Fact]
    public void FlacSizeFraction_offers_nothing_for_already_lossy_or_already_flac()
    {
        Assert.Null(LosslessAnalyzer.FlacSizeFraction("flac", null));
        Assert.Null(LosslessAnalyzer.FlacSizeFraction("aac", null));
    }
}
