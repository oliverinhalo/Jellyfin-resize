using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Every setting has to be both reachable and read. A switch nobody can find is a feature that
/// does not exist; a switch nothing reads is a lie the settings page tells, and both are the kind
/// of thing that survives for years because nothing fails.
/// </summary>
public class ConfigurationSurfaceTests
{
    /// <summary>
    /// Settings deliberately not on the settings page, with the reason. Anything added here needs
    /// one.
    /// </summary>
    private static readonly Dictionary<string, string> NotOnThePage = new(StringComparer.Ordinal)
    {
        // The rules have a panel of their own on the dashboard page, with a preview and an editor;
        // a raw list of them in a text box would be unusable.
        ["Rules"] = "edited in the dashboard's rules panel"
    };

    private static string Root => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static IEnumerable<PropertyInfo> Settings =>
        typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    [Fact]
    public void There_is_something_to_read_for_every_setting()
    {
        Assert.True(Settings.Count() > 20, "The configuration has shrunk unexpectedly.");
    }

    /// <summary>A setting nothing reads does nothing, however carefully the page describes it.</summary>
    [Fact]
    public void Every_setting_is_read_somewhere_in_the_plugin()
    {
        var sources = Directory
            .GetFiles(Path.Combine(Root, "Jellyfin.Plugin.MediaOptimizer"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("PluginConfiguration.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(sources);

        var unread = Settings
            .Where(p => !sources.Any(text => text.Contains("." + p.Name, StringComparison.Ordinal)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            unread.Count == 0,
            "These settings are never read, so changing them does nothing: " + string.Join(", ", unread));
    }

    /// <summary>
    /// And a setting nobody can reach is one only somebody willing to edit XML by hand can use.
    /// This caught <c>MeasureQualityWhenSampling</c>, which was added with the quality measurement
    /// and left off the page.
    /// </summary>
    [Fact]
    public void Every_setting_can_be_changed_from_a_page()
    {
        var pages = Directory
            .GetFiles(Path.Combine(Root, "Jellyfin.Plugin.MediaOptimizer", "Configuration"), "*.html")
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(pages);

        var unreachable = Settings
            .Where(p => !NotOnThePage.ContainsKey(p.Name))
            .Where(p => !pages.Any(html =>
                html.Contains("\"" + p.Name + "\"", StringComparison.Ordinal)
                || html.Contains("'" + p.Name + "'", StringComparison.Ordinal)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            unreachable.Count == 0,
            "These settings cannot be changed from any page, so only somebody willing to edit the "
            + "plugin's XML by hand can use them: " + string.Join(", ", unreachable));
    }

    /// <summary>
    /// A control on the page that the page's own save list forgets is worse than no control: it
    /// shows a value, accepts a change, and silently discards it.
    /// </summary>
    [Fact]
    public void Every_control_on_the_settings_page_is_one_the_page_saves()
    {
        var path = Path.Combine(Root, "Jellyfin.Plugin.MediaOptimizer", "Configuration", "configPage.html");
        var html = File.ReadAllText(path);

        var controls = System.Text.RegularExpressions.Regex
            .Matches(html, "id=\"(?<id>[A-Za-z]+)\"")
            .Select(m => m.Groups["id"].Value)
            .Where(id => Settings.Any(p => p.Name == id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(controls);

        var notSaved = controls
            .Where(id => !html.Contains("'" + id + "'", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            notSaved.Count == 0,
            "These controls are rendered but not in the page's save list, so editing them does "
            + "nothing: " + string.Join(", ", notSaved));
    }
}
