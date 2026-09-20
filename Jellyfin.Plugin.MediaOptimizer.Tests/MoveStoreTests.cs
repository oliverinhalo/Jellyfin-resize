using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Move;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// A move queue that forgot a job after a restart would leave a half-copied file on the
/// destination drive with nothing pointing at it, so the store is held to the same restart
/// guarantees as the conversion queue.
/// </summary>
public class MoveStoreTests : IDisposable
{
    private readonly string _dir;

    public MoveStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mopt-moves-" + Guid.NewGuid().ToString("N"));
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

    private MoveJobStore NewStore() => new MoveJobStore(_dir, NullLogger<MoveJobStore>.Instance);

    private static MoveJob Job(string name) => new MoveJob
    {
        ItemId = Guid.NewGuid(),
        ItemName = name,
        SourcePath = "/media/movies/" + name + ".mkv",
        DestinationRoot = "/mnt/disk2/movies",
        DestinationPath = "/mnt/disk2/movies/" + name + ".mkv",
        SizeBytes = 4L * 1024 * 1024 * 1024
    };

    [Fact]
    public void Queued_moves_survive_the_process_going_away()
    {
        var store = NewStore();
        store.Add(Job("Alpha"));
        store.Add(Job("Beta"));

        var reopened = NewStore();

        Assert.Equal(2, reopened.GetAll().Count);
        Assert.Contains(reopened.GetAll(), j => j.ItemName == "Alpha");
    }

    [Fact]
    public void Jobs_are_claimed_once_and_in_the_order_they_arrived()
    {
        var store = NewStore();
        var first = Job("Alpha");
        first.QueuedAt = DateTime.UtcNow.AddMinutes(-5);
        store.Add(first);
        store.Add(Job("Beta"));

        var claimed = store.TakeNextQueued();

        Assert.NotNull(claimed);
        Assert.Equal("Alpha", claimed!.ItemName);
        Assert.Equal(MoveStatus.Preflight, claimed.Status);
        Assert.Equal("Beta", store.TakeNextQueued()!.ItemName);
        Assert.Null(store.TakeNextQueued());
    }

    [Fact]
    public void A_move_interrupted_by_a_restart_is_held_for_review_rather_than_retried()
    {
        var store = NewStore();
        store.Add(Job("Alpha"));
        store.TakeNextQueued();

        var recovered = NewStore().ReconcileInterrupted();

        Assert.Single(recovered);
        Assert.Equal(MoveStatus.Interrupted, recovered[0].Status);
        Assert.Contains("original was left in place", recovered[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_already_queued_to_move_is_not_queued_twice()
    {
        var store = NewStore();
        var job = Job("Alpha");
        store.Add(job);

        Assert.True(store.HasActiveJobForItem(job.ItemId));
        Assert.True(store.IsDestinationClaimed(job.DestinationPath));

        job.Status = MoveStatus.Completed;
        store.Update(job);

        Assert.False(store.HasActiveJobForItem(job.ItemId));
        Assert.False(store.IsDestinationClaimed(job.DestinationPath));
    }

    [Fact]
    public void Progress_updates_are_visible_immediately_even_when_they_are_not_written_yet()
    {
        var store = NewStore();
        var job = Job("Alpha");
        store.Add(job);

        job.BytesCopied = 1024;
        job.ProgressPercent = 25d;
        store.ReportProgress(job);

        Assert.Equal(25d, store.Get(job.Id)!.ProgressPercent);
        Assert.Equal(1024, store.GetAll().Single().BytesCopied);
    }
}
