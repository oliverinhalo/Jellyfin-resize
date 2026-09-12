using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// How much encoding runs at once. Two 4K encodes are not two jobs, they are a server that has
/// stopped answering — so the limit is counted in ordinary jobs, and a 4K job counts as two.
/// </summary>
public class ConcurrencyPolicyTests
{
    [Theory]
    [InlineData(480, 1)]
    [InlineData(720, 1)]
    [InlineData(1080, 1)]
    [InlineData(1440, 2)]
    [InlineData(2160, 2)]
    [InlineData(4320, 2)]
    public void A_large_job_costs_twice_an_ordinary_one(int height, int expected)
    {
        Assert.Equal(expected, ConcurrencyPolicy.CostOf(height));
    }

    /// <summary>
    /// Guessing "cheap" for a file whose resolution is unknown and being wrong costs the server;
    /// guessing "expensive" and being wrong costs a little throughput.
    /// </summary>
    [Fact]
    public void An_unknown_resolution_is_assumed_to_be_the_expensive_kind()
    {
        Assert.Equal(2, ConcurrencyPolicy.CostOf(null));
    }

    /// <summary>
    /// The one inviolable rule. A weighting that made a 4K job permanently too expensive to start
    /// would be a queue that silently stops, which is worse than any amount of contention.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(-5)]
    public void An_idle_server_always_starts_the_next_job_whatever_it_weighs(int limit)
    {
        Assert.True(ConcurrencyPolicy.CanStart([], 2160, limit));
        Assert.True(ConcurrencyPolicy.CanStart([], null, limit));
    }

    [Fact]
    public void One_job_at_a_time_means_one_job_at_a_time()
    {
        Assert.False(ConcurrencyPolicy.CanStart([1080], 1080, 1));
        Assert.False(ConcurrencyPolicy.CanStart([2160], 720, 1));
    }

    [Fact]
    public void Two_ordinary_jobs_fit_where_one_large_one_does()
    {
        Assert.True(ConcurrencyPolicy.CanStart([1080], 1080, 2));
        Assert.False(ConcurrencyPolicy.CanStart([1080], 2160, 2));
        Assert.False(ConcurrencyPolicy.CanStart([2160], 1080, 2));
    }

    [Fact]
    public void A_larger_allowance_packs_more_in()
    {
        Assert.True(ConcurrencyPolicy.CanStart([1080, 1080], 1080, 4));
        Assert.True(ConcurrencyPolicy.CanStart([2160], 1080, 4));
        Assert.True(ConcurrencyPolicy.CanStart([2160], 2160, 4));
        Assert.True(ConcurrencyPolicy.CanStart([2160, 1080], 1080, 4));
        Assert.False(ConcurrencyPolicy.CanStart([2160, 1080], 2160, 4));
        Assert.False(ConcurrencyPolicy.CanStart([2160, 2160], 1080, 4));
    }

    /// <summary>
    /// A queue full of 4K films must not stop a 720p episode running in the room that is left,
    /// which is why the store skips a job that does not fit rather than blocking behind it.
    /// </summary>
    [Fact]
    public void A_small_job_runs_while_a_large_one_waits_for_room()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mopt-concurrency-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new JobStore(directory, NullLogger<JobStore>.Instance);

            var big = new EncodeJob { ItemName = "A 4K film", SourceHeight = 2160 };
            var small = new EncodeJob { ItemName = "An episode", SourceHeight = 720 };
            store.Add(big);
            store.Add(small);

            // One ordinary job is already running, with an allowance of two.
            var running = new List<int?> { 1080 };
            var taken = store.TakeNextQueued(job => ConcurrencyPolicy.CanStart(running, job.SourceHeight, 2));

            Assert.NotNull(taken);
            Assert.Equal("An episode", taken!.ItemName);

            // And the 4K film is still queued, not lost or cancelled.
            Assert.Equal(JobStatus.Queued, store.Get(big.Id)!.Status);

