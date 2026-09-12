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
        typeof(ClientAssetController)
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static bool RequiresElevation(MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>()
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
}
