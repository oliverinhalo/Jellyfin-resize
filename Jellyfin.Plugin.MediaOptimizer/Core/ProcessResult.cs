namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Outcome of a finished child process.</summary>
public class ProcessResult
{
    /// <summary>Gets or sets the process exit code.</summary>
    public int ExitCode { get; set; }

    /// <summary>Gets or sets everything written to stdout.</summary>
    public string StandardOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets everything written to stderr.</summary>
    public string StandardError { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether the process exited cleanly.</summary>
    public bool Success => ExitCode == 0;
}
