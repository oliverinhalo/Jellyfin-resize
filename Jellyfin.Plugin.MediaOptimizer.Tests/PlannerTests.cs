using Jellyfin.Plugin.MediaOptimizer.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

public class PlannerTests
{
    [Fact]
    public void ResolveTargetHeight_never_upscales()
    {
        // Asking for 4K from a 1080p source must keep the source resolution, not inflate it.
        Assert.Null(EncodePlanner.ResolveTargetHeight(1920, 1080, 2160));
        Assert.Null(EncodePlanner.ResolveTargetHeight(1920, 1080, 1080));
    }

    [Fact]
    public void ResolveTargetHeight_downscales_when_asked()
    {
        Assert.Equal(720, EncodePlanner.ResolveTargetHeight(3840, 2160, 720));
        Assert.Equal(1080, EncodePlanner.ResolveTargetHeight(3840, 2160, 1080));
    }

    [Fact]
    public void ResolveTargetHeight_keeps_the_height_even()
    {
        // Odd heights are rejected by most encoders' chroma subsampling.
        var height = EncodePlanner.ResolveTargetHeight(1920, 1080, 721);
        Assert.NotNull(height);
        Assert.Equal(0, height!.Value % 2);
    }

    [Fact]
    public void ResolveTargetHeight_handles_unknown_source_dimensions() =>
        Assert.Null(EncodePlanner.ResolveTargetHeight(null, null, 1080));

    [Theory]
    [InlineData(10, "yuv420p", "yuv420p10le")]
    [InlineData(8, "yuv420p", "yuv420p")]
    [InlineData(10, "yuv444p10le", "yuv444p10le")]
    [InlineData(8, "yuv422p", "yuv422p")]
    public void PixelFormatFor_preserves_chroma_subsampling(int depth, string source, string expected) =>
        Assert.Equal(expected, EncodePlanner.PixelFormatFor(depth, source));
}
