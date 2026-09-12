using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Housekeeping deletes files. Everything it is allowed to delete has to be provably abandoned,
/// because the alternative is deleting the output of a job that is still running.
/// </summary>
public class SweepTaskTests : IDisposable
{
    private readonly string _root;
    private readonly string _work;
    private readonly string _store;

    public SweepTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mopt-sweep-" + Guid.NewGuid().ToString("N"));
        _work = Path.Combine(_root, "work");
        _store = Path.Combine(_root, "store");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch space.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A stub that only answers the one question housekeeping asks of it.</summary>
    private sealed class WorkDirectory : IOutputPolicyService
    {
        private readonly string _path;

        public WorkDirectory(string path) => _path = path;

        public string GetTempDirectory() => _path;

        public string GetWorkDirectoryFor(string sourcePath) => _path;

        public string GetQuarantineDirectory() => _path;

        public Task ApplyAsync(EncodeJob job, string tempOutputPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RevertAsync(EncodeJob job, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private string WriteStaleWorkFile(Guid jobId)
    {
        var path = Path.Combine(_work, FormattableString.Invariant($".mo-{jobId:N}.mkv.motmp"));
        File.WriteAllText(path, "partial");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-10));
        return path;
    }

    /// <summary>
    /// The file name carries the job id after a ".mo-" prefix, and the whole safety of the sweep
    /// rests on reading it back correctly.
    /// </summary>
    [Fact]
    public void The_job_id_is_read_back_out_of_a_working_file_name()
    {
        var id = Guid.NewGuid();

        Assert.Equal(
            id.ToString("N"),
            SweepTask.JobIdFromWorkFileName(Path.Combine("/work", FormattableString.Invariant($".mo-{id:N}.mkv.motmp"))));

        Assert.Equal(
            id.ToString("N"),
            SweepTask.JobIdFromWorkFileName(FormattableString.Invariant($".mo-{id:N}.probe.mp4.motmp")));

        Assert.Equal(string.Empty, SweepTask.JobIdFromWorkFileName("/work/something-else.mkv"));
    }

    /// <summary>
    /// A job in verification writes nothing while a deep decode scan reads its output, which on a
    /// large file takes longer than the six hours after which a working file is presumed
    /// abandoned. The id in the name is what says otherwise — and it was being read with a rule
    /// that never matched, so the nightly sweep would delete the finished encode out from under
    /// the job about to apply it.
    /// </summary>
    [Fact]
    public async Task An_active_jobs_working_file_is_never_swept_however_old_it_looks()
    {
        var store = new JobStore(_store, NullLogger<JobStore>.Instance);

        var running = new EncodeJob { ItemName = "Running", Status = JobStatus.Verifying };
        var finished = new EncodeJob { ItemName = "Finished", Status = JobStatus.Failed, FinishedAt = DateTime.UtcNow };
        store.Add(running);
        store.Add(finished);

        var keep = WriteStaleWorkFile(running.Id);
        var drop = WriteStaleWorkFile(finished.Id);

        var task = new SweepTask(store, new WorkDirectory(_work), NullLogger<SweepTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(File.Exists(keep), "The working file of a job that is still running was deleted.");
        Assert.False(File.Exists(drop), "A working file left behind by a finished job should be cleaned up.");
    }

    [Fact]
    public async Task A_recent_working_file_is_left_alone_even_with_no_job_at_all()
    {
        var store = new JobStore(_store, NullLogger<JobStore>.Instance);

        var recent = Path.Combine(_work, FormattableString.Invariant($".mo-{Guid.NewGuid():N}.mkv.motmp"));
        File.WriteAllText(recent, "partial");

        var task = new SweepTask(store, new WorkDirectory(_work), NullLogger<SweepTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(File.Exists(recent), "A file written minutes ago is not abandoned.");
    }
}
