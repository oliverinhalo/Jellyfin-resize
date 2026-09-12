using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaOptimizer.Configuration;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>Which kinds of item a rule looks at.</summary>
public enum RuleItemKinds
{
    /// <summary>Films, episodes and other videos alike.</summary>
    Everything = 0,

    /// <summary>Films only.</summary>
    MoviesOnly = 1,

    /// <summary>Television episodes only.</summary>
    EpisodesOnly = 2
}

/// <summary>
/// A saved "convert anything that looks like this" instruction, applied on a schedule.
/// <para>
/// Deliberately conservative by construction. A rule names a ceiling on how many files it may
/// queue in one run, and a minimum saving below which it leaves a file alone, because the failure
/// mode of an automatic rule is not "it did nothing" — it is "it re-encoded four hundred files
/// overnight and the first anyone knew was the disk light".
/// </para>
/// </summary>
public class AutomationRule
{
    /// <summary>Gets or sets the rule id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the name shown in the dashboard.</summary>
    public string Name { get; set; } = "New rule";

    /// <summary>Gets or sets a value indicating whether the rule runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets which kinds of item the rule considers.</summary>
    public RuleItemKinds Kinds { get; set; } = RuleItemKinds.Everything;

    /// <summary>Gets or sets the minimum height in pixels, or null for any.</summary>
    public int? MinHeight { get; set; }

    /// <summary>Gets or sets the minimum file size in megabytes, or null for any.</summary>
    public long? MinSizeMb { get; set; }

    /// <summary>Gets or sets a container extension the file must have, or null for any.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets a video codec the file must use, or null for any.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Gets or sets the library the rule is confined to, by its name in Jellyfin, or null for
    /// every library. This is how "convert the TV recordings but never touch the films" is said.
    /// </summary>
    public string? LibraryName { get; set; }

    /// <summary>Gets or sets the watched-state requirement. "Watched" means watched by anyone.</summary>
    public WatchedFilter Watched { get; set; } = WatchedFilter.Any;

    /// <summary>
    /// Gets or sets how long an item must have been in the library before a rule will touch it.
    /// A grace period matters: converting something the evening it was added replaces the file
    /// before anyone has watched it once, and before a bad download has been noticed.
    /// </summary>
    public int? AddedMoreThanDaysAgo { get; set; } = 30;

    /// <summary>Gets or sets the strategy applied to matching items.</summary>
    public OptimizationStrategy Strategy { get; set; } = OptimizationStrategy.Standard;

    /// <summary>Gets or sets an explicit target height, overriding the strategy's own choice.</summary>
    public int? TargetHeight { get; set; }

    /// <summary>Gets or sets the output container, or null to let each file keep its best fit.</summary>
    public string? OutputContainer { get; set; }

    /// <summary>Gets or sets where results are placed, or null to use the plugin default.</summary>
    public OutputPolicy? OutputPolicy { get; set; }

    /// <summary>Gets or sets the audio languages to keep, or null for the plugin default.</summary>
    public string? KeepAudioLanguages { get; set; }

    /// <summary>Gets or sets the subtitle languages to keep, or null for the plugin default.</summary>
    public string? KeepSubtitleLanguages { get; set; }

    /// <summary>
    /// Gets or sets what kind of footage this rule's files are, for the encoder's content tuning.
    /// <para>
    /// A rule already narrows a library down — "the anime library", "everything shot on film" — so
    /// it is the one place where saying what the content is can be true of every file it takes.
    /// </para>
    /// </summary>
    public ContentTune Tune { get; set; } = ContentTune.Auto;

    /// <summary>Gets or sets a value indicating whether hardware encoding may be used.</summary>
    public bool UseHardware { get; set; }

    /// <summary>Gets or sets how many files this rule may queue in a single run.</summary>
    public int MaxItemsPerRun { get; set; } = 3;

    /// <summary>
    /// Gets or sets the saving, as a percentage of the current file, below which the rule leaves
    /// a file alone. Re-encoding for a 3% saving spends hours of CPU and a generation of quality
    /// to reclaim almost nothing.
    /// </summary>
    public int MinSavingPercent { get; set; } = 15;

    /// <summary>Gets or sets when the rule last ran.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Gets or sets how many jobs this rule has queued since it was created.</summary>
    public int TotalQueued { get; set; }
}

/// <summary>The facts about one library item that a rule is matched against.</summary>
public class RuleCandidate
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type name, e.g. Movie or Episode.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's container extension.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the video codec.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the library the file sits in, when it could be determined.</summary>
    public string? LibraryName { get; set; }

    /// <summary>Gets or sets the video height in pixels.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets whether anybody has watched this item.</summary>
    public bool IsWatched { get; set; }

    /// <summary>Gets or sets when the item was added to the library.</summary>
    public DateTime DateCreated { get; set; }

    /// <summary>Gets or sets a value indicating whether a conversion is already queued or running.</summary>
    public bool HasActiveJob { get; set; }

    /// <summary>Gets or sets a value indicating whether this plugin has already converted this item.</summary>
    public bool PreviouslyOptimized { get; set; }
}

/// <summary>Whether a rule takes an item, and why not when it does not.</summary>
public class RuleDecision
{
    /// <summary>Initializes a new instance of the <see cref="RuleDecision"/> class.</summary>
    /// <param name="matches">Whether the item matches.</param>
    /// <param name="reason">Why it does not, when it does not.</param>
    public RuleDecision(bool matches, string? reason)
    {
        Matches = matches;
        Reason = reason;
    }

    /// <summary>Gets a value indicating whether the item matches the rule.</summary>
    public bool Matches { get; }

    /// <summary>Gets the reason the item was passed over, or null when it matched.</summary>
    public string? Reason { get; }

    /// <summary>A match.</summary>
    /// <returns>A positive decision.</returns>
    public static RuleDecision Yes() => new RuleDecision(true, null);

    /// <summary>A miss, with the reason.</summary>
    /// <param name="reason">Why the item was passed over.</param>
    /// <returns>A negative decision.</returns>
    public static RuleDecision No(string reason) => new RuleDecision(false, reason);
}

/// <summary>What one rule did to one item during a run.</summary>
public class RuleRunItem
{
    /// <summary>Gets or sets the rule that considered the item.</summary>
    public Guid RuleId { get; set; }

    /// <summary>Gets or sets the rule's name.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a job was queued (or would have been).</summary>
    public bool Queued { get; set; }

    /// <summary>Gets or sets the job id, when one was created.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Gets or sets why the item was not queued.</summary>
    public string? SkippedReason { get; set; }

    /// <summary>Gets or sets the predicted saving in bytes.</summary>
    public long? EstimatedSavingBytes { get; set; }
}

/// <summary>The outcome of a rule run.</summary>
public class RuleRunResult
{
    /// <summary>Gets or sets a value indicating whether this was a preview that queued nothing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets how many library items were examined.</summary>
    public int Considered { get; set; }

    /// <summary>Gets or sets how many jobs were queued.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets the total predicted saving across those jobs.</summary>
    public long EstimatedSavingBytes { get; set; }

    /// <summary>Gets or sets the per-item outcomes, including the interesting near-misses.</summary>
    public IReadOnlyList<RuleRunItem> Items { get; set; } = Array.Empty<RuleRunItem>();
}
