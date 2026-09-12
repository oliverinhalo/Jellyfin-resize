using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// What the rules actually do to a library when nobody is watching. The matcher decides what is
/// eligible; this decides how many, in what order, and what stops one.
/// </summary>
public class AutomationServiceTests
{
    private sealed class Settings : IPluginConfigurationSource
    {
        public Settings(PluginConfiguration configuration) => Configuration = configuration;

        public PluginConfiguration Configuration { get; }

        public int Saves { get; private set; }

        public void Save() => Saves++;
    }

    private sealed class Candidates : ILibraryCandidateSource
    {
        private readonly IReadOnlyList<RuleCandidate> _items;

        public Candidates(IReadOnlyList<RuleCandidate> items) => _items = items;

        public IReadOnlyList<RuleCandidate> GetCandidates(CancellationToken cancellationToken) => _items;
    }

    /// <summary>Answers for any item, sized from the candidate list so estimates differ per file.</summary>
    private sealed class Probe : IMediaProbeService
    {
        private readonly IReadOnlyList<RuleCandidate> _items;

        public Probe(IReadOnlyList<RuleCandidate> items) => _items = items;

        public HashSet<Guid> Ineligible { get; } = new HashSet<Guid>();

        /// <summary>Gets how many files were probed, which is the cost a preview has to bound.</summary>
        public int Analyses { get; private set; }

        public Task<FileAnalysis?> AnalyzeAsync(Guid itemId, CancellationToken cancellationToken)
        {
            Analyses++;

            var candidate = _items.FirstOrDefault(i => i.ItemId == itemId);
            if (candidate is null)
            {
                return Task.FromResult<FileAnalysis?>(null);
            }

            var analysis = new FileAnalysis
            {
                ItemId = itemId,
                Name = candidate.Name,
                Path = "/media/" + candidate.Name + ".mkv",
                Container = candidate.Container ?? "mkv",
                SizeBytes = candidate.SizeBytes,
                DurationSeconds = 7200,
                IsEligible = !Ineligible.Contains(itemId),
                IneligibleReason = Ineligible.Contains(itemId) ? "This item is a disc folder rip." : null,
                IsWritable = true,
                Video = new VideoTrackInfo
                {
                    Index = 0,
                    Codec = candidate.VideoCodec ?? "h264",
                    Width = (candidate.Height ?? 1080) * 16 / 9,
                    Height = candidate.Height ?? 1080,
                    BitDepth = 8,
                    FrameRate = 24f,
                    Range = "SDR",
                    Bitrate = new BitrateInfo
                    {
                        Bps = (long)((candidate.SizeBytes ?? 1_000_000_000) * 8d / 7200d),
                        Source = ValueSource.Measured
                    }
                },
                Audio = [new AudioTrackInfo { Index = 1, TypeIndex = 0, Codec = "ac3", Channels = 6, SampleRate = 48000 }]
            };

            return Task.FromResult<FileAnalysis?>(analysis);
        }

        public Task<long?> MeasureBitrateAsync(string path, string specifier, double duration, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(null);
    }

    private sealed class Caps : ICapabilityService
    {
        private readonly Capabilities _caps = new Capabilities
        {
            VideoEncoders =
            [
                new EncoderOption { Name = "libx265", Codec = "hevc", DisplayName = "x265", Supports10Bit = true, Presets = ["medium"] }
            ],
            AudioEncoders = [new EncoderOption { Name = "libopus", Codec = "opus", DisplayName = "Opus" }],
            AllowHevcEncoding = true,
            AllowAv1Encoding = true
        };

        public Task<Capabilities> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_caps);

