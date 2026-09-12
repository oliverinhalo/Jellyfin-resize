using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The queue has to survive the server being killed mid-job: that is the whole reason it is not
/// just an in-memory list.
/// </summary>
public class DurabilityTests : IDisposable
{
    private readonly string _dir;

    public DurabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-store-" + Guid.NewGuid().ToString("N"));
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

    private JobStore NewStore() => new JobStore(_dir, NullLogger<JobStore>.Instance);

    private static EncodeJob Job(string name, JobStatus status = JobStatus.Queued) => new EncodeJob
    {
        ItemId = Guid.NewGuid(),
        ItemName = name,
        SourcePath = "/media/" + name + ".mkv",
        Status = status,
        Request = new EncodeRequest()
    };

    [Fact]
    public void Jobs_survive_the_process_going_away()
    {
        var store = NewStore();
        store.Add(Job("Alpha"));
        store.Add(Job("Beta"));

        // No graceful shutdown: simply open the same directory again, as a restart would.
        var reopened = NewStore();

        Assert.Equal(2, reopened.GetAll().Count);
        Assert.Contains(reopened.GetAll(), j => j.ItemName == "Alpha");
        Assert.Contains(reopened.GetAll(), j => j.ItemName == "Beta");
    }

    [Fact]
    public void Deletes_survive_a_restart_too()
    {
        var store = NewStore();
        var job = Job("Gone");
        store.Add(job);
        store.Add(Job("Kept"));
        store.Remove(job.Id);

        var reopened = NewStore();
        Assert.Single(reopened.GetAll());
        Assert.Equal("Kept", reopened.GetAll()[0].ItemName);
    }

    [Fact]
    public void A_torn_final_record_does_not_lose_the_records_before_it()
    {
        var store = NewStore();
        store.Add(Job("First"));
        store.Add(Job("Second"));

        // Exactly what a power cut mid-append leaves behind: a truncated last line.
        var journal = Path.Combine(_dir, "queue.journal.jsonl");
        File.AppendAllText(journal, "{\"op\":\"put\",\"job\":{\"Id\":\"not-valid");

        var reopened = NewStore();
        Assert.Equal(2, reopened.GetAll().Count);
    }

    [Fact]
    public void A_corrupt_snapshot_still_leaves_the_journal_usable()
    {
        var store = NewStore();
        store.Add(Job("Recoverable"));

        File.WriteAllText(Path.Combine(_dir, "queue.snapshot.json"), "{ this is not json");

        var reopened = NewStore();
        Assert.Contains(reopened.GetAll(), j => j.ItemName == "Recoverable");
    }

    [Fact]
    public void Replay_applies_journal_records_over_the_snapshot_in_order()
    {
        var id = Guid.NewGuid();
        var snapshot = "[{\"Id\":\"" + id + "\",\"ItemName\":\"Old\",\"Status\":\"Queued\"}]";
        var journal = new[]
        {
            "{\"op\":\"put\",\"job\":{\"Id\":\"" + id + "\",\"ItemName\":\"New\",\"Status\":\"Completed\"}}"
        };

        var jobs = JobStore.Replay(snapshot, journal);

        Assert.Single(jobs);
        Assert.Equal("New", jobs[id].ItemName);
        Assert.Equal(JobStatus.Completed, jobs[id].Status);
    }

    [Fact]
    public void An_encode_interrupted_by_a_restart_is_requeued_automatically()
    {
        var store = NewStore();
        var job = Job("Interrupted", JobStatus.Encoding);
        job.ProgressPercent = 42;
        store.Add(job);

        var reopened = NewStore();
        var recovered = reopened.ReconcileInterrupted();

        Assert.Single(recovered);
        Assert.Equal(JobStatus.Queued, recovered[0].Status);
        Assert.Equal(0, recovered[0].ProgressPercent);
        Assert.Equal(1, recovered[0].ResumeCount);
        Assert.Null(recovered[0].Error);
    }

    [Fact]
    public void A_job_interrupted_while_moving_files_is_held_for_a_human()
    {
        // Applying is the only phase where the original may already have been moved aside, so
        // silently retrying it could act on a half-completed swap.
        var store = NewStore();
        store.Add(Job("MidSwap", JobStatus.Applying));

        var recovered = NewStore().ReconcileInterrupted();

        Assert.Single(recovered);
        Assert.Equal(JobStatus.Interrupted, recovered[0].Status);
        Assert.Contains("quarantine", recovered[0].Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Finished_jobs_are_left_alone_by_recovery()
    {
        var store = NewStore();
        store.Add(Job("Done", JobStatus.Completed));
        store.Add(Job("Broke", JobStatus.Failed));

        Assert.Empty(NewStore().ReconcileInterrupted());
    }

    [Fact]
    public void Compaction_keeps_every_job_and_shrinks_the_journal()
    {
        var store = NewStore();
        var job = Job("Busy");
        store.Add(job);

        // Comfortably past the compaction threshold.
        for (var i = 0; i < 500; i++)
        {
            job.ProgressPercent = i % 100;
            store.Update(job);
        }

        // The invariant is the record count, not the byte size: the journal is folded into the
        // snapshot once it passes the threshold, so it can never grow without bound.
        var journalLines = File.ReadAllLines(Path.Combine(_dir, "queue.journal.jsonl"))
            .Count(l => !string.IsNullOrWhiteSpace(l));
        var reopened = NewStore();

        Assert.Single(reopened.GetAll());
        Assert.Equal("Busy", reopened.GetAll()[0].ItemName);
        Assert.True(journalLines < 400, $"journal should have been compacted, held {journalLines} records");
        Assert.True(File.Exists(Path.Combine(_dir, "queue.snapshot.json")), "a snapshot should have been written");
    }

    [Fact]
    public void Higher_priority_jobs_are_claimed_first()
    {
        var store = NewStore();

        var normal = Job("Normal");
        var urgent = Job("Urgent");
        urgent.Priority = -1;
        store.Add(normal);
        store.Add(urgent);

        Assert.Equal("Urgent", store.TakeNextQueued()!.ItemName);
    }

    [Fact]
    public void A_paused_queue_hands_out_nothing()
    {
        var store = NewStore();
        store.Add(Job("Waiting"));

        store.IsPaused = true;
        try
        {
            Assert.Null(store.TakeNextQueued());
        }
        finally
        {
            store.IsPaused = false;
        }

        Assert.NotNull(store.TakeNextQueued());
    }

    /// <summary>
    /// Pausing is how an administrator stops the server encoding during the day. It used to live
    /// in a static field, so a restart silently resumed the queue -- and a restart is exactly what
    /// follows an upgrade, which is when someone is most likely to have paused it.
    /// </summary>
    [Fact]
    public void A_paused_queue_is_still_paused_after_a_restart()
    {
        var store = NewStore();
        store.Add(Job("Waiting"));
        store.IsPaused = true;

        var reopened = NewStore();

        Assert.True(reopened.IsPaused);
        Assert.Null(reopened.TakeNextQueued());

        reopened.IsPaused = false;
        Assert.False(NewStore().IsPaused);
    }

    [Fact]
    public void A_queue_from_the_previous_storage_format_is_carried_over()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(_dir, "jobs.json"),
            "[{\"Id\":\"" + id + "\",\"ItemName\":\"Legacy\",\"Status\":\"Completed\"}]");

        var store = NewStore();

        Assert.Contains(store.GetAll(), j => j.ItemName == "Legacy");
        Assert.True(File.Exists(Path.Combine(_dir, "jobs.json.migrated")));
    }
}
