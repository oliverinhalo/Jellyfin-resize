using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>Outcome of one self-check.</summary>
public enum CheckStatus
{
    /// <summary>Working.</summary>
    Ok = 0,

    /// <summary>Working, but degraded or partially unavailable.</summary>
    Warning = 1,

    /// <summary>Not working.</summary>
    Failed = 2,

    /// <summary>Deliberately switched off.</summary>
    Disabled = 3
}

/// <summary>One line in the status panel.</summary>
public class DiagnosticCheck
{
    /// <summary>Initializes a new instance of the <see cref="DiagnosticCheck"/> class.</summary>
    public DiagnosticCheck()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DiagnosticCheck"/> class.</summary>
    /// <param name="name">Short label.</param>
    /// <param name="status">Outcome.</param>
    /// <param name="detail">What it means, and what to do about it.</param>
    public DiagnosticCheck(string name, CheckStatus status, string detail)
    {
        Name = name;
        Status = status;
        Detail = detail;
    }

    /// <summary>Gets or sets the short label.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the outcome.</summary>
    public CheckStatus Status { get; set; }

    /// <summary>Gets or sets the explanation shown under the label.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// A self-check of every moving part. The point is that no single failure hides the others:
/// if the injected UI cannot load, the dashboard still says so explicitly rather than the
/// plugin appearing to do nothing.
/// </summary>
public class DiagnosticsReport
{
    /// <summary>Gets or sets the running plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin server version.</summary>
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the caller may start conversions.</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets the individual checks.</summary>
    public IReadOnlyList<DiagnosticCheck> Checks { get; set; } = Array.Empty<DiagnosticCheck>();

    /// <summary>Gets or sets the overall worst status across all checks.</summary>
    public CheckStatus Overall { get; set; }

    /// <summary>Gets or sets a one-line summary of the overall state.</summary>
    public string Summary { get; set; } = string.Empty;
}
