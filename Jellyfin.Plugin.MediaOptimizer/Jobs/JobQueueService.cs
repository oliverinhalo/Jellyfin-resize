using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Jobs;

/// <summary>Runs queued conversions in the background.</summary>
public interface IJobQueueService
{
    /// <summary>Cancels a running or queued job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>True when a job was found and cancelled.</returns>
    bool Cancel(Guid id);
}

/// <summary>The background worker that drains the job queue.</summary>
public class JobQueueService : BackgroundService, IJobQueueService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    private readonly IJobStore _store;
    private readonly IMediaProbeService _probe;
    private readonly IEncodePlanner _planner;
    private readonly ISizeEstimator _estimator;
    private readonly IFfmpegRunner _runner;
    private readonly IVerificationService _verifier;
    private readonly IOutputPolicyService _output;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<JobQueueService> _logger;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>Initializes a new instance of the <see cref="JobQueueService"/> class.</summary>
    /// <param name="store">Job store.</param>
    /// <param name="probe">Probe service.</param>
    /// <param name="planner">Encode planner.</param>
    /// <param name="estimator">Size estimator.</param>
    /// <param name="runner">FFmpeg runner.</param>
    /// <param name="verifier">Verification service.</param>
    /// <param name="output">Output policy service.</param>
    /// <param name="sessionManager">Session manager, for playback-aware pausing.</param>
    /// <param name="logger">Logger.</param>
    public JobQueueService(
        IJobStore store,
        IMediaProbeService probe,
        IEncodePlanner planner,
        ISizeEstimator estimator,
        IFfmpegRunner runner,
        IVerificationService verifier,
        IOutputPolicyService output,
        ISessionManager sessionManager,
        ILogger<JobQueueService> logger)
    {
        _store = store;
        _probe = probe;
        _planner = planner;
        _estimator = estimator;
        _runner = runner;
        _verifier = verifier;
        _output = output;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public bool Cancel(Guid id)
    {
        if (_running.TryGetValue(id, out var cts))
        {
            cts.Cancel();
            return true;
        }

        var job = _store.Get(id);
        if (job is null || !job.IsActive)
        {
            return false;
        }

        job.Status = JobStatus.Cancelled;
        job.FinishedAt = DateTime.UtcNow;
        _store.Update(job);
        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.ReconcileInterrupted();

        _logger.LogInformation("[MediaOptimizer] Job queue worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_running.Count >= Math.Max(1, Config.MaxConcurrentJobs))
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (Config.PauseWhilePlaybackActive && IsPlaybackActive())
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var job = _store.TakeNextQueued();
                if (job is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _running[job.Id] = cts;

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await RunJobAsync(job, cts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            _running.TryRemove(job.Id, out _);
                            cts.Dispose();
                        }
                    },
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
#pragma warning disable CA1031 // The worker loop must never die on one bad job.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "[MediaOptimizer] Unexpected error in the queue loop");
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("[MediaOptimizer] Job queue worker stopped");
    }

    private async Task RunJobAsync(EncodeJob job, CancellationToken cancellationToken)
    {
        var tempPath = string.Empty;

        try
        {
            var analysis = await _probe.AnalyzeAsync(job.ItemId, cancellationToken).ConfigureAwait(false);
            if (analysis is null)
            {
                Fail(job, "The library item no longer exists.");
                return;
            }

            var preflightError = Preflight(job, analysis);
            if (preflightError is not null)
            {
                Fail(job, preflightError);
                return;
            }

            job.SourcePath = analysis.Path;
            job.SourceSizeBytes = analysis.SizeBytes;

            // Re-check the container against what this file actually contains, every time the job
            // runs. A queued job carries the settings it was created with, so a job queued before
            // this check existed -- or requeued with "Try again" -- would otherwise replay a
            // container that cannot hold the file and fail exactly as it did the first time.
            var containerBefore = job.Request.Container;
            StrategyResolver.ApplyContainerCompatibility(analysis, job.Request);
            if (!string.Equals(containerBefore, job.Request.Container, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[MediaOptimizer] Job {JobId} moved from {Before} to {After}: {Reason}",
                    job.Id,
                    containerBefore,
                    job.Request.Container,
                    job.Request.ContainerSwitchReason);
            }

            // Encoding into the media folder makes the final move a rename rather than a copy of
            // the whole file, which on a multi-gigabyte film is the difference between instant and
            // several minutes. The suffix keeps Jellyfin's scanner away from the partial file.
            var tempDir = _output.GetWorkDirectoryFor(analysis.Path);
            var extension = job.Request.Container.Trim().TrimStart('.').ToLowerInvariant();
            tempPath = Path.Combine(tempDir, FormattableString.Invariant($".mo-{job.Id:N}.{extension}.motmp"));

            var plan = await _planner
                .PlanAsync(analysis, job.Request, tempPath, cancellationToken)
                .ConfigureAwait(false);

            job.Warnings = plan.Warnings;
            job.IsLossless = plan.IsLossless;

            if (!plan.IsRunnable)
            {
                var blocker = plan.Warnings.First(w => w.Level == WarningLevel.Blocker);
                Fail(job, blocker.Message);
                return;
            }

            // Re-derive the temp path in case the planner normalised the container.
            if (!string.Equals(extension, plan.OutputExtension, StringComparison.OrdinalIgnoreCase))
            {
                tempPath = Path.Combine(tempDir, FormattableString.Invariant($".mo-{job.Id:N}.{plan.OutputExtension}.motmp"));
                plan = await _planner
                    .PlanAsync(analysis, job.Request, tempPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            var estimate = _estimator.Estimate(analysis, job.Request, plan);
            var spaceError = CheckFreeSpace(tempDir, estimate.EstimatedSizeHighBytes);
            if (spaceError is not null)
            {
                Fail(job, spaceError);
                return;
            }

            // Half a second of the real encode, through the real muxer. FFmpeg validates the whole
            // output configuration when it writes the header, so anything the container cannot
            // hold fails here in a second instead of after hours of encoding.
            var dryRunError = await MuxDryRunAsync(plan.Arguments, tempDir, job.Id, cancellationToken)
                .ConfigureAwait(false);
            if (dryRunError is not null)
            {
                Fail(job, dryRunError);
                return;
            }

            job.Status = JobStatus.Encoding;
            _store.Update(job);

            _logger.LogInformation(
                "[MediaOptimizer] Job {JobId} encoding {Name}: ffmpeg {Args}",
                job.Id,
                job.ItemName,
                string.Join(' ', plan.Arguments));

            var lastPersist = DateTime.UtcNow;
            var result = await _runner.RunEncodeAsync(
                plan.Arguments,
                analysis.DurationSeconds,
                progress =>
                {
                    job.ProgressPercent = progress.Percent ?? 0d;
                    job.Speed = progress.Speed;
                    job.EtaSeconds = progress.EtaSeconds;

                    // Persisting every sample would rewrite the store several times a second.
                    if (DateTime.UtcNow - lastPersist > TimeSpan.FromSeconds(3))
                    {
                        lastPersist = DateTime.UtcNow;
                        _store.Update(job);
                    }
                },
                Config.LowProcessPriority,
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                // Lead with the line that names the cause. FFmpeg prints it first and then floods
                // stderr with thread teardown, so a plain tail cuts off the only useful sentence
                // and leaves a message nobody can act on.
                Fail(
                    job,
                    "FFmpeg failed: " + Summarise(result.StandardError)
                    + "\n\nFull output: " + Tail(result.StandardError));
                TryDelete(tempPath);
                return;
            }

            job.Status = JobStatus.Verifying;
            job.ProgressPercent = 100d;
            _store.Update(job);

            var deepScan = Config.DeepVerifyBeforeReplace
                && job.OutputPolicy is OutputPolicy.Replace or OutputPolicy.ReplaceAndDelete;
            var verification = await _verifier.VerifyAsync(
                analysis.Path,
                tempPath,
                analysis.DurationSeconds,
                deepScan,
                plan.LosslessAudioIndexes,
                cancellationToken).ConfigureAwait(false);

            job.LosslessVerified = verification.LosslessVerified;

            if (!verification.Passed)
            {
                Fail(job, verification.FailureReason ?? "The produced file failed verification.");
                TryDelete(tempPath);
                return;
            }

            job.Status = JobStatus.Applying;
            _store.Update(job);

            await _output.ApplyAsync(job, tempPath, cancellationToken).ConfigureAwait(false);

            job.Status = JobStatus.Completed;
            job.FinishedAt = DateTime.UtcNow;
            job.PixelsPerSecond = MeasureThroughput(job, analysis);
            _store.Update(job);

            _logger.LogInformation(
                "[MediaOptimizer] Job {JobId} completed: {Source} bytes -> {Output} bytes",
                job.Id,
                job.SourceSizeBytes,
                job.OutputSizeBytes);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = DateTime.UtcNow;
            job.Error = "Cancelled. The original file was not modified.";
            _store.Update(job);
            TryDelete(tempPath);
            _logger.LogInformation("[MediaOptimizer] Job {JobId} cancelled", job.Id);
        }
#pragma warning disable CA1031 // A failed job must be recorded, not thrown out of the worker.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "[MediaOptimizer] Job {JobId} failed", job.Id);
            Fail(job, ex.Message);
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Checks everything that must be true before a single frame is encoded.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="analysis">Fresh analysis of the source.</param>
    /// <returns>An error message, or null when the job may proceed.</returns>
    /// <summary>
    /// Runs the planned arguments against half a second of the source, into a throwaway file.
    /// <para>
    /// This is the last line of defence against a plan FFmpeg will not accept. It caught nothing
    /// the planner already checks; it exists for the mistakes nobody has thought of yet, because
    /// the alternative is a job that burns an hour and then reports a muxer error.
    /// </para>
    /// </summary>
    /// <param name="arguments">The planned arguments, ending in <c>-y &lt;output&gt;</c>.</param>
    /// <param name="workDirectory">Where to put the throwaway output.</param>
    /// <param name="jobId">The job, used to name the throwaway file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An error message, or <c>null</c> when the configuration is valid.</returns>
    private async Task<string?> MuxDryRunAsync(
        IReadOnlyList<string> arguments,
        string workDirectory,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 2)
        {
            return null;
        }

        var realOutput = arguments[^1];
        var extension = Path.GetExtension(realOutput.Replace(".motmp", string.Empty, StringComparison.OrdinalIgnoreCase));

        // Same naming rules as the real temp file: leading dot and .motmp suffix, so the library
        // scanner never sees it even if the delete below is somehow missed.
        var probePath = Path.Combine(
            workDirectory,
            FormattableString.Invariant($".mo-{jobId:N}.probe{extension}.motmp"));

        var probeArgs = new List<string>(arguments.Count + 3);
        // Everything up to the trailing "-y <output>".
        for (var i = 0; i < arguments.Count - 2; i++)
        {
            probeArgs.Add(arguments[i]);
        }

        probeArgs.Add("-t");
        probeArgs.Add("0.5");
        probeArgs.Add("-y");
        probeArgs.Add(probePath);

        try
        {
            var result = await _runner
                .RunEncodeAsync(probeArgs, 0.5d, null, true, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return null;
            }

            _logger.LogWarning(
                "[MediaOptimizer] Job {JobId} failed its dry run: {Error}",
                jobId,
                result.StandardError);

            return "FFmpeg rejected these settings before encoding started, so nothing was wasted. "
                + Summarise(result.StandardError)
                + " Switching the container to MKV resolves most cases, because it can store formats MP4 cannot.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The dry run is a safety net, not a gate. If it cannot even write its scratch file,
            // let the real encode run and report the real problem.
            _logger.LogWarning(ex, "[MediaOptimizer] Job {JobId} could not run its dry run; continuing", jobId);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "[MediaOptimizer] Could not remove dry-run file {Path}", probePath);
            }
        }
    }

    /// <summary>
    /// Pulls the line that actually says what went wrong out of FFmpeg's output, so the job list
    /// shows a sentence rather than fifty lines of encoder banner.
    /// </summary>
    /// <param name="stderr">FFmpeg's standard error.</param>
    /// <returns>A short description.</returns>
    internal static string Summarise(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "FFmpeg gave no reason.";
        }

        string[] markers =
        [
            "Could not find tag for codec",
            "is not supported in container",
            "Could not write header",
            "Unsupported codec",
            "Invalid data found",
            "No such file or directory",
            "Permission denied",
            "Error initializing",
            "Unknown encoder",
        ];

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hit = lines.FirstOrDefault(l => markers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
        var line = hit ?? lines.LastOrDefault(l => !l.StartsWith("frame=", StringComparison.Ordinal)) ?? stderr;

        // Strip FFmpeg's "[mp4 @ 0x7f...]" component prefix; the address means nothing to anyone.
        var close = line.IndexOf("] ", StringComparison.Ordinal);
        if (line.StartsWith('[') && close > 0 && close + 2 < line.Length)
        {
            line = line[(close + 2)..];
        }

        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private string? Preflight(EncodeJob job, FileAnalysis analysis)
    {
        if (!analysis.IsEligible)
        {
            return analysis.IneligibleReason ?? "This item cannot be converted.";
        }

        if (string.IsNullOrEmpty(analysis.Path) || !File.Exists(analysis.Path))
        {
            return "The source file no longer exists on disk.";
        }

        var stability = Config.FileStabilitySeconds;
        if (stability > 0)
        {
            var lastWrite = File.GetLastWriteTimeUtc(analysis.Path);
            if (DateTime.UtcNow - lastWrite < TimeSpan.FromSeconds(stability))
            {
                return FormattableString.Invariant(
                    $"The source file was modified less than {stability} seconds ago and may still be downloading.");
            }
        }

        if (IsItemPlaying(job.ItemId))
        {
            return "Someone is currently playing this item.";
        }

        if (job.OutputPolicy != OutputPolicy.Sidecar && !analysis.IsWritable)
        {
            return "The library folder is not writable, so the file cannot be replaced or a version added.";
        }

        return null;
    }

    private string? CheckFreeSpace(string directory, long estimatedBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            var required = (long)(estimatedBytes * Math.Max(1d, Config.FreeSpaceSafetyFactor));

            if (drive.AvailableFreeSpace < required)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Not enough free space: {0:N0} MB available, about {1:N0} MB needed.",
                    drive.AvailableFreeSpace / 1024 / 1024,
                    required / 1024 / 1024);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not check free space for {Directory}", directory);
        }

        return null;
    }

    private bool IsPlaybackActive()
    {
        try
        {
            return _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read active sessions");
            return false;
        }
    }

    private bool IsItemPlaying(Guid itemId)
    {
        try
        {
            return _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null && s.NowPlayingItem.Id == itemId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read active sessions");
            return false;
        }
    }

    /// <summary>
    /// Records how many output pixels this server encoded per second, so the next job's time
    /// estimate is based on measured hardware rather than a guess.
    /// </summary>
    /// <param name="job">The finished job.</param>
    /// <param name="analysis">The source analysis.</param>
    /// <returns>Pixels per second, or null when it cannot be derived.</returns>
    private static double? MeasureThroughput(EncodeJob job, FileAnalysis analysis)
    {
        if (job.Request.Video != VideoAction.Encode
            || job.StartedAt is null
            || job.FinishedAt is null
            || analysis.Video is null
            || analysis.DurationSeconds is not > 0)
        {
            return null;
        }

        var elapsed = (job.FinishedAt.Value - job.StartedAt.Value).TotalSeconds;
        if (elapsed <= 1d)
        {
            return null;
        }

        var sourceHeight = analysis.Video.Height ?? 1080;
        var sourceWidth = analysis.Video.Width ?? 1920;
        var targetHeight = EncodePlanner.ResolveTargetHeight(sourceWidth, sourceHeight, job.Request.TargetHeight)
            ?? sourceHeight;
        var targetWidth = sourceHeight > 0 ? sourceWidth * targetHeight / sourceHeight : sourceWidth;
        var fps = analysis.Video.FrameRate ?? 24f;

        var pixels = (double)targetWidth * targetHeight * fps * analysis.DurationSeconds.Value;
        return pixels / elapsed;
    }

    private void Fail(EncodeJob job, string error)
    {
        job.Status = JobStatus.Failed;

        // Stamp the build. Without it there is no way to tell a failure on the current version
        // from one produced by an older version still installed, which is exactly the question
        // asked first when the same error is reported twice.
        var version = Plugin.Instance?.Version?.ToString();
        job.Error = version is null
            ? error
            : FormattableString.Invariant($"{error} (Media Optimizer {version})");

        job.FinishedAt = DateTime.UtcNow;
        _store.Update(job);
    }

    private void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not delete temporary file {Path}", path);
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= 500)
        {
            return trimmed;
        }

        return "…" + trimmed[^500..];
    }
}
