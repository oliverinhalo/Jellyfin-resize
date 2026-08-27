namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>How the dashboard's file list is ordered.</summary>
public enum LibrarySort
{
    /// <summary>Largest files first — the ones worth converting.</summary>
    SizeDescending = 0,

    /// <summary>Smallest files first.</summary>
    SizeAscending = 1,

    /// <summary>A to Z.</summary>
    Name = 2,

    /// <summary>Highest resolution first.</summary>
    ResolutionDescending = 3,

    /// <summary>Most recently added first.</summary>
    DateAdded = 4,

    /// <summary>Highest bitrate first.</summary>
    BitrateDescending = 5
}

/// <summary>Which items the dashboard's file list includes.</summary>
public enum WatchedFilter
{
    /// <summary>No filtering on watched state.</summary>
    Any = 0,

    /// <summary>Only items the current user has watched.</summary>
    Watched = 1,

    /// <summary>Only items the current user has not watched.</summary>
    Unwatched = 2
}
