using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaOptimizer.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaOptimizer.Configuration;

/// <summary>Where a finished encode is placed relative to the original file.</summary>
public enum OutputPolicy
{
    /// <summary>Write beside/into a separate directory. The original is never modified.</summary>
    Sidecar = 0,

    /// <summary>Register the result as an alternate version of the same library item.</summary>
    AlternateVersion = 1,

    /// <summary>
    /// Replace the original, keeping it for the retention period so the change can be undone.
    /// </summary>
    Replace = 2,

    /// <summary>
    /// Replace the original and delete it as soon as the result passes verification. Frees the
    /// space immediately; there is no undo.
    /// </summary>
    ReplaceAndDelete = 3
}

/// <summary>How the client script is delivered into jellyfin-web.</summary>
public enum InjectionMode
{
    /// <summary>Register with the File Transformation plugin. Non-destructive.</summary>
    FileTransformation = 0,

    /// <summary>Patch jellyfin-web/index.html on disk. Destructive, undone by server updates.</summary>
    PatchIndexHtml = 1,

    /// <summary>Do not inject. Dashboard pages only.</summary>
    Disabled = 2
}

/// <summary>How much encoder time to trade for file size.</summary>
public enum SpeedPreference
{
    /// <summary>Finish quickly, accepting a larger file.</summary>
    Fastest = 0,

    /// <summary>The usual trade-off.</summary>
    Balanced = 1,

    /// <summary>Smallest file, several times slower.</summary>
    SmallestFile = 2
}

