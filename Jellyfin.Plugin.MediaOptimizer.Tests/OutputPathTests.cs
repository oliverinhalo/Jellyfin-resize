using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

public class OutputPathTests
{
    [Fact]
    public void Sidecar_path_sits_beside_the_original_by_default()
    {
        var path = OutputPolicyService.BuildSidecarPath(
            "/media/Movies/Dune (2021)/Dune.mkv",
            string.Empty,
            "mkv",
            _ => false);

        Assert.Equal("/media/Movies/Dune (2021)/Dune - Optimized.mkv", path.Replace('\\', '/'));
    }

    [Fact]
    public void Sidecar_path_honours_a_configured_directory()
    {
        var path = OutputPolicyService.BuildSidecarPath(
            "/media/Movies/Dune.mkv",
            "/optimized",
            "mp4",
            _ => false);

        Assert.Equal("/optimized/Dune - Optimized.mp4", path.Replace('\\', '/'));
    }

    [Fact]
    public void Sidecar_path_never_silently_overwrites_an_existing_file()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal)
        {
            "/media/Dune - Optimized.mkv",
            "/media/Dune - Optimized (2).mkv"
        };

        var path = OutputPolicyService
            .BuildSidecarPath("/media/Dune.mkv", string.Empty, "mkv", p => taken.Contains(p.Replace('\\', '/')))
            .Replace('\\', '/');

        Assert.Equal("/media/Dune - Optimized (3).mkv", path);
    }
}
