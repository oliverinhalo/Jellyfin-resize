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
        progress.Report(50);

        CleanTempDirectory(cancellationToken);
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

            var name = Path.GetFileNameWithoutExtension(file);
            if (activeIds.Contains(name))
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
