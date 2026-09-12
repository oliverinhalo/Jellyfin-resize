using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.MediaOptimizer.Configuration;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Jellyfin persists a plugin's settings with <see cref="XmlSerializer"/>, which is fussier than
/// JSON: it refuses interfaces, it refuses read-only collections, and it has its own ideas about
/// nullable values. Nothing in the plugin can exercise that without a running server, so this does
/// it directly — a settings file that will not round-trip is a plugin that loses every rule the
/// user wrote, and finds out on their machine.
/// </summary>
public class ConfigurationPersistenceTests
{
    private static string Serialize(PluginConfiguration configuration)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, configuration);
        return writer.ToString();
    }

    private static PluginConfiguration Deserialize(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    [Fact]
    public void The_settings_survive_being_written_and_read_back()
    {
        var configuration = new PluginConfiguration
        {
            KeepAudioLanguages = "eng, jpn",
            QuarantineRetentionDays = 14,
            FreeSpaceSafetyFactor = 2.5d,
            Speed = SpeedPreference.SmallestFile,
            DefaultOutputPolicy = OutputPolicy.ReplaceAndDelete,
            RefuseBelowQuality = QualityFloor.SlightlySofter,
            MeasureQualityAfterEncoding = false,
            NotifyOnFailure = false
        };

        var restored = Deserialize(Serialize(configuration));

        Assert.Equal("eng, jpn", restored.KeepAudioLanguages);
        Assert.Equal(14, restored.QuarantineRetentionDays);
        Assert.Equal(2.5d, restored.FreeSpaceSafetyFactor);
        Assert.Equal(SpeedPreference.SmallestFile, restored.Speed);
        Assert.Equal(OutputPolicy.ReplaceAndDelete, restored.DefaultOutputPolicy);

        // A floor that failed to persist would quietly stop refusing anything, which is the one
        // failure of this setting nobody would notice until an original had been replaced.
        Assert.Equal(QualityFloor.SlightlySofter, restored.RefuseBelowQuality);
        Assert.False(restored.MeasureQualityAfterEncoding);
        Assert.False(restored.NotifyOnFailure);
    }

    /// <summary>
    /// The rules are the part that would hurt to lose: they are written by hand, one field at a
    /// time, and they carry nullable numbers and a nullable enum — the shapes XmlSerializer is
    /// most particular about.
    /// </summary>
    [Fact]
    public void A_rule_survives_with_every_field_it_was_given()
    {
        var rule = new AutomationRule
        {
            Name = "Big 4K films",
            Enabled = true,
            Kinds = RuleItemKinds.MoviesOnly,
            MinHeight = 2160,
            MinSizeMb = 20480,
            Container = "mkv",
            VideoCodec = "h264",
            Watched = WatchedFilter.Watched,
            AddedMoreThanDaysAgo = 45,
            Strategy = OptimizationStrategy.Medium,
            TargetHeight = 1080,
            OutputContainer = "mkv",
            OutputPolicy = OutputPolicy.Sidecar,
            KeepAudioLanguages = "eng",
            KeepSubtitleLanguages = "eng, fra",
            UseHardware = true,
            MaxItemsPerRun = 5,
            MinSavingPercent = 20,
            LastRunAt = new DateTime(2026, 5, 30, 3, 0, 0, DateTimeKind.Utc),
            TotalQueued = 7
        };

        var configuration = new PluginConfiguration();
        configuration.Rules.Add(rule);

        var restored = Deserialize(Serialize(configuration));
        var back = Assert.Single(restored.Rules);

        foreach (var property in typeof(AutomationRule).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Equal(property.GetValue(rule), property.GetValue(back));
        }
    }

    /// <summary>
    /// A rule with every optional filter left empty is the common case — "everything, converted
    /// the usual way" — and null has to come back as null, not as zero. A minimum height of 0 and
    /// no minimum height are different rules.
    /// </summary>
    [Fact]
    public void An_empty_filter_comes_back_empty_rather_than_as_zero()
    {
        var configuration = new PluginConfiguration();
        configuration.Rules.Add(new AutomationRule
        {
            Name = "Everything",
            MinHeight = null,
            MinSizeMb = null,
            TargetHeight = null,
            AddedMoreThanDaysAgo = null,
            OutputPolicy = null,
            Container = null,
            VideoCodec = null
        });

        var back = Assert.Single(Deserialize(Serialize(configuration)).Rules);

        Assert.Null(back.MinHeight);
        Assert.Null(back.MinSizeMb);
        Assert.Null(back.TargetHeight);
        Assert.Null(back.AddedMoreThanDaysAgo);
        Assert.Null(back.OutputPolicy);
        Assert.Null(back.Container);
        Assert.Null(back.VideoCodec);
    }

    [Fact]
    public void Several_rules_keep_their_order()
    {
        var configuration = new PluginConfiguration();
        configuration.Rules.AddRange(
        [
            new AutomationRule { Name = "First" },
            new AutomationRule { Name = "Second" },
            new AutomationRule { Name = "Third" }
        ]);

        var restored = Deserialize(Serialize(configuration));

        Assert.Equal(["First", "Second", "Third"], restored.Rules.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// A settings file written by an earlier version has no rules element at all. It must load as
    /// "no rules", not as a crash on upgrade.
    /// </summary>
    [Fact]
    public void Settings_written_before_rules_existed_still_load()
    {
        const string OldXml = """
        <?xml version="1.0" encoding="utf-16"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <DefaultContainer>mp4</DefaultContainer>
          <QuarantineRetentionDays>7</QuarantineRetentionDays>
          <KeepAudioLanguages>eng</KeepAudioLanguages>
        </PluginConfiguration>
        """;

        var restored = Deserialize(OldXml);

        Assert.NotNull(restored.Rules);
        Assert.Empty(restored.Rules);
        Assert.Equal("eng", restored.KeepAudioLanguages);
        Assert.Equal("mp4", restored.DefaultContainer);

        // And the defaults for everything the old file did not know about have to be the ones a
        // fresh install gets, not zero.
        Assert.True(restored.NotifyOnCompletion);
        Assert.True(restored.KeepOriginalsBesideMedia);
        Assert.Equal(1.5d, restored.FreeSpaceSafetyFactor);
    }
}
