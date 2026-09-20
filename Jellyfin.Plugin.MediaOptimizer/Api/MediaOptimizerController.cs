using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// The plugin's API. Reads are available to any signed-in user when the admin allows it;
/// anything that starts, cancels or reverses a conversion requires an administrator, because
/// these operations permanently rewrite files in the library.
/// </summary>
[ApiController]
[Authorize]
[Route("MediaOptimizer")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaOptimizerController : ControllerBase
{
    private readonly IMediaProbeService _probe;
    private readonly ICapabilityService _capabilities;
    private readonly IEncodePlanner _planner;
    private readonly ISizeEstimator _estimator;
    private readonly IJobStore _store;
    private readonly IJobQueueService _queue;
    private readonly IOutputPolicyService _output;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<MediaOptimizerController> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaOptimizerController"/> class.</summary>
    /// <param name="probe">Probe service.</param>
    /// <param name="capabilities">Capability service.</param>
    /// <param name="planner">Encode planner.</param>
    /// <param name="estimator">Size estimator.</param>
    /// <param name="store">Job store.</param>
    /// <param name="queue">Job queue.</param>
    /// <param name="output">Output policy service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager, for stream metadata.</param>
    /// <param name="userManager">User manager, for resolving the caller.</param>
    /// <param name="userDataManager">User data manager, for watched state.</param>
    /// <param name="logger">Logger.</param>
    public MediaOptimizerController(
        IMediaProbeService probe,
        ICapabilityService capabilities,
        IEncodePlanner planner,
        ISizeEstimator estimator,
        IJobStore store,
        IJobQueueService queue,
        IOutputPolicyService output,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<MediaOptimizerController> logger)
    {
        _probe = probe;
        _capabilities = capabilities;
        _planner = planner;
        _estimator = estimator;
        _store = store;
        _queue = queue;
        _output = output;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    private bool IsAdmin => User.IsInRole("Administrator");

    /// <summary>
    /// Lists convertible library items with sorting and filtering, so a conversion can be started
    /// from the dashboard without depending on the injected in-app UI.
    /// </summary>
    /// <param name="query">Optional search text.</param>
    /// <param name="sort">Ordering.</param>
    /// <param name="watched">Watched-state filter for the calling user.</param>
    /// <param name="minHeight">Only include items at least this tall.</param>
    /// <param name="minSizeMb">Only include files at least this large.</param>
    /// <param name="container">Only include this container extension.</param>
    /// <param name="codec">Only include this video codec.</param>
    /// <param name="location">Only include files living under this folder.</param>
    /// <param name="limit">Maximum results returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching items.</returns>
    [HttpGet("Library/Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LibraryItemSummary>>> SearchLibrary(
        [FromQuery] string? query,
        [FromQuery] LibrarySort sort = LibrarySort.SizeDescending,
        [FromQuery] WatchedFilter watched = WatchedFilter.Any,
        [FromQuery] int? minHeight = null,
        [FromQuery] long? minSizeMb = null,
        [FromQuery] string? container = null,
        [FromQuery] string? codec = null,
        [FromQuery] string? location = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var itemQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false,
            // Scan a wider set than we return, because size, resolution and codec all live in the
            // media streams rather than in the item query, so they can only be filtered after.
            Limit = Math.Clamp(limit, 1, 200) * 12,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)]
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            itemQuery.SearchTerm = query.Trim();
        }

        var user = await GetCallingUserAsync().ConfigureAwait(false);
        var results = new List<LibraryItemSummary>();

        foreach (var item in _libraryManager.GetItemList(itemQuery))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            // Used by the move page to ask "what is on this drive?", which the item query itself
            // cannot answer: Jellyfin indexes items by library, not by the folder they sit in.
            if (!string.IsNullOrWhiteSpace(location) && !Move.MovePathPlanner.IsUnder(item.Path, location))
            {
                continue;
            }

            var summary = BuildSummary(item, user);

            if (minHeight is > 0 && (summary.Height ?? 0) < minHeight.Value)
            {
                continue;
            }

            if (minSizeMb is > 0 && (summary.SizeBytes ?? 0) < minSizeMb.Value * 1024 * 1024)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(container)
                && !string.Equals(summary.Container, container, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(codec)
                && !string.Equals(summary.VideoCodec, codec, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (watched == WatchedFilter.Watched && summary.IsWatched != true)
            {
                continue;
            }

            if (watched == WatchedFilter.Unwatched && summary.IsWatched != false)
            {
                continue;
            }

            results.Add(summary);
        }

        IEnumerable<LibraryItemSummary> ordered = sort switch
        {
            LibrarySort.SizeAscending => results.OrderBy(r => r.SizeBytes ?? long.MaxValue),
            LibrarySort.Name => results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySort.ResolutionDescending => results.OrderByDescending(r => r.Height ?? 0).ThenByDescending(r => r.SizeBytes ?? 0),
            LibrarySort.DateAdded => results,
            LibrarySort.BitrateDescending => results.OrderByDescending(r => r.BitrateBps ?? 0),
            _ => results.OrderByDescending(r => r.SizeBytes ?? 0)
        };

        return Ok(ordered.Take(Math.Clamp(limit, 1, 200)).ToList());
    }

    /// <summary>
    /// Reports the distinct containers and codecs present, so the filter dropdowns only offer
    /// values that actually exist in this library.
    /// </summary>
    /// <returns>Available filter values.</returns>
    [HttpGet("Library/Facets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetFacets()
    {
        var itemQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false,
            Limit = 400
        };

        var containers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var codecs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _libraryManager.GetItemList(itemQuery))
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            var extension = System.IO.Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(extension))
            {
                containers.Add(extension);
            }

            var video = _mediaSourceManager.GetMediaStreams(item.Id)
                .FirstOrDefault(x => x.Type == MediaStreamType.Video);
            if (!string.IsNullOrEmpty(video?.Codec))
            {
                codecs.Add(video.Codec);
            }
        }

        return Ok(new { containers = containers.ToList(), codecs = codecs.ToList() });
    }

    /// <summary>
    /// Turns a named strategy into concrete settings for one file. The dialog, the batch runner
    /// and the estimate all go through this, so a preset means exactly one thing.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="strategy">The strategy to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fully populated request.</returns>
    [HttpGet("ResolveStrategy/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EncodeRequest>> ResolveStrategy(
        [FromRoute] Guid itemId,
        [FromQuery] OptimizationStrategy strategy,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        return Ok(StrategyResolver.Resolve(analysis, strategy, caps, config));
    }

    /// <summary>Applies one strategy to many items at once.</summary>
    /// <param name="request">The items and the strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-item outcomes.</returns>
    [HttpPost("Jobs/Batch")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<BatchResult>> CreateBatch(
        [FromBody] BatchRequest request,
        CancellationToken cancellationToken)
    {
        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        var items = new List<BatchItemResult>();
        long totalSaving = 0;
        var queued = 0;

        foreach (var itemId in request.ItemIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = new BatchItemResult { ItemId = itemId };
            var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);

            if (analysis is null)
            {
                outcome.SkippedReason = "The item no longer exists.";
                items.Add(outcome);
                continue;
            }

            outcome.Name = analysis.Name;

            if (!analysis.IsEligible)
            {
                outcome.SkippedReason = analysis.IneligibleReason;
                items.Add(outcome);
                continue;
            }

            if (_store.HasActiveJobForItem(itemId))
            {
                outcome.SkippedReason = "Already queued.";
                items.Add(outcome);
                continue;
            }

            // A batch-level language choice overrides the plugin default for this run only.
            var effectiveConfig = config;
            if (request.KeepAudioLanguages is not null || request.KeepSubtitleLanguages is not null)
            {
                effectiveConfig = CloneWithLanguages(config, request.KeepAudioLanguages, request.KeepSubtitleLanguages);
            }

            var encodeRequest = StrategyResolver.Resolve(analysis, request.Strategy, caps, effectiveConfig);

            // Batch-wide overrides, applied after the per-file strategy so an explicit choice wins.
            if (request.TargetHeight is > 0)
            {
                encodeRequest.TargetHeight = request.TargetHeight;
            }

            if (!string.IsNullOrWhiteSpace(request.Container))
            {
                // A batch container applies to files it can actually hold. Re-running the
                // compatibility pass puts any file back on MKV rather than queuing a job that
                // would fail, or one that would silently drop its subtitles.
                encodeRequest.Container = request.Container;
                StrategyResolver.ApplyContainerCompatibility(analysis, encodeRequest);
            }

            if (request.OutputPolicy is not null)
            {
                encodeRequest.OutputPolicy = request.OutputPolicy.Value;
            }

            encodeRequest.UseHardware = request.UseHardware;
            encodeRequest.AcceptDolbyVisionLoss = request.AcceptDolbyVisionLoss;

            var plan = await _planner
                .PlanAsync(analysis, encodeRequest, "/dev/null", cancellationToken)
                .ConfigureAwait(false);

            if (!plan.IsRunnable)
            {
                outcome.SkippedReason = plan.Warnings.First(w => w.Level == WarningLevel.Blocker).Message;
                items.Add(outcome);
                continue;
            }

            var estimate = _estimator.Estimate(analysis, encodeRequest, plan);

            var job = new EncodeJob
            {
                ItemId = itemId,
                ItemName = analysis.Name,
                SourcePath = analysis.Path,
                SourceSizeBytes = analysis.SizeBytes,
                Request = encodeRequest,
                OutputPolicy = encodeRequest.OutputPolicy,
                Warnings = plan.Warnings,
                IsLossless = plan.IsLossless
            };

            _store.Add(job);

            outcome.Queued = true;
            outcome.JobId = job.Id;
            outcome.EstimatedSavingBytes = estimate.CurrentSizeBytes - estimate.EstimatedSizeBytes;
            totalSaving += outcome.EstimatedSavingBytes ?? 0;
            queued++;
            items.Add(outcome);
        }

        _logger.LogInformation(
            "[MediaOptimizer] Batch queued {Queued} job(s), skipped {Skipped}",
            queued,
            items.Count - queued);

        return Ok(new BatchResult
        {
            QueuedCount = queued,
            SkippedCount = items.Count - queued,
            EstimatedSavingBytes = totalSaving,
            Items = items
        });
    }

    /// <summary>
    /// Copies the configuration with different language keep-lists, so a batch override never
    /// mutates the saved settings.
    /// </summary>
    /// <param name="source">The saved configuration.</param>
    /// <param name="audio">Audio languages to keep, or null to leave unchanged.</param>
    /// <param name="subtitles">Subtitle languages to keep, or null to leave unchanged.</param>
    /// <returns>A copy carrying the overrides.</returns>
    private static Configuration.PluginConfiguration CloneWithLanguages(
        Configuration.PluginConfiguration source,
        string? audio,
        string? subtitles)
    {
        return new Configuration.PluginConfiguration
        {
            Injection = source.Injection,
            DefaultOutputPolicy = source.DefaultOutputPolicy,
            DefaultContainer = source.DefaultContainer,
            SidecarDirectory = source.SidecarDirectory,
            TempDirectory = source.TempDirectory,
            QuarantineDirectory = source.QuarantineDirectory,
            QuarantineRetentionDays = source.QuarantineRetentionDays,
            MaxConcurrentJobs = source.MaxConcurrentJobs,
            PauseWhilePlaybackActive = source.PauseWhilePlaybackActive,
            LowProcessPriority = source.LowProcessPriority,
            EncodingThreadCount = source.EncodingThreadCount,
            FileStabilitySeconds = source.FileStabilitySeconds,
            DeepVerifyBeforeReplace = source.DeepVerifyBeforeReplace,
            FreeSpaceSafetyFactor = source.FreeSpaceSafetyFactor,
            AllowNonAdminAnalysis = source.AllowNonAdminAnalysis,
            JobHistoryLimit = source.JobHistoryLimit,
            KeepAudioLanguages = audio ?? source.KeepAudioLanguages,
            KeepSubtitleLanguages = subtitles ?? source.KeepSubtitleLanguages,
            KeepUntaggedTracks = source.KeepUntaggedTracks,
            DropCommentaryTracks = source.DropCommentaryTracks,
            Speed = source.Speed,
            PreferHardwareEncoding = source.PreferHardwareEncoding,
            ResumeJobsAfterRestart = source.ResumeJobsAfterRestart,
            RegenerateTrickplayAfterReplace = source.RegenerateTrickplayAfterReplace
        };
    }

    private async Task<Jellyfin.Database.Implementations.Entities.User?> GetCallingUserAsync()
    {
        try
        {
            var claim = User.FindFirst("Jellyfin-UserId")?.Value;
            if (Guid.TryParse(claim, out var userId))
            {
                return _userManager.GetUserById(userId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not resolve the calling user");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return null;
    }

    private LibraryItemSummary BuildSummary(BaseItem item, Jellyfin.Database.Implementations.Entities.User? user)
    {
        var streams = _mediaSourceManager.GetMediaStreams(item.Id);
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

        bool? watched = null;
        if (user is not null)
        {
            try
            {
                watched = _userDataManager.GetUserData(user, item)?.Played;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _logger.LogDebug(ex, "[MediaOptimizer] Could not read watched state");
            }
        }

        return new LibraryItemSummary
        {
            Id = item.Id,
            Name = BuildDisplayName(item),
            Type = item.GetType().Name,
            Path = item.Path,
            Container = System.IO.Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(),
            SizeBytes = TryGetSize(item.Path),
            RunTimeTicks = item.RunTimeTicks,
            Width = video?.Width,
            Height = video?.Height,
            VideoCodec = video?.Codec,
            BitrateBps = video?.BitRate,
            IsWatched = watched,
            HasActiveJob = _store.HasActiveJobForItem(item.Id)
        };
    }

    /// <summary>Builds a name that identifies an episode, not just its title.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>A display name.</returns>
    private static string BuildDisplayName(BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var series = episode.SeriesName;
            var season = episode.ParentIndexNumber;
            var number = episode.IndexNumber;

            if (!string.IsNullOrEmpty(series) && season is not null && number is not null)
            {
                return FormattableString.Invariant($"{series} — S{season:00}E{number:00} — {episode.Name}");
            }

            if (!string.IsNullOrEmpty(series))
            {
                return FormattableString.Invariant($"{series} — {episode.Name}");
            }
        }

        if (item.ProductionYear is not null)
        {
            return FormattableString.Invariant($"{item.Name} ({item.ProductionYear})");
        }

        return item.Name ?? "Untitled";
    }

    private static long? TryGetSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Describes a file as it currently exists on disk.</summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analysis.</returns>
    [HttpGet("Analyze/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FileAnalysis>> Analyze(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin && !(Plugin.Instance?.Configuration.AllowNonAdminAnalysis ?? true))
        {
            return Forbid();
        }

        var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        analysis.HasActiveJob = _store.HasActiveJobForItem(itemId);

        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        analysis.RecommendedStrategy = StrategyResolver.Recommend(analysis, caps).ToString();

        var previous = _store.GetAll()
            .FirstOrDefault(j => j.ItemId == itemId && j.Status == JobStatus.Completed);
        if (previous is not null)
        {
            analysis.OptimizationHistory = FormattableString.Invariant(
                $"This item was already optimised by this plugin on {previous.FinishedAt:yyyy-MM-dd}. Re-encoding an encode compounds quality loss.");
        }

        return Ok(analysis);
    }

    /// <summary>Reports what this server's ffmpeg can actually do.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capabilities.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<Capabilities>> GetCapabilities(CancellationToken cancellationToken)
    {
        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        caps.CanConvert = IsAdmin;
        return Ok(caps);
    }

    /// <summary>Predicts the outcome of a conversion without running it.</summary>
    /// <param name="request">The proposed settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The estimate and any warnings.</returns>
    [HttpPost("Estimate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EstimateResult>> Estimate(
        [FromBody] EncodeRequest request,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        // The planner needs a destination to build a complete argument vector; nothing is written.
        var plan = await _planner
            .PlanAsync(analysis, request, "/dev/null", cancellationToken)
            .ConfigureAwait(false);

        return Ok(_estimator.Estimate(analysis, request, plan));
    }

    /// <summary>Measures a stream's exact bitrate. Reads the whole file, so it is opt-in.</summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measured video bitrate.</returns>
    [HttpGet("MeasureBitrate/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BitrateInfo>> MeasureBitrate(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (analysis?.Video is null || analysis.DurationSeconds is not > 0)
        {
            return NotFound();
        }

        var bps = await _probe
            .MeasureBitrateAsync(analysis.Path, "v:0", analysis.DurationSeconds.Value, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new BitrateInfo
        {
            Bps = bps,
            Source = bps is not null ? ValueSource.Measured : ValueSource.Unknown
        });
    }

    /// <summary>Lists the queue and recent history.</summary>
    /// <returns>All known jobs, newest first.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<EncodeJob>> GetJobs() => Ok(_store.GetAll());

    /// <summary>Gets one job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job.</returns>
    [HttpGet("Jobs/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EncodeJob> GetJob([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Queues a conversion.</summary>
    /// <param name="request">The settings to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The queued job.</returns>
    [HttpPost("Jobs")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EncodeJob>> CreateJob(
        [FromBody] EncodeRequest request,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        if (!analysis.IsEligible)
        {
            return BadRequest(new { error = analysis.IneligibleReason });
        }

        if (_store.HasActiveJobForItem(request.ItemId))
        {
            return BadRequest(new { error = "This item already has a conversion queued or running." });
        }

        // Validate before queueing so blockers surface immediately rather than on a worker
        // thread minutes later.
        var plan = await _planner
            .PlanAsync(analysis, request, "/dev/null", cancellationToken)
            .ConfigureAwait(false);

        if (!plan.IsRunnable)
        {
            var blockers = plan.Warnings.Where(w => w.Level == WarningLevel.Blocker).ToList();
            return BadRequest(new { error = blockers[0].Message, warnings = blockers });
        }

        var job = new EncodeJob
        {
            ItemId = request.ItemId,
            ItemName = analysis.Name,
            SourcePath = analysis.Path,
            SourceSizeBytes = analysis.SizeBytes,
            Request = request,
            OutputPolicy = request.OutputPolicy,
            Warnings = plan.Warnings,
            IsLossless = plan.IsLossless
        };

        _store.Add(job);

        _logger.LogInformation(
            "[MediaOptimizer] Queued job {JobId} for {Name} ({Policy})",
            job.Id,
            job.ItemName,
            job.OutputPolicy);

        return Ok(job);
    }

    /// <summary>Cancels a queued or running job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelJob([FromRoute] Guid id) =>
        _queue.Cancel(id) ? NoContent() : NotFound();

    /// <summary>Deletes a finished job from the history.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}/History")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DeleteJob([FromRoute] Guid id)
    {
        var job = _store.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        if (job.IsActive)
        {
            return BadRequest(new { error = "Cancel the job before removing it from the history." });
        }

        if (job.CanRevert)
        {
            return BadRequest(new { error = "This job still holds the original file in quarantine. Revert it, or wait for the retention period to expire." });
        }

        _store.Remove(id);
        return NoContent();
    }

    /// <summary>Restores the original file for a completed Replace job.</summary>
    /// <param name="id">Job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated job.</returns>
    [HttpPost("Jobs/{id}/Revert")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EncodeJob>> RevertJob(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var job = _store.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        if (!job.CanRevert)
        {
            return BadRequest(new { error = "This job cannot be reverted. The original is no longer in quarantine." });
        }

        try
        {
            await _output.RevertAsync(job, cancellationToken).ConfigureAwait(false);
            _store.Update(job);
            return Ok(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Revert failed for job {JobId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Requeues an interrupted or failed job with the same settings.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The new job.</returns>
    [HttpPost("Jobs/{id}/Requeue")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EncodeJob> RequeueJob([FromRoute] Guid id)
    {
        var original = _store.Get(id);
        if (original is null)
        {
            return NotFound();
        }

        if (_store.HasActiveJobForItem(original.ItemId))
        {
            return BadRequest(new { error = "This item already has a conversion queued or running." });
        }

        var job = new EncodeJob
        {
            ItemId = original.ItemId,
            ItemName = original.ItemName,
            SourcePath = original.SourcePath,
            Request = original.Request,
            OutputPolicy = original.OutputPolicy
        };

        _store.Add(job);
        return Ok(job);
    }
}
