using System;
using System.Globalization;
using Jellyfin.Plugin.MediaOptimizer.Models;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>
/// Decides whether one saved rule applies to one library item.
/// <para>
/// Kept as a pure function over plain facts, with no Jellyfin types in sight, because this is the
/// part that decides which of someone's files get rewritten while they are asleep. It has to be
/// exhaustively testable, and every "no" has to carry a reason the dashboard can show — a preview
/// that says only "4 of 900 items match" is not something anybody can trust enough to switch on.
/// </para>
/// </summary>
public static class RuleMatcher
{
    /// <summary>Evaluates a rule against one candidate.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="candidate">The item's facts.</param>
    /// <param name="now">The current time, so the age filter is testable.</param>
    /// <returns>The decision, with a reason when the answer is no.</returns>
    public static RuleDecision Evaluate(AutomationRule rule, RuleCandidate candidate, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!rule.Enabled)
        {
            return RuleDecision.No("The rule is switched off.");
        }

        // These two come first: they are about the item's history with this plugin rather than
        // about the filters, and they are the reasons a user is most likely to ask about.
        if (candidate.HasActiveJob)
        {
            return RuleDecision.No("Already queued or converting.");
        }

        if (candidate.PreviouslyOptimized)
        {
            return RuleDecision.No("Already converted by this plugin. Re-encoding an encode compounds quality loss.");
        }

        var kindMatches = rule.Kinds switch
        {
            RuleItemKinds.MoviesOnly => string.Equals(candidate.ItemType, "Movie", StringComparison.OrdinalIgnoreCase),
            RuleItemKinds.EpisodesOnly => string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase),
            _ => true
        };

        if (!kindMatches)
        {
            return RuleDecision.No(FormattableString.Invariant(
                $"This rule only looks at {(rule.Kinds == RuleItemKinds.MoviesOnly ? "films" : "episodes")}."));
        }

        if (rule.MinHeight is > 0)
        {
            if (candidate.Height is null)
            {
                return RuleDecision.No("The resolution of this file is not known, and the rule filters on it.");
            }

            if (candidate.Height < rule.MinHeight)
            {
                return RuleDecision.No(FormattableString.Invariant(
                    $"{candidate.Height}p is below the rule's {rule.MinHeight}p floor."));
            }
        }

        if (rule.MinSizeMb is > 0)
        {
            var minBytes = rule.MinSizeMb.Value * 1024L * 1024L;
            if (candidate.SizeBytes is null)
            {
                return RuleDecision.No("The size of this file is not known, and the rule filters on it.");
            }

            if (candidate.SizeBytes < minBytes)
            {
                return RuleDecision.No(FormattableString.Invariant(
                    $"{candidate.SizeBytes.Value / 1024 / 1024} MB is below the rule's {rule.MinSizeMb} MB floor."));
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.Container)
            && !string.Equals(Trim(candidate.Container), Trim(rule.Container), StringComparison.OrdinalIgnoreCase))
        {
            return RuleDecision.No(FormattableString.Invariant(
                $"The rule only takes {rule.Container!.Trim().ToUpperInvariant()} files; this one is {(string.IsNullOrEmpty(candidate.Container) ? "of an unknown container" : candidate.Container.ToUpperInvariant())}."));
        }

        if (!string.IsNullOrWhiteSpace(rule.VideoCodec)
            && !string.Equals(Trim(candidate.VideoCodec), Trim(rule.VideoCodec), StringComparison.OrdinalIgnoreCase))
        {
            return RuleDecision.No(FormattableString.Invariant(
                $"The rule only takes {rule.VideoCodec!.Trim().ToUpperInvariant()}; this one is {(string.IsNullOrEmpty(candidate.VideoCodec) ? "of an unknown codec" : candidate.VideoCodec.ToUpperInvariant())}."));
        }

        if (rule.Watched == WatchedFilter.Watched && !candidate.IsWatched)
        {
            return RuleDecision.No("Nobody has watched this yet, and the rule only takes watched items.");
        }

        if (rule.Watched == WatchedFilter.Unwatched && candidate.IsWatched)
        {
            return RuleDecision.No("Somebody has watched this, and the rule only takes unwatched items.");
        }

        if (rule.AddedMoreThanDaysAgo is > 0)
        {
            var age = now - candidate.DateCreated;
            if (age < TimeSpan.FromDays(rule.AddedMoreThanDaysAgo.Value))
            {
                return RuleDecision.No(string.Format(
                    CultureInfo.InvariantCulture,
                    "Added {0:F0} day(s) ago; the rule waits {1}.",
                    Math.Max(0d, age.TotalDays),
                    rule.AddedMoreThanDaysAgo.Value == 1 ? "1 day" : rule.AddedMoreThanDaysAgo.Value + " days"));
            }
        }

        return RuleDecision.Yes();
    }

    /// <summary>
    /// Whether a predicted saving is worth the encode. A rule that spends four hours of CPU and a
    /// generation of quality to reclaim 2% of a file is not optimising anything.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="currentBytes">The file's current size.</param>
    /// <param name="estimatedBytes">The predicted size afterwards.</param>
    /// <returns>The decision, with the numbers in the reason when the answer is no.</returns>
    public static RuleDecision IsSavingWorthwhile(AutomationRule rule, long currentBytes, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var floor = Math.Clamp(rule.MinSavingPercent, 0, 99);
        if (floor == 0)
        {
            return RuleDecision.Yes();
        }

        if (currentBytes <= 0)
        {
            return RuleDecision.No("The current size of this file is not known, so the saving cannot be judged.");
        }

        var percent = (1d - ((double)estimatedBytes / currentBytes)) * 100d;
        if (percent < floor)
        {
            return RuleDecision.No(string.Format(
                CultureInfo.InvariantCulture,
                "Predicted saving is only {0:F0}%, below the rule's {1}% floor.",
                percent,
                floor));
        }

        return RuleDecision.Yes();
    }

    private static string Trim(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('.');
}
