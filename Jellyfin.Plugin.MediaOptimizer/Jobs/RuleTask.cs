using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Jobs;

/// <summary>
/// Applies the saved automatic rules on a schedule.
/// <para>
/// Runs at three in the morning by default, and queues jobs rather than encoding anything itself,
/// so the queue's own rules still hold: one at a time, paused while anyone is streaming, and
/// nothing touches an original until the result passes verification.
/// </para>
/// </summary>
public class RuleTask : IScheduledTask
{
    private readonly IAutomationService _automation;
    private readonly ILogger<RuleTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="RuleTask"/> class.</summary>
    /// <param name="automation">The rule engine.</param>
    /// <param name="logger">Logger.</param>
    public RuleTask(IAutomationService automation, ILogger<RuleTask> logger)
    {
        _automation = automation;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Optimizer: automatic rules";

    /// <inheritdoc />
    public string Key => "MediaOptimizerRules";

    /// <inheritdoc />
    public string Description =>
        "Queues conversions for library items matching the rules saved in the Media Optimizer dashboard. "
        + "Each rule queues at most the number of files it allows per run.";

    /// <inheritdoc />
    public string Category => "Media Optimizer";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        progress.Report(0);

        var enabled = Plugin.Instance?.Configuration.Rules.Count(r => r.Enabled) ?? 0;
        if (enabled == 0)
        {
            _logger.LogInformation("[MediaOptimizer] No automatic rules are switched on; nothing to do");
            progress.Report(100);
            return;
        }

        var result = await _automation.RunAsync(dryRun: false, ruleId: null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[MediaOptimizer] Automatic rules queued {Queued} job(s) from {Considered} item(s), predicted saving {Saving}",
            result.Queued,
            result.Considered,
            (result.EstimatedSavingBytes / 1024d / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture) + " GiB");

        progress.Report(100);
    }
}
