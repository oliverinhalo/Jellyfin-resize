using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Jobs;

/// <summary>Supplies the library items rules are matched against.</summary>
public interface ILibraryCandidateSource
{
    /// <summary>Lists every convertible item with the facts a rule needs.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates.</returns>
    IReadOnlyList<RuleCandidate> GetCandidates(CancellationToken cancellationToken);
}

/// <summary>Applies the saved rules to the library.</summary>
public interface IAutomationService
{
    /// <summary>Runs the enabled rules, or previews what they would do.</summary>
    /// <param name="dryRun">When true, nothing is queued and nothing is written.</param>
    /// <param name="ruleId">One rule to run, or null for every enabled rule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, item by item.</returns>
    Task<RuleRunResult> RunAsync(bool dryRun, Guid? ruleId, CancellationToken cancellationToken);
}

/// <summary>
/// The automatic side of the plugin: saved rules, applied on a schedule.
/// <para>
/// Everything here is built around the fact that this runs unattended. A rule queues at most the
/// number of files it says it may, biggest first, and only after the same planning and estimating
/// the interactive dialog does — so a file that would be blocked in the dialog is skipped here for
/// the same stated reason rather than queued and failed. Nothing about a rule can bypass the
/// verification gate: a rule produces ordinary jobs, which the same worker runs the same way.
/// </para>
/// </summary>
public class AutomationService : IAutomationService
{
    private readonly IPluginConfigurationSource _settings;
    private readonly ILibraryCandidateSource _candidates;
    private readonly IMediaProbeService _probe;
    private readonly IEncodePlanner _planner;
    private readonly ISizeEstimator _estimator;
    private readonly ICapabilityService _capabilities;
    private readonly IJobStore _store;
    private readonly ILogger<AutomationService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AutomationService"/> class.</summary>
    /// <param name="settings">Plugin settings, which hold the rules.</param>
    /// <param name="candidates">Where library items come from.</param>
    /// <param name="probe">Probe service.</param>
    /// <param name="planner">Encode planner.</param>
    /// <param name="estimator">Size estimator.</param>
    /// <param name="capabilities">Capability service.</param>
    /// <param name="store">Job store.</param>
    /// <param name="logger">Logger.</param>
    public AutomationService(
        IPluginConfigurationSource settings,
        ILibraryCandidateSource candidates,
        IMediaProbeService probe,
        IEncodePlanner planner,
        ISizeEstimator estimator,
        ICapabilityService capabilities,
        IJobStore store,
        ILogger<AutomationService> logger)
    {
        _settings = settings;
        _candidates = candidates;
        _probe = probe;
        _planner = planner;
        _estimator = estimator;
        _capabilities = capabilities;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RuleRunResult> RunAsync(bool dryRun, Guid? ruleId, CancellationToken cancellationToken)
    {
        var config = _settings.Configuration;
        var rules = config.Rules
            .Where(r => ruleId is null ? r.Enabled : r.Id == ruleId.Value)
            .ToList();

        var result = new RuleRunResult { DryRun = dryRun };
        var items = new List<RuleRunItem>();

        if (rules.Count == 0)
        {
            result.Items = items;
            return result;
        }

        // Asking for one rule by name is an explicit act: previewing it, or running it by hand.
        // Both are how a rule gets written in the first place -- see what it would take, then
        // switch it on -- so being switched off does not stop them. The scheduled run passes no
        // id, and there "off" means off.
        var explicitRule = ruleId is not null;

        var candidates = _candidates.GetCandidates(cancellationToken);
        result.Considered = candidates.Count;

        var caps = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Items queued earlier in this run are off the table for later rules, so two overlapping
        // rules cannot both queue the same file.
        var claimed = new HashSet<Guid>();

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var queuedForRule = 0;
            var ceiling = Math.Max(0, rule.MaxItemsPerRun);

            // Matching is free; deciding is not. Every item that passes the filters is probed with
            // ffprobe and planned, and a rule whose files all fall below its saving floor would
            // otherwise walk the entire library doing that -- inside one HTTP request, when this
            // is a preview. Stop after a sensible multiple of what the rule could queue anyway.
            var examineLimit = Math.Max(25, ceiling * 10);
            var examined = 0;

            // Biggest first: the whole point of an automatic rule is to reclaim space, and the
            // 40 GB remux is worth more than fifty episodes of a sitcom.
            foreach (var candidate in candidates.OrderByDescending(c => c.SizeBytes ?? 0))
            {
                if (queuedForRule >= ceiling)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (claimed.Contains(candidate.ItemId))
                {
                    continue;
                }

                var decision = RuleMatcher.Evaluate(rule, candidate, now, explicitRule);
                if (!decision.Matches)
                {
                    // Only the near-misses are reported: a library of ten thousand items would
                    // otherwise produce ten thousand lines saying "this rule only takes films".
                    if (IsWorthReporting(decision.Reason))
                    {
                        items.Add(Skip(rule, candidate, decision.Reason!));
                    }

                    continue;
                }

                if (examined >= examineLimit)
                {
                    items.Add(Skip(
                        rule,
                        candidate,
                        FormattableString.Invariant(
                            $"Stopped after examining {examineLimit} matching item(s). Narrow the rule, or raise its per-run limit, to reach further down the library.")));
                    break;
                }

                examined++;

                var outcome = await ConsiderAsync(rule, candidate, caps, config, dryRun, cancellationToken)
                    .ConfigureAwait(false);
                items.Add(outcome);

                if (outcome.Queued)
                {
                    claimed.Add(candidate.ItemId);
                    queuedForRule++;
                    result.Queued++;
                    result.EstimatedSavingBytes += outcome.EstimatedSavingBytes ?? 0;
                }
            }

            if (!dryRun)
            {
                rule.LastRunAt = now;
                rule.TotalQueued += queuedForRule;
            }

            _logger.LogInformation(
                "[MediaOptimizer] Rule '{Rule}' {Verb} {Count} job(s) out of {Considered} item(s)",
                rule.Name,
                dryRun ? "would queue" : "queued",
                queuedForRule,
                candidates.Count);
        }

        if (!dryRun && rules.Count > 0)
        {
            SaveRuleState();
        }

        result.Items = items;
        return result;
    }

