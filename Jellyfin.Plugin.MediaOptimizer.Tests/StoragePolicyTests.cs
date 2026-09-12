using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Where files are put decides how long a job takes: a rename inside one folder is instant, a
/// move to another disk copies every byte. On a 5 GB film that is minutes, twice over.
/// </summary>
public class StoragePolicyTests : IDisposable
{
    private readonly string _dir;

    public StoragePolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-storage-" + Guid.NewGuid().ToString("N"));
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
            // Test scratch space.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_writable_media_folder_is_detected()
    {
        Assert.True(OutputPolicyService.IsWritable(_dir));
        Assert.False(OutputPolicyService.IsWritable(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void A_kept_original_uses_an_extension_jellyfin_ignores()
    {
        // If the kept file ended in .mkv, Jellyfin would index it as a second copy of the film.
        Assert.EndsWith(".mooriginal", OutputPolicyService.OriginalSuffix, StringComparison.Ordinal);
        Assert.DoesNotContain("mkv", OutputPolicyService.OriginalSuffix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mp4", OutputPolicyService.OriginalSuffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Moving_within_one_directory_keeps_the_contents_intact()
    {
        var source = Path.Combine(_dir, "film.mkv");
        var destination = Path.Combine(_dir, "film.mkv" + OutputPolicyService.OriginalSuffix);
        File.WriteAllText(source, "the original bytes");

        OutputPolicyService.MoveAcrossVolumes(source, destination, overwrite: false);

        Assert.False(File.Exists(source));
        Assert.Equal("the original bytes", File.ReadAllText(destination));
    }

    [Fact]
    public void Moving_never_silently_overwrites_when_told_not_to()
    {
        var source = Path.Combine(_dir, "a.mkv");
        var destination = Path.Combine(_dir, "b.mkv");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "existing");

        Assert.ThrowsAny<IOException>(() =>
            OutputPolicyService.MoveAcrossVolumes(source, destination, overwrite: false));

        // The source must survive a refused move, or the file would simply be gone.
        Assert.True(File.Exists(source));
        Assert.Equal("existing", File.ReadAllText(destination));
    }

    /// <summary>
    /// A rename that fails because the destination is taken is a different problem from one that
    /// fails because the destination is on another disk, and both arrive as IOException. Copying
    /// several gigabytes and then failing the rename anyway is a slow way to learn which it was —
    /// and on a replace, the source is the user's only copy while that happens.
    /// </summary>
    [Fact]
    public void A_rename_blocked_by_an_existing_file_is_refused_without_copying_anything()
    {
        var source = Path.Combine(_dir, "source.mkv");
        var destination = Path.Combine(_dir, "taken.mkv");
        File.WriteAllText(source, "payload");
        File.WriteAllText(destination, "already here");

        Assert.Throws<IOException>(() =>
            OutputPolicyService.MoveAcrossVolumes(source, destination, overwrite: false));

        Assert.Equal("payload", File.ReadAllText(source));
        Assert.Equal("already here", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(_dir, SweepTask.WorkFilePattern));
    }

    [Fact]
    public void A_partial_copy_is_never_left_behind_under_the_destination_name()
    {
        var source = Path.Combine(_dir, "src.mkv");
        File.WriteAllText(source, "data");

        var destination = Path.Combine(_dir, "nested", "dest.mkv");
        OutputPolicyService.MoveAcrossVolumes(source, destination, overwrite: false);

        Assert.True(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, SweepTask.WorkFilePattern));
    }

    [Fact]
    public void Sidecar_names_do_not_collide_with_an_existing_output()
    {
        var taken = new[] { "/media/Film - Optimized.mkv" };
        var path = OutputPolicyService
            .BuildSidecarPath("/media/Film.mkv", string.Empty, "mkv", p => taken.Contains(p.Replace('\\', '/')))
            .Replace('\\', '/');

        Assert.Equal("/media/Film - Optimized (2).mkv", path);
    }

    [Fact]
    public void Working_files_are_hidden_and_carry_an_ignored_extension()
    {
        // The encode writes into the library folder to keep the final move a rename, so the
        // partial file must be invisible to both the user and Jellyfin's scanner.
        var jobId = Guid.NewGuid();
        var name = FormattableString.Invariant($".mo-{jobId:N}.mkv.motmp");

        Assert.StartsWith(".", name, StringComparison.Ordinal);
        Assert.EndsWith(".motmp", name, StringComparison.Ordinal);
        Assert.NotEqual(".mkv", Path.GetExtension(name));
    }

    /// <summary>
    /// A cross-volume move copies the whole file to a staging name first, and a power cut in the
    /// middle of that leaves the copy behind. It lands in the library folder, so it has to obey
    /// the same two rules as every other working file: invisible to Jellyfin's scanner, and
    /// removable by housekeeping. The old name — the destination with ".mopt-partial" glued on —
    /// was neither, so a 60 GB half-copy would have sat next to the film forever.
    /// </summary>
    [Fact]
    public void An_interrupted_copy_leaves_something_housekeeping_can_clean_up()
    {
        var staging = OutputPolicyService.StagingPathFor(Path.Combine(_dir, "Arrival (2016).mkv"));
        var name = Path.GetFileName(staging);

        Assert.Equal(_dir, Path.GetDirectoryName(staging));
        Assert.StartsWith(".mo-", name, StringComparison.Ordinal);
        Assert.EndsWith(".motmp", name, StringComparison.Ordinal);

        // Two moves to the same destination must not share a staging file.
        Assert.NotEqual(staging, OutputPolicyService.StagingPathFor(Path.Combine(_dir, "Arrival (2016).mkv")));

        // And the name is one the sweep actually matches, which is the part that makes it true.
        File.WriteAllText(staging, "half a film");
        Assert.Contains(staging, Directory.GetFiles(_dir, SweepTask.WorkFilePattern));
    }
}
