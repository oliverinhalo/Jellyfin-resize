using System;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// What a finished conversion says in Jellyfin's activity feed. This is the line most people will
/// actually read about a job — long after the dashboard was closed — so it has to lead with what
/// happened to their file rather than with a status word.
/// </summary>
public class NotificationTests
{
    private static EncodeJob Completed(long from, long to) => new EncodeJob
    {
        ItemName = "A Film (2019)",
        Status = JobStatus.Completed,
        SourceSizeBytes = from,
        OutputSizeBytes = to,
        OutputPolicy = OutputPolicy.Replace
    };

    [Fact]
    public void A_completed_job_leads_with_what_it_reclaimed()
    {
        var text = ActivityNotifier.DescribeCompletion(Completed(20L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));

        Assert.Contains("20.0 GiB", text, StringComparison.Ordinal);
        Assert.Contains("8.0 GiB", text, StringComparison.Ordinal);
        Assert.Contains("freeing 12.0 GiB", text, StringComparison.Ordinal);
        Assert.Contains("60%", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A conversion can legitimately produce a bigger file — a lossless repack of a lossy source,
    /// say. Reporting that as "freeing -2 GiB" would be nonsense, and quietly not reporting it
    /// would be worse.
    /// </summary>
    [Fact]
    public void A_job_that_produced_a_bigger_file_says_so()
    {
        var text = ActivityNotifier.DescribeCompletion(Completed(1L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024));

        Assert.Contains("larger", text, StringComparison.Ordinal);
        Assert.DoesNotContain("freeing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verified_lossless_job_says_that_it_was_verified()
    {
        var job = Completed(4L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024);
        job.IsLossless = true;
        job.LosslessVerified = true;

        Assert.Contains("Bit-exactness verified", ActivityNotifier.DescribeCompletion(job), StringComparison.Ordinal);
    }

    /// <summary>
    /// The undo window is the single most useful thing to know afterwards, and the feed entry may
    /// well be read days later.
    /// </summary>
    [Fact]
    public void A_replace_says_the_original_can_still_be_restored()
    {
        var job = Completed(4L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024);
        job.QuarantinePath = "/media/film.mkv.mooriginal";
        job.QuarantineExpiresAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var text = ActivityNotifier.DescribeCompletion(job);

        Assert.Contains("2026-07-01", text, StringComparison.Ordinal);
        Assert.Contains("restored", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sidecar or an alternate version keeps both files, so the disk went up by the size of the
    /// new one. Reporting that as "freeing 12 GiB" is not a rounding error, it is the opposite of
    /// what happened.
    /// </summary>
    [Theory]
    [InlineData(OutputPolicy.Sidecar)]
    [InlineData(OutputPolicy.AlternateVersion)]
    public void A_conversion_that_kept_the_original_never_claims_to_have_freed_anything(OutputPolicy policy)
    {
        var job = Completed(20L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024);
        job.OutputPolicy = policy;

        var text = ActivityNotifier.DescribeCompletion(job);

        Assert.DoesNotContain("freeing", text, StringComparison.Ordinal);
        Assert.Contains("alongside the original", text, StringComparison.Ordinal);
        Assert.Contains("8.0 GiB more disk", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replace_that_did_free_space_still_says_so()
    {
        var job = Completed(20L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024);
        job.OutputPolicy = OutputPolicy.ReplaceAndDelete;

        Assert.Contains("freeing 12.0 GiB", ActivityNotifier.DescribeCompletion(job), StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_with_no_sizes_still_says_something_true()
    {
        var job = new EncodeJob { ItemName = "A Film", Status = JobStatus.Completed, OutputPolicy = OutputPolicy.Sidecar };

        var text = ActivityNotifier.DescribeCompletion(job);

        Assert.Contains("Finished", text, StringComparison.Ordinal);
        Assert.Contains("Sidecar", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FFmpeg's own output runs to hundreds of lines. The feed holds one, and the first line is
    /// the one that names the cause — the same reason the job list leads with it.
    /// </summary>
    [Fact]
    public void A_failure_quotes_the_first_line_and_nothing_else()
    {
        var job = new EncodeJob
        {
            ItemName = "A Film",
            Status = JobStatus.Failed,
            Error = "FFmpeg failed: Could not write header for output file\n\nFull output: " + new string('x', 5000)
        };

        var text = ActivityNotifier.DescribeFailure(job);

        Assert.Contains("Could not write header", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxx", text, StringComparison.Ordinal);
        Assert.True(text.Length < 320, "A feed entry is one line: " + text.Length + " characters is not.");
    }

    /// <summary>Nobody should have to wonder whether a failed conversion damaged the file.</summary>
    [Fact]
    public void A_failure_always_says_the_original_is_untouched()
    {
        var job = new EncodeJob { ItemName = "A Film", Status = JobStatus.Failed, Error = "Not enough free space." };

        Assert.Contains("original file was not modified", ActivityNotifier.DescribeFailure(job), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_with_no_recorded_reason_says_that_too()
    {
        var job = new EncodeJob { ItemName = "A Film", Status = JobStatus.Failed };

        Assert.Contains("No reason was recorded", ActivityNotifier.DescribeFailure(job), StringComparison.Ordinal);
    }
}