    /// <summary>
    /// Plans and estimates one matching item, and queues it unless something says otherwise.
    /// </summary>
    /// <param name="rule">The rule that matched.</param>
    /// <param name="candidate">The item.</param>
    /// <param name="caps">Server capabilities.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="dryRun">Whether to stop short of queueing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened to this item.</returns>
    private async Task<RuleRunItem> ConsiderAsync(
        AutomationRule rule,
        RuleCandidate candidate,
        Capabilities caps,
        PluginConfiguration config,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var analysis = await _probe.AnalyzeAsync(candidate.ItemId, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return Skip(rule, candidate, "The item no longer exists.");
        }

        if (!analysis.IsEligible)
        {
            return Skip(rule, candidate, analysis.IneligibleReason ?? "This item cannot be converted.");
        }

        var effectiveConfig = config.Clone();
        if (rule.KeepAudioLanguages is not null)
        {
            effectiveConfig.KeepAudioLanguages = rule.KeepAudioLanguages;
        }

        if (rule.KeepSubtitleLanguages is not null)
        {
            effectiveConfig.KeepSubtitleLanguages = rule.KeepSubtitleLanguages;
        }

        var request = StrategyResolver.Resolve(analysis, rule.Strategy, caps, effectiveConfig);

        if (rule.TargetHeight is > 0)
        {
            request.TargetHeight = rule.TargetHeight;
        }

        if (!string.IsNullOrWhiteSpace(rule.OutputContainer))
        {
            request.Container = rule.OutputContainer;
            StrategyResolver.ApplyContainerCompatibility(analysis, request);
        }

        if (rule.OutputPolicy is not null)
        {
            request.OutputPolicy = rule.OutputPolicy.Value;
        }

        request.UseHardware = rule.UseHardware;

        var plan = await _planner
            .PlanAsync(analysis, request, "/dev/null", cancellationToken)
            .ConfigureAwait(false);

        if (!plan.IsRunnable)
        {
            // Dolby Vision ends up here, and that is deliberate: accepting the loss of it is a
            // decision for a person, never for a rule running at four in the morning.
            return Skip(rule, candidate, plan.Warnings.First(w => w.Level == WarningLevel.Blocker).Message);
        }

        var estimate = _estimator.Estimate(analysis, request, plan);
        var worthwhile = RuleMatcher.IsSavingWorthwhile(rule, estimate.CurrentSizeBytes, estimate.EstimatedSizeBytes);
        if (!worthwhile.Matches)
        {
            return Skip(rule, candidate, worthwhile.Reason!);
        }

        var saving = estimate.CurrentSizeBytes - estimate.EstimatedSizeBytes;

        if (dryRun)
        {
            return new RuleRunItem
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ItemId = candidate.ItemId,
                Name = analysis.Name,
                Queued = true,
                EstimatedSavingBytes = saving
            };
        }

        // The candidate list was taken at the start of the run, and a run over a large library
        // spends minutes probing files. Somebody may have queued this one by hand in between.
        if (_store.HasActiveJobForItem(candidate.ItemId))
        {
            return Skip(rule, candidate, "Already queued or converting.");
        }

        var job = new EncodeJob
        {
            ItemId = candidate.ItemId,
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
            "[MediaOptimizer] Rule '{Rule}' queued {Name} ({Saving} bytes predicted)",
            rule.Name,
            analysis.Name,
            saving);

        return new RuleRunItem
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            ItemId = candidate.ItemId,
            Name = analysis.Name,
            Queued = true,
            JobId = job.Id,
            EstimatedSavingBytes = saving
        };
    }

    /// <summary>
    /// Whether a skip is worth showing. Filter misses are the normal case for almost every item in
    /// a library and would drown the interesting ones — a file that matched everything and was
    /// then held back by its predicted saving, or by something the planner refused.
    /// </summary>
    /// <param name="reason">The skip reason.</param>
    /// <returns>Whether to include it in the report.</returns>
    private static bool IsWorthReporting(string? reason) =>
        reason is not null && reason.StartsWith("Already", StringComparison.Ordinal);

    private static RuleRunItem Skip(AutomationRule rule, RuleCandidate candidate, string reason) =>
        new RuleRunItem
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            ItemId = candidate.ItemId,
            Name = candidate.Name,
            Queued = false,
            SkippedReason = reason
        };

    /// <summary>
    /// Persists the run bookkeeping the rules carry. Configuration is the right home for it: the
    /// rules live there, and "last run" and "total queued" are what the dashboard shows to answer
    /// "is this thing actually doing anything?".
    /// </summary>
    private void SaveRuleState()
    {
        try
        {
            _settings.Save();
        }
#pragma warning disable CA1031 // Failing to record bookkeeping must not fail the run that did the work.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Could not save rule bookkeeping");
        }
    }
}