        public Task<bool> HasEncoderAsync(string encoder, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Store : IJobStore
    {
        public List<EncodeJob> Jobs { get; } = new List<EncodeJob>();

        public bool IsPaused { get; set; }

        public void Add(EncodeJob job) => Jobs.Add(job);

        public void Update(EncodeJob job)
        {
        }

        public EncodeJob? Get(Guid id) => Jobs.FirstOrDefault(j => j.Id == id);

        public IReadOnlyList<EncodeJob> GetAll() => Jobs;

        public IReadOnlyList<EncodeJob> GetActive() => Jobs.Where(j => j.IsActive).ToList();

        public EncodeJob? TakeNextQueued(Predicate<EncodeJob>? canStart = null) => null;

        public bool HasActiveJobForItem(Guid itemId) => Jobs.Any(j => j.ItemId == itemId && j.IsActive);

        public bool RequestCancel(Guid id) => false;

        public bool Remove(Guid id) => false;

        public IReadOnlyList<EncodeJob> ReconcileInterrupted() => Array.Empty<EncodeJob>();
    }

    private static RuleCandidate Item(string name, long gib, int height = 2160, string codec = "h264") =>
        new RuleCandidate
        {
            ItemId = Guid.NewGuid(),
            Name = name,
            ItemType = "Movie",
            Container = "mkv",
            VideoCodec = codec,
            Height = height,
            SizeBytes = gib * 1024 * 1024 * 1024,
            IsWatched = true,
            DateCreated = DateTime.UtcNow.AddYears(-2)
        };

    private static (AutomationService Service, Store Store, Settings Settings, Probe Probe) Build(
        IReadOnlyList<RuleCandidate> items,
        params AutomationRule[] rules)
    {
        var configuration = new PluginConfiguration();
        configuration.Rules.AddRange(rules);

        var settings = new Settings(configuration);
        var store = new Store();
        var probe = new Probe(items);
        var planner = new EncodePlanner(new Caps(), NullLogger<EncodePlanner>.Instance);

        var service = new AutomationService(
            settings,
            new Candidates(items),
            probe,
            planner,
            new SizeEstimator(store),
            new Caps(),
            store,
            NullLogger<AutomationService>.Instance);

        return (service, store, settings, probe);
    }

    private static AutomationRule Rule(string name = "Shrink the big ones") => new AutomationRule
    {
        Name = name,
        Enabled = true,
        Strategy = OptimizationStrategy.Medium,
        AddedMoreThanDaysAgo = null,
        MinSavingPercent = 0,
        MaxItemsPerRun = 3
    };

    [Fact]
    public async Task Nothing_happens_when_no_rule_is_switched_on()
    {
        var rule = Rule();
        rule.Enabled = false;
        var (service, store, _, _) = Build([Item("Film", 40)], rule);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(0, result.Queued);
        Assert.Empty(store.Jobs);
    }

    /// <summary>
    /// The ceiling is the whole safety story of an automatic rule. Without it, one run queues the
    /// library.
    /// </summary>
    [Fact]
    public async Task A_rule_queues_no_more_than_its_ceiling()
    {
        var items = Enumerable.Range(1, 10).Select(i => Item("Film " + i, 40)).ToList();
        var rule = Rule();
        rule.MaxItemsPerRun = 3;

        var (service, store, _, _) = Build(items, rule);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(3, result.Queued);
        Assert.Equal(3, store.Jobs.Count);
    }

    /// <summary>Reclaiming space means starting with the files that have space to reclaim.</summary>
    [Fact]
    public async Task The_biggest_files_are_taken_first()
    {
        var items = new List<RuleCandidate>
        {
            Item("Small", 2),
            Item("Huge", 60),
            Item("Medium", 20)
        };

        var rule = Rule();
        rule.MaxItemsPerRun = 2;

        var (service, store, _, _) = Build(items, rule);
        await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(["Huge", "Medium"], store.Jobs.Select(j => j.ItemName).ToArray());
    }

    [Fact]
    public async Task A_preview_queues_nothing_but_says_what_it_would_take()
    {
        var items = new List<RuleCandidate> { Item("Film", 40) };
        var rule = Rule();

        var (service, store, settings, _) = Build(items, rule);

        var result = await service.RunAsync(dryRun: true, ruleId: rule.Id, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Queued);
        Assert.Empty(store.Jobs);
        Assert.Equal(0, settings.Saves);
        Assert.Null(rule.LastRunAt);
        Assert.True(result.EstimatedSavingBytes > 0);
    }

    /// <summary>
    /// Previewing a rule that is switched off is the entire point of previewing: a new rule is
    /// saved off, and the way to decide whether to switch it on is to see what it would take. A
    /// preview that answered "nothing, because the rule is off" would be useless.
    /// </summary>
    [Fact]
    public async Task A_disabled_rule_can_still_be_previewed_by_id()
    {
        var rule = Rule();
        rule.Enabled = false;
        var (service, store, _, _) = Build([Item("Film", 40)], rule);

        var result = await service.RunAsync(dryRun: true, ruleId: rule.Id, CancellationToken.None);

        Assert.Equal(1, result.Queued);
        Assert.Empty(store.Jobs);
    }

    /// <summary>
    /// Running one rule by hand is an explicit act too, so it works on a rule that is off — but
    /// the scheduled run, which passes no id, must never touch one.
    /// </summary>
    [Fact]
    public async Task A_disabled_rule_runs_when_asked_for_by_name_but_never_on_the_schedule()
    {
        var rule = Rule();
        rule.Enabled = false;
        var (service, store, _, _) = Build([Item("Film", 40)], rule);

        var scheduled = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);
        Assert.Equal(0, scheduled.Queued);
        Assert.Empty(store.Jobs);

        var byHand = await service.RunAsync(dryRun: false, ruleId: rule.Id, CancellationToken.None);
        Assert.Equal(1, byHand.Queued);
        Assert.Single(store.Jobs);
    }

