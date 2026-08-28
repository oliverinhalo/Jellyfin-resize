using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>
/// Matches track languages against a user's keep-list. Containers are wildly inconsistent about
/// how they spell a language — "eng", "en", "en-GB", "English", or nothing at all — so a plain
/// string comparison would quietly drop tracks the user wanted to keep.
/// </summary>
public static class LanguageMatcher
{
    // ISO 639-1 to 639-2/B for the languages that actually turn up in media, plus English names.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["english"] = "eng", ["eng"] = "eng",
        ["fr"] = "fra", ["french"] = "fra", ["fra"] = "fra", ["fre"] = "fra",
        ["de"] = "deu", ["german"] = "deu", ["deu"] = "deu", ["ger"] = "deu",
        ["es"] = "spa", ["spanish"] = "spa", ["spa"] = "spa",
        ["it"] = "ita", ["italian"] = "ita", ["ita"] = "ita",
        ["pt"] = "por", ["portuguese"] = "por", ["por"] = "por",
        ["nl"] = "nld", ["dutch"] = "nld", ["nld"] = "nld", ["dut"] = "nld",
        ["ja"] = "jpn", ["japanese"] = "jpn", ["jpn"] = "jpn",
        ["zh"] = "zho", ["chinese"] = "zho", ["zho"] = "zho", ["chi"] = "zho",
        ["ko"] = "kor", ["korean"] = "kor", ["kor"] = "kor",
        ["ru"] = "rus", ["russian"] = "rus", ["rus"] = "rus",
        ["pl"] = "pol", ["polish"] = "pol", ["pol"] = "pol",
        ["sv"] = "swe", ["swedish"] = "swe", ["swe"] = "swe",
        ["da"] = "dan", ["danish"] = "dan", ["dan"] = "dan",
        ["no"] = "nor", ["norwegian"] = "nor", ["nor"] = "nor",
        ["fi"] = "fin", ["finnish"] = "fin", ["fin"] = "fin",
        ["cs"] = "ces", ["czech"] = "ces", ["ces"] = "ces", ["cze"] = "ces",
        ["hu"] = "hun", ["hungarian"] = "hun", ["hun"] = "hun",
        ["tr"] = "tur", ["turkish"] = "tur", ["tur"] = "tur",
        ["ar"] = "ara", ["arabic"] = "ara", ["ara"] = "ara",
        ["he"] = "heb", ["hebrew"] = "heb", ["heb"] = "heb",
        ["hi"] = "hin", ["hindi"] = "hin", ["hin"] = "hin",
        ["th"] = "tha", ["thai"] = "tha", ["tha"] = "tha",
        ["uk"] = "ukr", ["ukrainian"] = "ukr", ["ukr"] = "ukr",
        ["el"] = "ell", ["greek"] = "ell", ["ell"] = "ell", ["gre"] = "ell",
        ["ro"] = "ron", ["romanian"] = "ron", ["ron"] = "ron", ["rum"] = "ron"
    };

    /// <summary>Reduces any spelling of a language to a single canonical code.</summary>
    /// <param name="language">The raw language tag from the container.</param>
    /// <returns>A canonical code, or null when the track carries no usable language.</returns>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var trimmed = language.Trim();

        // Region subtags such as en-GB or pt-BR still identify the base language.
        var separator = trimmed.IndexOfAny(['-', '_']);
        if (separator > 0)
        {
            trimmed = trimmed[..separator];
        }

        if (Aliases.TryGetValue(trimmed, out var canonical))
        {
            return canonical;
        }

        // "und", "unknown" and similar placeholders mean the track is untagged.
        if (trimmed.Equals("und", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("undetermined", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("mis", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("zxx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>Parses a comma or space separated keep-list into canonical codes.</summary>
    /// <param name="list">The configured list, e.g. "eng, fr".</param>
    /// <returns>Canonical codes; empty when nothing was configured.</returns>
    public static IReadOnlyList<string> ParseList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return Array.Empty<string>();
        }

        return list
            .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Returns true when a track's language is one the user wants to keep.</summary>
    /// <param name="trackLanguage">The track's raw language tag.</param>
    /// <param name="keep">Canonical codes to keep.</param>
    /// <param name="keepUntagged">Whether a track with no language should be kept.</param>
    /// <returns>Whether to keep the track.</returns>
    public static bool ShouldKeep(string? trackLanguage, IReadOnlyList<string> keep, bool keepUntagged)
    {
        if (keep.Count == 0)
        {
            return true;
        }

        var normalized = Normalize(trackLanguage);
        if (normalized is null)
        {
            // An untagged track is usually the main one on a single-language release, so
            // discarding it by default would silently remove the audio people actually want.
            return keepUntagged;
        }

        return keep.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gives a language code a readable name for the interface.</summary>
    /// <param name="language">The raw language tag.</param>
    /// <returns>A display name.</returns>
    public static string Describe(string? language)
    {
        var normalized = Normalize(language);
        if (normalized is null)
        {
            return "untagged";
        }

        foreach (var pair in Aliases)
        {
            if (pair.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase) && pair.Key.Length > 3)
            {
                return char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..];
            }
        }

        return normalized;
    }
}
