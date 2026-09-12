using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// The saved automatic rules, and the ability to see what they would do before switching one on.
/// Everything here is administrator-only: a rule rewrites files in the library on a schedule.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MediaOptimizer/Rules")]
[Produces(MediaTypeNames.Application.Json)]
public class RulesController : ControllerBase
{
    private readonly IAutomationService _automation;
    private readonly IPluginConfigurationSource _settings;
    private readonly ILogger<RulesController> _logger;

    /// <summary>Initializes a new instance of the <see cref="RulesController"/> class.</summary>
    /// <param name="automation">The rule engine.</param>
    /// <param name="settings">Plugin settings, which hold the rules.</param>
    /// <param name="logger">Logger.</param>
    public RulesController(
        IAutomationService automation,
        IPluginConfigurationSource settings,
        ILogger<RulesController> logger)
    {
        _automation = automation;
        _settings = settings;
        _logger = logger;
    }

    private List<AutomationRule> Rules => _settings.Configuration.Rules;

    /// <summary>Lists the saved rules.</summary>
    /// <returns>Every rule, in the order they are applied.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AutomationRule>> GetRules() => Ok(Rules);

    /// <summary>Creates or replaces a rule.</summary>
    /// <param name="rule">The rule to save.</param>
    /// <returns>The saved rule, with the id it was stored under.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AutomationRule> SaveRule([FromBody] AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var error = Validate(rule);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        if (rule.Id == Guid.Empty)
        {
            rule.Id = Guid.NewGuid();
        }

        var rules = Rules;
        var existing = rules.FindIndex(r => r.Id == rule.Id);

        if (existing >= 0)
        {
            // The bookkeeping belongs to the rule's history, not to what the form posted back.
            rule.LastRunAt = rules[existing].LastRunAt;
            rule.TotalQueued = rules[existing].TotalQueued;
            rules[existing] = rule;
        }
        else
        {
            rules.Add(rule);
        }

        _settings.Save();
        _logger.LogInformation("[MediaOptimizer] Saved rule '{Name}' ({Enabled})", rule.Name, rule.Enabled ? "on" : "off");
        return Ok(rule);
    }

    /// <summary>Deletes a rule.</summary>
    /// <param name="id">The rule id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteRule([FromRoute] Guid id)
    {
        if (Rules.RemoveAll(r => r.Id == id) == 0)
        {
            return NotFound();
        }

        _settings.Save();
        return NoContent();
    }

    /// <summary>
    /// Reports what a rule would queue, without queueing anything. This is the answer to "what
    /// will this do to my library", and it is why a rule can be written with some confidence
    /// before it is switched on.
    /// </summary>
    /// <param name="id">The rule to preview.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The items it would take, and the near-misses.</returns>
    [HttpPost("{id}/Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuleRunResult>> Preview(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        if (Rules.All(r => r.Id != id))
        {
            return NotFound();
        }

        return Ok(await _automation.RunAsync(dryRun: true, ruleId: id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Runs one rule now, or every enabled rule when no id is given.</summary>
    /// <param name="id">The rule to run, or null for all enabled rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was queued.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RuleRunResult>> Run(
        [FromQuery] Guid? id,
        CancellationToken cancellationToken)
    {
        var result = await _automation.RunAsync(dryRun: false, ruleId: id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("[MediaOptimizer] Manual rule run queued {Count} job(s)", result.Queued);
        return Ok(result);
    }

    /// <summary>
    /// Rejects a rule that would behave in a way nobody could have meant. The ceiling matters
    /// most: a rule with no limit, written at midnight, queues the whole library.
    /// </summary>
    /// <param name="rule">The rule as posted.</param>
    /// <returns>An error message, or null when the rule is sane.</returns>
    internal static string? Validate(AutomationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return "A rule needs a name, so the queue can say what put a job there.";
        }

        if (rule.MaxItemsPerRun < 1 || rule.MaxItemsPerRun > 500)
        {
            return "A rule must queue between 1 and 500 files per run.";
        }

        if (rule.MinSavingPercent is < 0 or > 99)
        {
            return "The minimum saving must be between 0 and 99 percent.";
        }

        if (rule.MinHeight is not null && (rule.MinHeight < 64 || rule.MinHeight > 4320))
        {
            return "The minimum height must be a real resolution, between 64 and 4320.";
        }

        if (rule.TargetHeight is not null && (rule.TargetHeight < 64 || rule.TargetHeight > 4320))
        {
            return "The target height must be a real resolution, between 64 and 4320.";
        }

        if (rule.MinSizeMb is < 0)
        {
            return "The minimum size cannot be negative.";
        }

        if (rule.AddedMoreThanDaysAgo is < 0)
        {
            return "The age filter cannot be negative.";
        }

        return null;
    }
}