    [Fact]
    public async Task A_real_run_records_when_it_ran_and_what_it_queued()
    {
        var rule = Rule();
        var (service, _, settings, _) = Build([Item("Film", 40)], rule);

        await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.NotNull(rule.LastRunAt);
        Assert.Equal(1, rule.TotalQueued);
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public async Task An_item_the_probe_refuses_is_skipped_with_its_reason()
    {
        var item = Item("Disc rip", 40);
        var (service, store, _, probe) = Build([item], Rule());
        probe.Ineligible.Add(item.ItemId);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(0, result.Queued);
        Assert.Empty(store.Jobs);
        Assert.Contains(result.Items, i => i.SkippedReason!.Contains("disc folder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A file that is already efficiently encoded at its resolution saves almost nothing. Spending
    /// hours of CPU and a generation of quality on it is the opposite of optimising.
    /// </summary>
    [Fact]
    public async Task A_file_with_nothing_to_gain_is_left_alone()
    {
        // Already HEVC at 1080p, and the rule keeps the resolution, so the model predicts
        // essentially no saving.
        var item = Item("Already lean", 8, height: 1080, codec: "hevc");
        var rule = Rule();
        rule.Strategy = OptimizationStrategy.Standard;
        rule.MinSavingPercent = 20;

        var (service, store, _, _) = Build([item], rule);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(0, result.Queued);
        Assert.Empty(store.Jobs);
        Assert.Contains(result.Items, i => i.SkippedReason!.Contains("below the rule's 20% floor", StringComparison.Ordinal));
    }

    /// <summary>Two rules that both match a file must not both queue it.</summary>
    [Fact]
    public async Task Overlapping_rules_do_not_queue_the_same_file_twice()
    {
        var items = new List<RuleCandidate> { Item("Film", 40) };
        var first = Rule("First");
        var second = Rule("Second");

        var (service, store, _, _) = Build(items, first, second);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(1, result.Queued);
        Assert.Single(store.Jobs);
    }

    /// <summary>
    /// Matching is free; deciding is not — every item that passes the filters is probed and
    /// planned. A rule whose matches all fall below its saving floor would otherwise walk the
    /// whole library doing that, inside a single HTTP request when this is a preview.
    /// </summary>
    [Fact]
    public async Task A_rule_whose_matches_all_fall_short_stops_instead_of_probing_the_library()
    {
        var items = Enumerable.Range(1, 200).Select(i => Item("Film " + i, 40, height: 1080, codec: "hevc")).ToList();

        var rule = Rule();
        rule.Strategy = OptimizationStrategy.Standard;
        rule.MinSavingPercent = 90;   // nothing will ever clear this
        rule.MaxItemsPerRun = 1;      // so the examine limit is its floor of 25

        var (service, store, _, probe) = Build(items, rule);

        var result = await service.RunAsync(dryRun: true, ruleId: rule.Id, CancellationToken.None);

        Assert.Equal(0, result.Queued);
        Assert.Empty(store.Jobs);
        Assert.Equal(25, probe.Analyses);
        Assert.Contains(result.Items, i => i.SkippedReason!.Contains("Stopped after examining", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_rule_that_queued_a_job_is_recorded_against_it()
    {
        var rule = Rule("Weekend cleanup");
        var (service, _, _, _) = Build([Item("Film", 40)], rule);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        var queued = Assert.Single(result.Items, i => i.Queued);
        Assert.Equal("Weekend cleanup", queued.RuleName);
        Assert.Equal(rule.Id, queued.RuleId);
        Assert.NotNull(queued.JobId);
    }

    /// <summary>
    /// A rule's own settings have to reach the job, or the preview and the result describe
    /// different conversions.
    /// </summary>
    [Fact]
    public async Task The_rules_own_settings_reach_the_queued_job()
    {
        var rule = Rule();
        rule.TargetHeight = 1080;
        rule.OutputContainer = "mkv";
        rule.OutputPolicy = OutputPolicy.Sidecar;
        rule.KeepAudioLanguages = "eng";

        var (service, store, _, _) = Build([Item("Film", 40)], rule);
        await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        var job = Assert.Single(store.Jobs);
        Assert.Equal(1080, job.Request.TargetHeight);
        Assert.Equal("mkv", job.Request.Container);
        Assert.Equal(OutputPolicy.Sidecar, job.OutputPolicy);
        Assert.Equal(OutputPolicy.Sidecar, job.Request.OutputPolicy);
    }

    /// <summary>
    /// Overriding the language list for one rule must not rewrite the saved settings, which is
    /// exactly the kind of thing that only shows up months later.
    /// </summary>
    [Fact]
    public async Task A_rules_language_override_does_not_change_the_saved_settings()
    {
        var rule = Rule();
        rule.KeepAudioLanguages = "jpn";

        var (service, _, settings, _) = Build([Item("Film", 40)], rule);
        settings.Configuration.KeepAudioLanguages = "eng";

        await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal("eng", settings.Configuration.KeepAudioLanguages);
    }

    /// <summary>
    /// The candidate list is taken once, and a run over a large library spends minutes probing
    /// files. Somebody queueing the same item by hand in between must not get a second job.
    /// </summary>
    [Fact]
    public async Task An_item_queued_by_hand_mid_run_is_not_queued_again()
    {
        var item = Item("Film", 40);
        var (service, store, _, _) = Build([item], Rule());

        // Exactly what a user clicking "Optimize…" during the run would leave behind.
        store.Add(new EncodeJob { ItemId = item.ItemId, ItemName = "Film", Status = JobStatus.Queued });

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(0, result.Queued);
        Assert.Single(store.Jobs);
        Assert.Contains(result.Items, i => i.SkippedReason!.Contains("Already queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_already_converted_by_this_plugin_is_reported_rather_than_silently_dropped()
    {
        var item = Item("Film", 40);
        item.PreviouslyOptimized = true;

        var (service, store, _, _) = Build([item], Rule());

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Empty(store.Jobs);
        Assert.Contains(result.Items, i => !i.Queued && i.SkippedReason!.StartsWith("Already converted", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two rules that both match a file: the first one in the list takes it, and the second never
    /// sees it. That is the whole reason the order can be changed from the dashboard — "keep the
    /// 4K films as they are, shrink everything else" is only that sentence if the keeping rule is
    /// above the shrinking one.
    /// </summary>
    [Fact]
    public async Task The_first_rule_in_the_list_claims_a_file_the_second_would_also_take()
    {
        var film = Item("Overlapping film", 40);

        var first = Rule("Runs first");
        first.Strategy = OptimizationStrategy.Medium;
        var second = Rule("Runs second");
        second.Strategy = OptimizationStrategy.HighReduction;

        var (service, store, _, _) = Build([film], first, second);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(1, result.Queued);
        var job = Assert.Single(store.Jobs);
        Assert.Equal("Runs first", job.QueuedByRule);

        // And with the order reversed, the other rule takes it -- same library, same two rules.
        var reversed = Build([film], second, first);
        var again = await reversed.Service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(1, again.Queued);
        Assert.Equal("Runs second", Assert.Single(reversed.Store.Jobs).QueuedByRule);
    }

    /// <summary>
    /// A rule already narrows a library down, which makes it the one place where "this is all
    /// animation" can be true of every file it takes — so the rule's answer has to reach the job
    /// it queues, not stop at the form.
    /// </summary>
    [Fact]
    public async Task A_rules_content_tuning_reaches_the_job_it_queues()
    {
        var rule = Rule("The anime library");
        rule.Tune = ContentTune.Animation;

        var (service, store, _, _) = Build([Item("An episode", 8, height: 1080)], rule);

        var result = await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(1, result.Queued);
        Assert.Equal(ContentTune.Animation, Assert.Single(store.Jobs).Request.Tune);
    }

    [Fact]
    public async Task A_rule_that_says_nothing_about_content_leaves_the_encoders_default_alone()
    {
        var (service, store, _, _) = Build([Item("A film", 40)], Rule());

        await service.RunAsync(dryRun: false, ruleId: null, CancellationToken.None);

        Assert.Equal(ContentTune.Auto, Assert.Single(store.Jobs).Request.Tune);
    }
}