            // With nothing else running it is taken next, weight and all.
            var idle = new List<int?>();
            var next = store.TakeNextQueued(job => ConcurrencyPolicy.CanStart(idle, job.SourceHeight, 2));
            Assert.Equal("A 4K film", next!.ItemName);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // Scratch space.
            }
        }
    }

    /// <summary>Priority still decides, among the jobs that fit.</summary>
    [Fact]
    public void Priority_is_respected_within_what_fits()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mopt-concurrency-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new JobStore(directory, NullLogger<JobStore>.Instance);

            var ordinary = new EncodeJob { ItemName = "First in", SourceHeight = 1080 };
            var urgent = new EncodeJob { ItemName = "Run this next", SourceHeight = 1080, Priority = -1 };
            store.Add(ordinary);
            store.Add(urgent);

            var running = new List<int?> { 1080 };
            var taken = store.TakeNextQueued(job => ConcurrencyPolicy.CanStart(running, job.SourceHeight, 2));

            Assert.Equal("Run this next", taken!.ItemName);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // Scratch space.
            }
        }
    }

    // --- which job to start, not merely whether one can ------------------------------------

    private sealed record Queued(string Name, int Priority, DateTime QueuedAt, bool Fits);

    private static string? Choose(DateTime now, params Queued[] jobs) =>
        ConcurrencyPolicy.Choose(jobs, j => j.Fits, j => j.Priority, j => j.QueuedAt, now)?.Name;

    /// <summary>
    /// Taking the first job that fits is the obvious rule, and it silently inverts priority: a 4K
    /// job costs two slots, so "run this next" on one means it is passed over every single time
    /// while ordinary jobs keep arriving. That is not a scheduling nicety, it is the urgent job
    /// never running.
    /// </summary>
    [Fact]
    public void An_urgent_job_is_not_passed_over_by_ordinary_ones()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var chosen = Choose(
            now,
            new Queued("Urgent 4K", -1, now.AddMinutes(-1), Fits: false),
            new Queued("Ordinary 1080p", 0, now.AddMinutes(-1), Fits: true));

        Assert.Null(chosen);
    }

    /// <summary>
    /// But the room a large job cannot use should not sit empty either, so a job of the same
    /// urgency may still fill it.
    /// </summary>
    [Fact]
    public void A_job_of_equal_urgency_may_fill_the_space_a_large_one_cannot()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var chosen = Choose(
            now,
            new Queued("Big", 0, now.AddMinutes(-1), Fits: false),
            new Queued("Small", 0, now.AddMinutes(-1), Fits: true));

        Assert.Equal("Small", chosen);
    }

    /// <summary>
    /// And backfilling has to stop eventually, or a steady trickle of small jobs is a large one
    /// that never runs at all.
    /// </summary>
    [Fact]
    public void After_long_enough_the_queue_holds_room_for_the_job_at_the_front()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var waiting = now - ConcurrencyPolicy.HeadOfQueueGrace - TimeSpan.FromMinutes(1);

        var chosen = Choose(
            now,
            new Queued("Big, waiting", 0, waiting, Fits: false),
            new Queued("Small", 0, now.AddMinutes(-1), Fits: true));

        Assert.Null(chosen);
    }

    [Fact]
    public void The_job_at_the_front_is_taken_whenever_it_fits()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            "First",
            Choose(
                now,
                new Queued("First", 0, now.AddMinutes(-5), Fits: true),
                new Queued("Second", 0, now.AddMinutes(-1), Fits: true)));
    }

    [Fact]
    public void An_empty_queue_chooses_nothing()
    {
        Assert.Null(Choose(DateTime.UtcNow));
    }

    /// <summary>Every path that queues a job has to record the height, or the weighting is blind.</summary>
    [Fact]
    public void Every_way_of_queueing_a_job_records_the_resolution()
    {
        var sources = new[]
        {
            "Jellyfin.Plugin.MediaOptimizer/Api/MediaOptimizerController.cs",
            "Jellyfin.Plugin.MediaOptimizer/Jobs/AutomationService.cs"
        };

        var root = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        foreach (var relative in sources)
        {
            var path = System.IO.Path.Combine(root, relative);
            Assert.True(System.IO.File.Exists(path), "Could not find " + path);

            var text = System.IO.File.ReadAllText(path);
            var constructions = text.Split("new EncodeJob", StringSplitOptions.None).Skip(1).ToList();

            foreach (var construction in constructions)
            {
                var block = construction[..Math.Min(construction.Length, 600)];
                Assert.True(
                    block.Contains("SourceHeight", StringComparison.Ordinal),
                    "A job is created in " + relative + " without recording SourceHeight, so the "
                    + "queue cannot tell how much of the machine it will take.");
            }
        }
    }
}