/// <summary>Plugin settings, persisted by Jellyfin as XML.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets how the in-app UI is delivered.</summary>
    public InjectionMode Injection { get; set; } = InjectionMode.FileTransformation;

    /// <summary>Gets or sets the default output policy offered in the dialog.</summary>
    public OutputPolicy DefaultOutputPolicy { get; set; } = OutputPolicy.Replace;

    /// <summary>
    /// Gets or sets the container the dialog opens on. MP4 plays on the widest range of devices;
    /// files carrying image-based subtitles or font attachments are switched to MKV automatically,
    /// because MP4 cannot hold either.
    /// </summary>
    public string DefaultContainer { get; set; } = "mp4";

    /// <summary>Gets or sets the directory used for Sidecar output. Empty means "beside the original".</summary>
    public string SidecarDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the working directory for in-progress encodes. Empty means the plugin data folder.</summary>
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the directory originals are moved to when they are not kept beside the media.
    /// Empty means the plugin data folder.
    /// </summary>
    public string QuarantineDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets how many days a replaced original is kept before it is deleted.</summary>
    public int QuarantineRetentionDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets a value indicating whether a replaced original is kept next to the media file
    /// rather than moved into the plugin's data folder.
    /// <para>
    /// On by default, and it is also the single biggest speed win available: moving a file within
    /// one directory is an instant rename, whereas moving it to a folder on another disk copies
    /// every byte. On a 5 GB film that is the difference between milliseconds and minutes, twice
    /// over — once for the finished encode and once for the original.
    /// </para>
    /// </summary>
    public bool KeepOriginalsBesideMedia { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether encoding writes into the media folder rather than
    /// the working directory. Same reasoning: it makes the final move a rename instead of a copy.
    /// </summary>
    public bool EncodeBesideMedia { get; set; } = true;

    /// <summary>Gets or sets how many encodes may run at once.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether the queue pauses while anyone is streaming.</summary>
    public bool PauseWhilePlaybackActive { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether ffmpeg runs at below-normal process priority.
    /// Off by default: it keeps the server responsive but measurably slows encoding whenever
    /// anything else wants the CPU.
    /// </summary>
    public bool LowProcessPriority { get; set; }

    /// <summary>Gets or sets the ffmpeg thread cap. Zero or less lets ffmpeg decide.</summary>
    public int EncodingThreadCount { get; set; }

    /// <summary>Gets or sets the seconds a source file must be unmodified before it is eligible.</summary>
    public int FileStabilitySeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether a full decode scan runs before a Replace.
    /// <para>
    /// Off by default. It re-decodes the entire output looking for corruption, which roughly
    /// doubles how long a job takes. The cheap checks — the file parses, the duration matches, the
    /// streams are all present — already run every time and catch essentially every real failure.
    /// </para>
    /// </summary>
    public bool DeepVerifyBeforeReplace { get; set; }

    /// <summary>Gets or sets the multiple of the estimated output size that must be free before starting.</summary>
    public double FreeSpaceSafetyFactor { get; set; } = 1.5;

    /// <summary>Gets or sets a value indicating whether non-admin users may open the read-only analysis dialog.</summary>
    public bool AllowNonAdminAnalysis { get; set; } = true;

    /// <summary>Gets or sets how many finished jobs are retained in history.</summary>
    public int JobHistoryLimit { get; set; } = 200;

    /// <summary>
    /// Gets or sets how many days a finished job stays in the history before it is removed
    /// automatically. Zero keeps them until the count limit above is reached instead.
    /// </summary>
    public int RemoveFinishedJobsAfterDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the audio languages worth keeping, comma separated (e.g. "eng, fr").
    /// Empty keeps every track. Codes, two-letter tags and English names all work.
    /// </summary>
    public string KeepAudioLanguages { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle languages worth keeping. Empty keeps every track.</summary>
    public string KeepSubtitleLanguages { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether tracks with no language tag are kept. On by
    /// default: an untagged track is usually the main one, so dropping it would remove the audio
    /// people actually want.
    /// </summary>
    public bool KeepUntaggedTracks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether commentary and description tracks are removed.
    /// Detected from the track title, since containers rarely flag them properly.
    /// </summary>
    public bool DropCommentaryTracks { get; set; }

    /// <summary>Gets or sets how much encoder time to trade for file size.</summary>
    public SpeedPreference Speed { get; set; } = SpeedPreference.Balanced;

    /// <summary>
    /// Gets or sets a value indicating whether hardware encoding is preferred when the server has
    /// it configured. Far faster, at the cost of a larger file for the same quality.
    /// </summary>
    public bool PreferHardwareEncoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether jobs interrupted by a restart are resumed
    /// automatically. Encoding only ever writes to a temporary file, so restarting one is safe.
    /// </summary>
    public bool ResumeJobsAfterRestart { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether trickplay images are rebuilt after a replace.</summary>
    public bool RegenerateTrickplayAfterReplace { get; set; } = true;

    /// <summary>
    /// Gets or sets the automatic rules. Empty by default, and every rule starts switched off:
    /// nothing in this plugin converts anything until somebody has said so explicitly.
    /// </summary>
    public List<AutomationRule> Rules { get; set; } = new List<AutomationRule>();

    /// <summary>
    /// Makes a copy of these settings, for callers that need to override one value for a single
    /// operation without touching what is saved.
    /// <para>
    /// Copying property by property is what this replaces: the hand-written version quietly
    /// omitted three settings, so a batch run that overrode the language list also reverted
    /// "keep originals beside the media" to its default for that run. Reflection cannot forget a
    /// property that is added later.
    /// </para>
    /// </summary>
    /// <returns>An independent copy.</returns>
    public PluginConfiguration Clone()
    {
        var copy = new PluginConfiguration();

        foreach (var property in typeof(PluginConfiguration).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(copy, property.GetValue(this));
            }
        }

        // The rules are a mutable list. Copying the reference would mean an override made for one
        // batch run could edit the saved rules, which is precisely what this method exists to
        // prevent.
        copy.Rules = Rules.Select(CloneRule).ToList();

        return copy;
    }

    /// <summary>Copies one rule, property by property, by the same reflection rule.</summary>
    /// <param name="rule">The rule to copy.</param>
    /// <returns>An independent copy.</returns>
    private static AutomationRule CloneRule(AutomationRule rule)
    {
        var copy = new AutomationRule();
        foreach (var property in typeof(AutomationRule).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(copy, property.GetValue(rule));
            }
        }

        return copy;
    }
}
