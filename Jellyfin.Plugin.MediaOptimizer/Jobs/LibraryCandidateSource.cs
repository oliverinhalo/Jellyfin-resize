using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Jobs;

/// <summary>
/// Reads the library and reduces each item to the handful of facts a rule is matched against.
/// <para>
/// This is the only part of the automatic side that touches Jellyfin, which is the point: the
/// decisions about which of someone's files get rewritten are made in
/// <see cref="Core.RuleMatcher"/> and <see cref="AutomationService"/> over plain data, where they
/// can be tested exhaustively without a server.
/// </para>
/// </summary>
public class LibraryCandidateSource : ILibraryCandidateSource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IJobStore _store;
    private readonly ILogger<LibraryCandidateSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="LibraryCandidateSource"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager, for stream metadata.</param>
    /// <param name="userManager">User manager, for resolving watched state across users.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="store">Job store, for what has already been done.</param>
    /// <param name="logger">Logger.</param>
    public LibraryCandidateSource(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IJobStore store,
        ILogger<LibraryCandidateSource> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<RuleCandidate> GetCandidates(CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false
        };

        // One pass over the job history rather than a lookup per item: a large library would
        // otherwise walk the whole history thousands of times over.
        var jobs = _store.GetAll();
        var active = jobs.Where(j => j.IsActive).Select(j => j.ItemId).ToHashSet();
        var converted = jobs.Where(j => j.Status == JobStatus.Completed).Select(j => j.ItemId).ToHashSet();

        var users = SafeUsers();
        var libraries = SafeLibraries();
        var candidates = new List<RuleCandidate>();

        foreach (var item in _libraryManager.GetItemList(query))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.Path) || item is not Video)
            {
                continue;
            }

            var video = SafeStreams(item.Id).FirstOrDefault(s => s.Type == MediaStreamType.Video);

            candidates.Add(new RuleCandidate
            {
                ItemId = item.Id,
                Name = item.Name ?? string.Empty,
                ItemType = item.GetType().Name,
                Container = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(),
                VideoCodec = video?.Codec,
                LibraryName = LibraryLocator.NameFor(item.Path, libraries),
                Height = video?.Height,
                SizeBytes = SafeSize(item.Path),
                IsWatched = WatchedByAnyone(item, users),
                DateCreated = item.DateCreated == default ? DateTime.UtcNow : item.DateCreated.ToUniversalTime(),
                HasActiveJob = active.Contains(item.Id),
                PreviouslyOptimized = converted.Contains(item.Id)
            });
        }

        return candidates;
    }

    /// <summary>
    /// Reads the server's libraries as name-and-folders pairs. A library the plugin cannot read
    /// simply means no rule can be confined to it, which is a filter that matches nothing rather
    /// than a rule that runs over everything.
    /// </summary>
    /// <returns>The libraries, or an empty list.</returns>
    private IReadOnlyList<LibraryLocation> SafeLibraries()
    {
        try
        {
            return _libraryManager.GetVirtualFolders()
                .Select(f => new LibraryLocation(f.Name ?? string.Empty, f.Locations ?? []))
                .Where(l => !string.IsNullOrEmpty(l.Name))
                .ToList();
        }
#pragma warning disable CA1031 // A library list we cannot read must not abandon the run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not read the server's libraries; rules confined to one will match nothing");
            return Array.Empty<LibraryLocation>();
        }
    }

    private IReadOnlyList<Jellyfin.Database.Implementations.Entities.User> SafeUsers()
    {
        try
        {
            return _userManager.Users.ToList();
        }
#pragma warning disable CA1031 // Watched state is a filter, not a reason to abandon the run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not list users; watched filters will treat items as unwatched");
            return Array.Empty<Jellyfin.Database.Implementations.Entities.User>();
        }
    }

    private IReadOnlyList<MediaStream> SafeStreams(Guid itemId)
    {
        try
        {
            return _mediaSourceManager.GetMediaStreams(itemId);
        }
#pragma warning disable CA1031 // An unreadable item is skipped by the filters, not fatal.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not read streams for {ItemId}", itemId);
            return Array.Empty<MediaStream>();
        }
    }

    private static long? SafeSize(string path)
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

    /// <summary>
    /// Whether anybody at all has watched this. A rule saying "only convert what has been watched"
    /// means the household, not one nominated account — on a family server the person who watched
    /// it is rarely the administrator who wrote the rule.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="users">Every user on the server.</param>
    /// <returns>Whether any user has played it.</returns>
    private bool WatchedByAnyone(BaseItem item, IReadOnlyList<Jellyfin.Database.Implementations.Entities.User> users)
    {
        foreach (var user in users)
        {
            try
            {
                if (_userDataManager.GetUserData(user, item)?.Played == true)
                {
                    return true;
                }
            }
#pragma warning disable CA1031 // One unreadable user must not decide the answer for the rest.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "[MediaOptimizer] Could not read watched state for {ItemId}", item.Id);
            }
        }

        return false;
    }
}
