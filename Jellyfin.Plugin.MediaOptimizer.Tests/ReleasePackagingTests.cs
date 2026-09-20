using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The one failure that reaches every user at once.
/// <para>
/// Jellyfin verifies the MD5 in <c>manifest.json</c> when it downloads a plugin, and refuses the
/// install if it does not match the zip. So the manifest and the zip have to be produced together
/// — which is what <c>tools/package.sh</c> is for — and any commit that edits the version, edits
/// the manifest by hand, or rebuilds one without the other ships a repository that cannot be
/// installed from. Nothing about that is visible until somebody tries.
/// </para>
/// </summary>
public class ReleasePackagingTests
{
    private static string Root => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static JsonElement Entry()
    {
        var manifest = Path.Combine(Root, "manifest.json");
        Assert.True(File.Exists(manifest), "Could not find " + manifest);

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var plugin = doc.RootElement[0];

        // Cloned because the document is disposed with the using block.
        return JsonDocument.Parse(plugin.GetRawText()).RootElement;
    }

    private static string DeclaredVersion()
    {
        var build = File.ReadAllLines(Path.Combine(Root, "build.yaml"))
            .First(l => l.StartsWith("version:", StringComparison.Ordinal));

        return build.Split(':')[1].Trim().Trim('"');
    }

    /// <summary>The version in the manifest is the version the build declares.</summary>
    [Fact]
    public void The_manifest_describes_the_version_this_build_is()
    {
        var version = DeclaredVersion();
        var versions = Entry().GetProperty("versions");

        Assert.Equal(version, versions[0].GetProperty("version").GetString());
    }

    /// <summary>
    /// The checksum Jellyfin will compare against the download has to be the checksum of the zip
    /// actually committed here.
    /// </summary>
    [Fact]
    public void The_manifest_checksum_is_the_checksum_of_the_zip_in_the_repository()
    {
        var newest = Entry().GetProperty("versions")[0];
        var version = newest.GetProperty("version").GetString();
        var zip = Path.Combine(Root, "dist", FormattableString.Invariant($"media-optimizer_{version}.zip"));

        Assert.True(File.Exists(zip), "The manifest names a version with no zip beside it: " + zip);

        var actual = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(zip)))
            .ToLowerInvariant();

        Assert.Equal(newest.GetProperty("checksum").GetString(), actual);
    }

    /// <summary>
    /// The zip has to contain the plugin and the assembly has to be the version the release
    /// claims. A zip built from a stale bin directory installs happily and then reports the wrong
    /// version for ever.
    /// <para>
    /// It contains the assembly and nothing else, which is the shape Jellyfin expects from a
    /// repository install: the name, version and ABI come from the manifest entry, and Jellyfin
    /// writes its own <c>meta.json</c> beside the DLL when it unpacks it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_zip_contains_the_assembly_at_the_version_being_released()
    {
        var version = DeclaredVersion();
        var zip = Path.Combine(Root, "dist", FormattableString.Invariant($"media-optimizer_{version}.zip"));
        Assert.True(File.Exists(zip), "Could not find " + zip);

        using var archive = ZipFile.OpenRead(zip);
        var assembly = archive.GetEntry("Jellyfin.Plugin.MediaOptimizer.dll");
        Assert.NotNull(assembly);
        Assert.True(assembly!.Length > 100_000, "That assembly is too small to be this plugin.");

        // The version the DLL carries has to be the version being shipped, which is what Jellyfin
        // shows in My Plugins and what the next upgrade is compared against.
        var extracted = Path.Combine(Path.GetTempPath(), "mopt-release-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            assembly.ExtractToFile(extracted);
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(extracted);
            Assert.Equal(version, info.FileVersion);
        }
        finally
        {
            try
            {
                File.Delete(extracted);
            }
            catch (IOException)
            {
                // Scratch space.
            }
        }
    }

    /// <summary>
    /// The manifest points installs at a URL; if it points at a working branch, every install
    /// follows whatever is being developed there rather than the released file.
    /// </summary>
    [Fact]
    public void Every_version_in_the_manifest_points_at_its_own_file_on_one_branch()
    {
        var versions = Entry().GetProperty("versions");
        string? branch = null;

        foreach (var version in versions.EnumerateArray())
        {
            var url = version.GetProperty("sourceUrl").GetString();
            var number = version.GetProperty("version").GetString();

            Assert.NotNull(url);
            Assert.EndsWith(
                FormattableString.Invariant($"media-optimizer_{number}.zip"),
                url!,
                StringComparison.Ordinal);

            // ".../<owner>/<repo>/<branch...>/dist/<file>" — everything between the repository and
            // the dist folder is the branch, which may itself contain slashes.
            var start = url!.IndexOf("Jellyfin-resize/", StringComparison.Ordinal);
            var end = url.IndexOf("/dist/", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start, "Unexpected source URL shape: " + url);

            var thisBranch = url[(start + "Jellyfin-resize/".Length)..end];
            branch ??= thisBranch;

            Assert.Equal(branch, thisBranch);
        }
    }

    /// <summary>
    /// The changelog the plugin catalogue shows is the one in build.yaml, and it has to mention
    /// the version being released — an entry that describes the previous release is how a user
    /// installs an upgrade and cannot tell what changed.
    /// </summary>
    [Fact]
    public void The_changelog_describes_the_version_being_released()
    {
        var version = DeclaredVersion();
        var build = File.ReadAllText(Path.Combine(Root, "build.yaml"));

        Assert.Contains("### " + version, build, StringComparison.Ordinal);

        var manifestLog = Entry().GetProperty("versions")[0].GetProperty("changelog").GetString();
        Assert.False(string.IsNullOrWhiteSpace(manifestLog), "The manifest has no changelog for this version.");
    }
}
