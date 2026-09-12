using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaOptimizer.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Who is allowed to call what. These are assertions about the plugin's attack surface rather
/// than about its behaviour: an endpoint that quietly loses its authorization attribute in a
/// refactor is not something any functional test would notice.
/// </summary>
public class ApiSurfaceTests
{
    private static readonly Type[] Controllers =
    [
        typeof(MediaOptimizerController),
        typeof(QueueControlController),
        typeof(DiagnosticsController),
        typeof(RulesController),
        typeof(ClientAssetController)
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    /// <summary>
    /// Whether an administrator is required, counting a policy declared on the controller as
    /// covering every action on it — which is how ASP.NET actually applies it.
    /// </summary>
    /// <param name="action">The action method.</param>
    /// <returns>Whether the action demands an administrator.</returns>
    private static bool RequiresElevation(MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(action.DeclaringType?.GetCustomAttributes<AuthorizeAttribute>() ?? [])
            .Any(a => string.Equals(a.Policy, "RequiresElevation", StringComparison.Ordinal));

    private static string Route(MethodInfo action) =>
        action.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault()?.Template ?? action.Name;

    /// <summary>
    /// POST endpoints that change nothing. "Estimate" takes a whole proposed conversion as its
    /// body, which is why it is a POST at all; it writes nothing and is gated by the same
    /// "non-administrators may inspect files" setting as the analysis it is built on. Anything
    /// added here needs that same justification in writing.
    /// </summary>
    private static readonly string[] ReadOnlyPosts = [nameof(MediaOptimizerController.Estimate)];

    /// <summary>
    /// Anything that writes — starting, cancelling, reverting a conversion, or changing the queue
    /// — rewrites files in someone's library. Being signed in is not enough.
    /// </summary>
    [Fact]
    public void Every_state_changing_endpoint_demands_an_administrator()
    {
        var offenders = new List<string>();

        foreach (var controller in Controllers)
        {
            foreach (var action in Actions(controller))
            {
                var writes = action.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(a => a.HttpMethods)
                    .Any(m => m is "POST" or "PUT" or "DELETE" or "PATCH");

                if (writes && !RequiresElevation(action) && !ReadOnlyPosts.Contains(action.Name))
                {
                    offenders.Add(controller.Name + "." + action.Name + " (" + Route(action) + ")");
                }
            }
        }

        Assert.True(offenders.Count == 0, "These endpoints change state without requiring an administrator: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Reads that hand back paths on the server's filesystem, or that make the server read an
    /// entire media file, are administrator-only too. A signed-in user can browse the library in
    /// Jellyfin; that does not mean they should be able to enumerate where every file lives, or
    /// ask the server to read a 60 GB remux end to end on demand.
    /// </summary>
    [Theory]
    [InlineData(nameof(MediaOptimizerController.SearchLibrary))]
    [InlineData(nameof(MediaOptimizerController.GetFacets))]
    [InlineData(nameof(MediaOptimizerController.GetJobs))]
    [InlineData(nameof(MediaOptimizerController.GetJob))]
    [InlineData(nameof(MediaOptimizerController.MeasureBitrate))]
    public void Sensitive_reads_demand_an_administrator(string methodName)
    {
        var action = typeof(MediaOptimizerController).GetMethod(methodName);
        Assert.NotNull(action);
        Assert.True(RequiresElevation(action!), methodName + " must require an administrator.");
    }

    /// <summary>
    /// Exactly one endpoint may be anonymous: the script tag the browser adds to index.html
    /// carries no credentials, so the bundle has to be fetchable without them. Anything else
    /// anonymous is reachable by anyone who can reach the server at all.
    /// </summary>
    [Fact]
    public void Only_the_client_script_is_reachable_without_signing_in()
    {
        var anonymous = new List<string>();

        foreach (var controller in Controllers)
        {
            foreach (var action in Actions(controller))
            {
                if (action.GetCustomAttributes<AllowAnonymousAttribute>().Any())
                {
                    anonymous.Add(controller.Name + "." + action.Name);
                }
            }
        }

        Assert.Equal(["ClientAssetController.GetClientScript"], anonymous);
    }

    /// <summary>
    /// The removed transformation endpoint accepted a document from anyone and returned it as
    /// text/html on the Jellyfin origin, which is a cross-site scripting sink whatever it was for.
    /// Nothing may reintroduce an anonymous endpoint that answers with HTML.
    /// </summary>
    [Fact]
    public void No_anonymous_endpoint_answers_with_html()
    {
        foreach (var controller in Controllers)
        {
            foreach (var action in Actions(controller))
            {
                if (!action.GetCustomAttributes<AllowAnonymousAttribute>().Any())
                {
                    continue;
                }

                var produces = action.GetCustomAttributes<ProducesAttribute>()
                    .Concat(controller.GetCustomAttributes<ProducesAttribute>())
                    .SelectMany(p => p.ContentTypes);

                Assert.DoesNotContain("text/html", produces, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// A rule rewrites files on a schedule, so reading, writing and previewing them is all
    /// administrator-only — including the read, because a rule describes the library.
    /// </summary>
    [Fact]
    public void Every_rule_endpoint_demands_an_administrator()
    {
        var controllerPolicy = typeof(RulesController).GetCustomAttributes<AuthorizeAttribute>()
            .Any(a => string.Equals(a.Policy, "RequiresElevation", StringComparison.Ordinal));

        Assert.True(controllerPolicy, "The rules controller must require an administrator for everything on it.");
        Assert.All(Actions(typeof(RulesController)), action =>
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>()));
    }

    /// <summary>
    /// A rule with no ceiling queues the whole library on its first run, at three in the morning.
    /// The validation is the only thing standing between a typo and that.
    /// </summary>
    [Theory]
    [InlineData(0, 15, "between 1 and 500")]
    [InlineData(100000, 15, "between 1 and 500")]
    [InlineData(3, 150, "between 0 and 99")]
    [InlineData(3, -5, "between 0 and 99")]
    public void An_unusable_rule_is_refused_with_a_reason(int perRun, int savingPercent, string expected)
    {
        var error = RulesController.Validate(new Jellyfin.Plugin.MediaOptimizer.Models.AutomationRule
        {
            Name = "Rule",
            MaxItemsPerRun = perRun,
            MinSavingPercent = savingPercent
        });

        Assert.NotNull(error);
        Assert.Contains(expected, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_without_a_name_is_refused()
    {
        var error = RulesController.Validate(new Jellyfin.Plugin.MediaOptimizer.Models.AutomationRule
        {
            Name = "   ",
            MaxItemsPerRun = 3,
            MinSavingPercent = 15
        });

        Assert.NotNull(error);
        Assert.Contains("name", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sensible_rule_is_accepted()
    {
        Assert.Null(RulesController.Validate(new Jellyfin.Plugin.MediaOptimizer.Models.AutomationRule
        {
            Name = "Big 4K films",
            MaxItemsPerRun = 3,
            MinSavingPercent = 15,
            MinHeight = 2160,
            TargetHeight = 1080,
            MinSizeMb = 20480,
            AddedMoreThanDaysAgo = 30
        }));
    }

    /// <summary>The script is cacheable, so the browser has to be able to tell when it changed.</summary>
    [Fact]
    public void Etag_matching_understands_the_shapes_browsers_send()
    {
        Assert.True(ClientAssetController.MatchesEtag(new Microsoft.Extensions.Primitives.StringValues("\"abc\""), "\"abc\""));
        Assert.True(ClientAssetController.MatchesEtag(new Microsoft.Extensions.Primitives.StringValues("W/\"abc\""), "\"abc\""));
        Assert.True(ClientAssetController.MatchesEtag(new Microsoft.Extensions.Primitives.StringValues("\"old\", \"abc\""), "\"abc\""));
        Assert.True(ClientAssetController.MatchesEtag(new Microsoft.Extensions.Primitives.StringValues("*"), "\"abc\""));
        Assert.False(ClientAssetController.MatchesEtag(new Microsoft.Extensions.Primitives.StringValues("\"old\""), "\"abc\""));
        Assert.False(ClientAssetController.MatchesEtag(default(Microsoft.Extensions.Primitives.StringValues), "\"abc\""));
    }

    // --- what a non-administrator is allowed to learn -----------------------------------------

    /// <summary>
    /// The analysis dialog is the one thing a non-administrator may open, and it was handing back
    /// the absolute path of the file on the server — the very thing every other read on this
    /// controller is administrator-only to prevent, and which the diagnostics page on this same
    /// branch deliberately hides. The file name is what the dialog needs; the directory it sits in
    /// is a description of somebody else's server.
    /// </summary>
    [Fact]
    public void A_non_administrator_is_not_told_where_the_file_lives()
    {
        var analysis = new Jellyfin.Plugin.MediaOptimizer.Models.FileAnalysis
        {
            Path = "/srv/media/films/Arrival (2016)/Arrival.mkv",
            IneligibleReason = "Could not read /srv/media/films/Arrival (2016)/Arrival.mkv.",
            StreamInfoError = "ffprobe: /srv/media/films/Arrival (2016)/Arrival.mkv: Invalid data found"
        };

        MediaOptimizerController.RedactServerPaths(analysis);

        Assert.Equal("Arrival.mkv", analysis.Path);
        Assert.DoesNotContain("/srv/media", analysis.IneligibleReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/media", analysis.StreamInfoError!, StringComparison.Ordinal);

        // And what is left still names the file, so the message is still worth reading.
        Assert.Contains("Arrival.mkv", analysis.IneligibleReason!, StringComparison.Ordinal);
    }

    /// <summary>Redacting a file with no path must not turn its messages into nonsense.</summary>
    [Fact]
    public void Redaction_copes_with_an_item_that_has_no_path()
    {
        var analysis = new Jellyfin.Plugin.MediaOptimizer.Models.FileAnalysis
        {
            Path = string.Empty,
            IneligibleReason = "This item has no file path on disk."
        };

        MediaOptimizerController.RedactServerPaths(analysis);

        Assert.Equal(string.Empty, analysis.Path);
        Assert.Equal("This item has no file path on disk.", analysis.IneligibleReason);
    }

    /// <summary>
    /// The redaction only helps if the one endpoint a non-administrator can reach actually
    /// applies it, which no test of the helper on its own would notice.
    /// </summary>
    [Fact]
    public void The_analysis_endpoint_applies_that_redaction()
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Jellyfin.Plugin.MediaOptimizer", "Api", "MediaOptimizerController.cs"));

        Assert.True(System.IO.File.Exists(path), "Could not find " + path);

        var text = System.IO.File.ReadAllText(path);
        var start = text.IndexOf("public async Task<ActionResult<FileAnalysis>> Analyze", StringComparison.Ordinal);
        Assert.True(start > 0, "The analysis endpoint has been renamed; this test needs updating.");

        var body = text[start..Math.Min(text.Length, start + 2500)];
        Assert.Contains("RedactServerPaths", body, StringComparison.Ordinal);
    }
}
