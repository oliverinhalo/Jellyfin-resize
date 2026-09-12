using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Jobs;

/// <summary>
/// Housekeeping: expires quarantined originals and removes abandoned temporary files.
/// </summary>
public class SweepTask : IScheduledTask
{
    private readonly IJobStore _store;
    private readonly IOutputPolicyService _output;
    private readonly ILogger<SweepTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SweepTask"/> class.</summary>
    /// <param name="store">Job store.</param>
    /// <param name="output">Output policy service, for the managed directories.</param>
    /// <param name="logger">Logger.</param>
    public SweepTask(IJobStore store, IOutputPolicyService output, ILogger<SweepTask> logger)
    {
        _store = store;
        _output = output;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Optimizer: housekeeping";

    /// <inheritdoc />
    public string Key => "MediaOptimizerSweep";

    /// <inheritdoc />
    public string Description =>
        "Deletes quarantined original files once their retention period has passed, and clears temporary files left behind by interrupted encodes.";

    /// <inheritdoc />
    public string Category => "Media Optimizer";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    ];

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        ExpireQuarantine(cancellationToken);
        progress.Report(35);

        RemoveOldJobs(cancellationToken);
        progress.Report(60);

        CleanTempDirectory(cancellationToken);
        progress.Report(85);

        CleanOrphanedWorkFiles(cancellationToken);
        progress.Report(100);

        return Task.CompletedTask;
    }

    private void ExpireQuarantine(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expired = _store.GetAll()
            .Where(j => !string.IsNullOrEmpty(j.QuarantinePath) && j.QuarantineExpiresAt is not null && j.QuarantineExpiresAt <= now)
            .ToList();

        foreach (var job in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(job.QuarantinePath!))
                {
                    File.Delete(job.QuarantinePath!);
                    _logger.LogInformation(
                        "[MediaOptimizer] Deleted quarantined original for job {JobId} after {Days} day(s)",
                        job.Id,
                        (now - (job.FinishedAt ?? job.QueuedAt)).TotalDays.ToString("F0", CultureInfo.InvariantCulture));
                }

                job.QuarantinePath = null;
                job.QuarantineExpiresAt = null;
                _store.Update(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[MediaOptimizer] Could not delete quarantined file for job {JobId}", job.Id);
            }
        }
    }

    /// <summary>
    /// Removes finished jobs once they are older than the configured age, so the history does not
    /// have to be tidied by hand. A job still holding a restorable original is always kept.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private void RemoveOldJobs(CancellationToken cancellationToken)
    {
        var days = Plugin.Instance?.Configuration.RemoveFinishedJobsAfterDays ?? 30;
        if (days <= 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var removed = 0;

        foreach (var job in _store.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (job.IsActive || job.CanRevert)
            {
                continue;
            }

            if ((job.FinishedAt ?? job.QueuedAt) < cutoff && _store.Remove(job.Id))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("[MediaOptimizer] Removed {Count} finished job(s) older than {Days} days", removed, days);
        }
    }

    /// <summary>
    /// Deletes working files left in library folders by an encode that was killed outright, so a
    /// crash cannot leave hidden partial files sitting next to the media.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private void CleanOrphanedWorkFiles(CancellationToken cancellationToken)
    {
        var activeIds = _store.GetActive().Select(j => j.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = _store.GetAll()
            .Select(j => Path.GetDirectoryName(j.SourcePath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cutoff = DateTime.UtcNow.AddHours(-6);

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] leftovers;
            try
            {
                leftovers = Directory.GetFiles(directory!, ".mo-*.motmp");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var file in leftovers)
            {
                if (activeIds.Contains(JobIdFromWorkFileName(file)))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        _logger.LogInformation("[MediaOptimizer] Removed abandoned working file {Path}", file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "[MediaOptimizer] Could not delete working file {Path}", file);
                }
            }
        }
    }

    /// <summary>
    /// Reads the job id out of a working file name of the form ".mo-&lt;jobid&gt;.&lt;ext&gt;.motmp".
    /// <para>
    /// Getting this wrong is not cosmetic: the id is the only thing that marks a file as belonging
    /// to a job that is still running, and a file that fails to match is deleted once it has been
    /// untouched for six hours. A finished encode being verified with a deep decode scan writes
    /// nothing for exactly that long.
    /// </para>
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The job id in "N" form, or an empty string when the name does not carry one.</returns>
    internal static string JobIdFromWorkFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(".mo-", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return name[4..].Split('.')[0];
    }

    private void CleanTempDirectory(CancellationToken cancellationToken)
    {
        string tempDir;
        try
        {
            tempDir = _output.GetTempDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not open the working directory");
            return;
        }

        var activeIds = _store.GetActive().Select(j => j.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow.AddHours(-6);

        foreach (var file in Directory.EnumerateFiles(tempDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (activeIds.Contains(JobIdFromWorkFileName(file)))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.LogInformation("[MediaOptimizer] Removed abandoned temporary file {Path}", file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[MediaOptimizer] Could not delete temporary file {Path}", file);
            }
        }
    }
}
