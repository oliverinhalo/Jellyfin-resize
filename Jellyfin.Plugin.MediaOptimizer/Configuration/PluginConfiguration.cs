using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaOptimizer.Configuration;

/// <summary>Where a finished encode is placed relative to the original file.</summary>
public enum OutputPolicy
{
    /// <summary>Write beside/into a separate directory. The original is never modified.</summary>
    Sidecar = 0,

    /// <summary>Register the result as an alternate version of the same library item.</summary>
    AlternateVersion = 1,

    /// <summary>Replace the original, moving it to quarantine first.</summary>
    Replace = 2
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

    /// <summary>Gets or sets the directory originals are moved to before a Replace. Empty means the plugin data folder.</summary>
    public string QuarantineDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets how many days a quarantined original is kept before the sweep task deletes it.</summary>
    public int QuarantineRetentionDays { get; set; } = 14;

    /// <summary>Gets or sets how many encodes may run at once.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether the queue pauses while anyone is streaming.</summary>
    public bool PauseWhilePlaybackActive { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether ffmpeg runs at below-normal process priority.</summary>
    public bool LowProcessPriority { get; set; } = true;

    /// <summary>Gets or sets the ffmpeg thread cap. Zero or less lets ffmpeg decide.</summary>
    public int EncodingThreadCount { get; set; }

    /// <summary>Gets or sets the seconds a source file must be unmodified before it is eligible.</summary>
    public int FileStabilitySeconds { get; set; } = 60;

    /// <summary>Gets or sets a value indicating whether a full decode scan runs before a Replace.</summary>
    public bool DeepVerifyBeforeReplace { get; set; } = true;

    /// <summary>Gets or sets the multiple of the estimated output size that must be free before starting.</summary>
    public double FreeSpaceSafetyFactor { get; set; } = 1.5;

    /// <summary>Gets or sets a value indicating whether non-admin users may open the read-only analysis dialog.</summary>
    public bool AllowNonAdminAnalysis { get; set; } = true;

    /// <summary>Gets or sets how many finished jobs are retained in history.</summary>
    public int JobHistoryLimit { get; set; } = 200;

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
}
