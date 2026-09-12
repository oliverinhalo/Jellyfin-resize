using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// A batch run may override one setting for the duration of that run, which means copying the
/// configuration. A copy that silently drops a property is the worst kind of bug: the run does
/// something the settings page says it should not, and nothing anywhere reports it.
/// </summary>
public class ConfigurationCloneTests
{
    /// <summary>
    /// Every writable property must survive the copy, including ones added after this test was
    /// written — which is the entire point of checking it by reflection rather than by listing.
    /// </summary>
    [Fact]
    public void Cloning_carries_every_property()
    {
        var source = new PluginConfiguration();
        var properties = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToList();

        Assert.NotEmpty(properties);

        // Move every property off its default, so a copy that ignores one is caught rather than
        // passing because both sides happen to hold the same default.
        foreach (var property in properties)
        {
            property.SetValue(source, NonDefaultFor(property, property.GetValue(source)));
        }

        var copy = source.Clone();

        Assert.NotSame(source, copy);
        foreach (var property in properties)
        {
            Assert.Equal(property.GetValue(source), property.GetValue(copy));
        }
    }

    [Fact]
    public void A_clone_can_be_changed_without_touching_the_original()
    {
        var source = new PluginConfiguration { KeepAudioLanguages = "eng", KeepOriginalsBesideMedia = true };

        var copy = source.Clone();
        copy.KeepAudioLanguages = "jpn";
        copy.KeepOriginalsBesideMedia = false;

        Assert.Equal("eng", source.KeepAudioLanguages);
        Assert.True(source.KeepOriginalsBesideMedia);
    }

    private static object? NonDefaultFor(PropertyInfo property, object? current)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
        {
            return !(bool)(current ?? false);
        }

        if (type == typeof(string))
        {
            return (current as string) == "changed" ? "changed twice" : "changed";
        }

        if (type == typeof(int))
        {
            return (int)(current ?? 0) + 7;
        }

        if (type == typeof(double))
        {
            return (double)(current ?? 0d) + 0.5d;
        }

        if (type == typeof(long))
        {
            return (long)(current ?? 0L) + 7L;
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToList();
            return values.FirstOrDefault(v => !Equals(v, current)) ?? values[0];
        }

        // A property type this test does not know how to vary would silently weaken it.
        throw new NotSupportedException(
            "Add a case for " + type.Name + " so " + property.Name + " is genuinely exercised.");
    }
}
