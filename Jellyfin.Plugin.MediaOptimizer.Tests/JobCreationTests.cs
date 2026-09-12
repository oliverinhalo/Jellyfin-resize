using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Facts that every path queueing a job has to establish, checked by reading the source rather
/// than by exercising each path — because the failure is a field nobody set, and a job with a
/// field nobody set behaves plausibly right up to the point where it does the wrong thing to
/// somebody's file.
/// </summary>
public class JobCreationTests
{
    private static readonly string[] Sources =
    [
        "Jellyfin.Plugin.MediaOptimizer/Api/MediaOptimizerController.cs",
        "Jellyfin.Plugin.MediaOptimizer/Jobs/AutomationService.cs"
    ];

    /// <summary>Reads each block of source that constructs a job.</summary>
    /// <returns>The file it came from and the text of the construction.</returns>
    private static (string File, string Block)[] Constructions()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var blocks = new System.Collections.Generic.List<(string, string)>();

        foreach (var relative in Sources)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), "Could not find " + path);

            var text = File.ReadAllText(path);
            foreach (var construction in text.Split("new EncodeJob", StringSplitOptions.None).Skip(1))
            {
                blocks.Add((relative, construction[..Math.Min(construction.Length, 600)]));
            }
        }

        Assert.True(blocks.Count >= 3, "The job construction sites have moved; this test needs updating.");
        return blocks.ToArray();
    }

    /// <summary>
    /// The queue counts concurrency by weight, and a 4K job costs twice an ordinary one — so a job
    /// that does not know its own resolution is weighed as "unknown, assume expensive". Found the
    /// first time by writing this test: "Try again" built a fresh job without the height or size.
    /// </summary>
    [Fact]
    public void Every_way_of_queueing_a_job_records_the_resolution()
    {
        foreach (var (file, block) in Constructions())
        {
            Assert.True(
                block.Contains("SourceHeight", StringComparison.Ordinal),
                "A job is created in " + file + " without recording SourceHeight, so the queue "
                + "cannot tell how much of the machine it will take.");
        }
    }

    /// <summary>
    /// What happens to the original is carried on the job as well as inside its request, and the
    /// job's copy is the one that decides: whether the original is replaced, whether it is kept,
    /// whether the deep verification runs first. A creation site that forgets it gets the first
    /// value of the enum by default — so a user who asked for "replace" would quietly get a second
    /// file instead, or worse, the other way about.
    /// </summary>
    [Fact]
    public void Every_way_of_queueing_a_job_records_what_happens_to_the_original()
    {
        foreach (var (file, block) in Constructions())
        {
            Assert.True(
                block.Contains("OutputPolicy", StringComparison.Ordinal),
                "A job is created in " + file + " without recording OutputPolicy, so what happens "
                + "to the original file is whatever the enum's first value happens to be.");
        }
    }
}
