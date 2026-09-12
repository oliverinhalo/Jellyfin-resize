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
    private readonly ISampleEncoder _sampler;
    private readonly IQualitySearch _qualitySearch;
    private readonly IOperationRegistry _operations;
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
    /// <param name="sampler">Sample encoder, for a measured estimate.</param>
    /// <param name="qualitySearch">Quality search, for finding a setting by measuring.</param>
    /// <param name="operations">Registry of work that outlives the request that started it.</param>
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
        ISampleEncoder sampler,
        IQualitySearch qualitySearch,
        IOperationRegistry operations,
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
        _sampler = sampler;
        _qualitySearch = qualitySearch;
        _operations = operations;
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
    /// Whether the caller may inspect files. Every endpoint that probes a file honours this, not
    /// just the one named "Analyze": resolving a strategy and estimating a conversion both run
    /// ffprobe over the same file and return the same information about it, so gating one and not
    /// the others was a setting that did not do what it said.
    /// </summary>
    private bool MayAnalyze => IsAdmin || (Plugin.Instance?.Configuration.AllowNonAdminAnalysis ?? true);

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
    /// <param name="limit">Maximum results returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching items.</returns>
    [HttpGet("Library/Search")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LibraryItemSummary>>> SearchLibrary(
        [FromQuery] string? query,
        [FromQuery] LibrarySort sort = LibrarySort.SizeDescending,
        [FromQuery] WatchedFilter watched = WatchedFilter.Any,
        [FromQuery] int? minHeight = null,
        [FromQuery] long? minSizeMb = null,
        [FromQuery] string? container = null,
        [FromQuery] string? codec = null,
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
            LibrarySort.SavingDescending => results.OrderByDescending(r => r.PotentialSavingBytes ?? 0),
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
    [Authorize(Policy = "RequiresElevation")]
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

        // The libraries come from the server rather than from the items, so a rule can be confined
        // to one that happens to be empty today.
        var libraries = new List<string>();
        try
        {
            libraries = _libraryManager.GetVirtualFolders()
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
#pragma warning disable CA1031 // A filter list is not worth failing the page over.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not list the server's libraries");
        }

        return Ok(new
        {
            containers = containers.ToList(),
            codecs = codecs.ToList(),
            libraries
        });
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
        if (!MayAnalyze)
        {
            return Forbid();
        }

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
                SourceHeight = analysis.Video?.Height,
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
        var copy = source.Clone();
        copy.KeepAudioLanguages = audio ?? source.KeepAudioLanguages;
        copy.KeepSubtitleLanguages = subtitles ?? source.KeepSubtitleLanguages;
        return copy;
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

        var size = TryGetSize(item.Path);
        var forecast = SavingForecast.For(size, video?.Height, video?.Codec);

        return new LibraryItemSummary
        {
            Id = item.Id,
            Name = BuildDisplayName(item),
            Type = item.GetType().Name,
            Path = item.Path,
            Container = System.IO.Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(),
            SizeBytes = size,
            RunTimeTicks = item.RunTimeTicks,
            Width = video?.Width,
            Height = video?.Height,
            VideoCodec = video?.Codec,
            BitrateBps = video?.BitRate,
            IsWatched = watched,
            HasActiveJob = _store.HasActiveJobForItem(item.Id),
            PotentialSavingBytes = forecast.SavingBytes,
            SavingBasis = forecast.SavingBytes is null ? null : forecast.Basis
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
        if (!MayAnalyze)
        {
            return Forbid();
        }

        var analysis = await _probe.AnalyzeAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        analysis.HasActiveJob = _store.HasActiveJobForItem(itemId);
        analysis.ActiveJobId = _store.GetActive().FirstOrDefault(j => j.ItemId == itemId)?.Id;

        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        analysis.RecommendedStrategy = StrategyResolver.Recommend(analysis, caps).ToString();

        var previous = _store.GetAll()
            .FirstOrDefault(j => j.ItemId == itemId && j.Status == JobStatus.Completed);
        if (previous is not null)
        {
            analysis.OptimizationHistory = FormattableString.Invariant(
                $"This item was already optimised by this plugin on {previous.FinishedAt:yyyy-MM-dd}. Re-encoding an encode compounds quality loss.");
        }

        if (!IsAdmin)
        {
            RedactServerPaths(analysis);
        }

        return Ok(analysis);
    }

    /// <summary>
    /// Strips the server's filesystem out of an analysis, leaving the file name.
    /// <para>
    /// This is the one thing a non-administrator can open, and every other read on this controller
    /// is administrator-only precisely so that being able to browse a library does not become
    /// being able to enumerate where every file lives. The dialog only ever displays the path, and
    /// the name is the part of it that means anything to someone who cannot reach the disk. The
    /// two error fields are scrubbed as well, because ffprobe quotes the path it was given.
    /// </para>
    /// </summary>
    /// <param name="analysis">The analysis, edited in place.</param>
    internal static void RedactServerPaths(FileAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var full = analysis.Path;
        if (string.IsNullOrEmpty(full))
        {
            return;
        }

        var name = System.IO.Path.GetFileName(full);
        var directory = System.IO.Path.GetDirectoryName(full);

        analysis.Path = name;
        analysis.IneligibleReason = Scrub(analysis.IneligibleReason);
        analysis.StreamInfoError = Scrub(analysis.StreamInfoError);

        string? Scrub(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            // The full path first: replacing the directory alone would leave a bare file name
            // glued to whatever followed it.
            var cleaned = text.Replace(full, name, StringComparison.Ordinal);
            return string.IsNullOrEmpty(directory)
                ? cleaned
                : cleaned.Replace(directory, "\u2026", StringComparison.Ordinal);
        }
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
        if (!MayAnalyze)
        {
            return Forbid();
        }

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

    /// <summary>
    /// Measures the outcome by actually encoding a few short stretches of the file with these
    /// settings, rather than modelling it.
    /// <para>
    /// This costs a minute or so of real encoding, which is why it is a separate request the user
    /// asks for. It is the only number this plugin can offer that is a measurement rather than a
    /// prediction, so the answer it gives is labelled as such — including how far apart the
    /// samples were, which is the part a single averaged number would hide.
    /// </para>
    /// </summary>
    /// <para>
    /// It answers with an id rather than the measurement: a minute of encoding is longer than the
    /// sixty-second read timeout in front of most Jellyfin servers, and a request that dies in a
    /// proxy looks exactly like a broken feature. Ask
    /// <see cref="GetOperation"/> how it is going.
    /// </para>
    /// <param name="request">The proposed settings.</param>
    /// <param name="cancellationToken">Cancellation token for starting it, not for the work.</param>
    /// <returns>The id of the measurement now running.</returns>
    [HttpPost("Estimate/Sample")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationHandle>> SampleEstimate(
        [FromBody] EncodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var analysis = await _probe.AnalyzeAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        var id = _operations.Start("measure", token => MeasureAsync(analysis, request, token));
        return Accepted(new OperationHandle { Id = id });
    }

    /// <summary>Does the measuring, once the request that asked for it has already answered.</summary>
    /// <param name="analysis">The source analysis.</param>
    /// <param name="request">The proposed settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measured estimate, or the modelled one when nothing could be sampled.</returns>
    private async Task<object> MeasureAsync(
        FileAnalysis analysis,
        EncodeRequest request,
        CancellationToken cancellationToken)
    {
        var workDirectory = _output.GetWorkDirectoryFor(analysis.Path);

        // The samples are written with the same naming rules as a real working file -- leading
        // dot, .motmp suffix -- so the library scanner never sees one even if a delete is missed.
        var plan = await _planner
            .PlanAsync(analysis, request, System.IO.Path.Combine(workDirectory, "sample.motmp"), cancellationToken)
            .ConfigureAwait(false);

        var modelled = _estimator.Estimate(analysis, request, plan);
        if (!plan.IsRunnable)
        {
            return modelled;
        }

        // The comparison rides along with the sample encodes that are happening anyway, so the
        // quality number costs a fraction of what it would to measure on its own.
        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        var metric = (Plugin.Instance?.Configuration.MeasureQualityWhenSampling ?? true)
            ? caps.QualityMetric
            : null;

        var measurement = await _sampler
            .MeasureAsync(analysis, plan, workDirectory, metric, 0, cancellationToken)
            .ConfigureAwait(false);

        if (!measurement.Succeeded)
        {
            modelled.MeasurementNote = measurement.FailureReason;
            return modelled;
        }

        return SizeEstimator.FromMeasurement(analysis, measurement, modelled);
    }

    /// <summary>
    /// Finds the smallest file that still looks as close to the source as asked for.
    /// <para>
    /// The one question this plugin could never answer was "what quality number should I use?", and
    /// the honest answer was always "it depends on the file". It still does — but the file can now
    /// be measured, so the question can be turned round: say how close to the source it has to
    /// look, and the server encodes short stretches at several settings until it finds the smallest
    /// one that holds. It costs real encoding time, which is why it is a button rather than
    /// something that happens on its own.
    /// </para>
    /// </summary>
    /// <para>
    /// Like the measurement, it answers with an id and gets on with it: this one runs for several
    /// minutes, which no HTTP request between a browser and a Jellyfin server should be asked to
    /// survive.
    /// </para>
    /// <param name="request">The settings to search within. Its quality is what moves.</param>
    /// <param name="target">How close to the source the result has to look.</param>
    /// <param name="cancellationToken">Cancellation token for starting it, not for the work.</param>
    /// <returns>The id of the search now running.</returns>
    [HttpPost("Estimate/FindQuality")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationHandle>> FindQuality(
        [FromBody] EncodeRequest request,
        [FromQuery] QualityTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var analysis = await _probe.AnalyzeAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return NotFound();
        }

        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        var workDirectory = _output.GetWorkDirectoryFor(analysis.Path);

        var id = _operations.Start("search", async token => await _qualitySearch
            .SearchAsync(analysis, request, target, caps.QualityMetric, workDirectory, token)
            .ConfigureAwait(false));

        return Accepted(new OperationHandle { Id = id });
    }

    /// <summary>
    /// Reports how a measurement or a search is going, and hands over its answer when it has one.
    /// </summary>
    /// <param name="id">The operation id.</param>
    /// <returns>Its state, or 404 once it has been forgotten.</returns>
    [HttpGet("Operations/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OperationState> GetOperation([FromRoute] Guid id)
    {
        var state = _operations.Get(id);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>
    /// Stops a measurement or a search.
    /// <para>
    /// This is what closing the dialog does. Without it, walking away from a five-minute search
    /// would leave the server encoding for five minutes with nobody left to tell.
    /// </para>
    /// </summary>
    /// <param name="id">The operation id.</param>
    /// <returns>No content, whether or not there was anything still running.</returns>
    [HttpDelete("Operations/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelOperation([FromRoute] Guid id)
    {
        _operations.Cancel(id);
        return NoContent();
    }

    /// <summary>Measures a stream's exact bitrate. Reads the whole file, so it is opt-in.</summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measured video bitrate.</returns>
    [HttpGet("MeasureBitrate/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
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
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<EncodeJob>> GetJobs() => Ok(_store.GetAll());

    /// <summary>Gets one job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job.</returns>
    [HttpGet("Jobs/{id}")]
    [Authorize(Policy = "RequiresElevation")]
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
            SourceHeight = analysis.Video?.Height,
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
            SourceSizeBytes = original.SourceSizeBytes,
            SourceHeight = original.SourceHeight,
            Request = original.Request,
            OutputPolicy = original.OutputPolicy
        };

        _store.Add(job);
        return Ok(job);
    }
}
