using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>How seriously the UI should present a planner message.</summary>
public enum WarningLevel
{
    /// <summary>Informational.</summary>
    Info = 0,

    /// <summary>Something is lost or degraded, but the job may proceed.</summary>
    Warning = 1,

    /// <summary>The job cannot run as specified.</summary>
    Blocker = 2
}

/// <summary>One planner message.</summary>
public class PlanWarning
{
    /// <summary>Initializes a new instance of the <see cref="PlanWarning"/> class.</summary>
    public PlanWarning()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PlanWarning"/> class.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="message">Human-readable text shown in the dialog.</param>
    public PlanWarning(WarningLevel level, string code, string message)
    {
        Level = level;
        Code = code;
        Message = message;
    }

    /// <summary>Gets or sets the severity.</summary>
    public WarningLevel Level { get; set; }

    /// <summary>Gets or sets the stable code, e.g. DOLBY_VISION_LOSS.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the message text.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>A validated conversion, ready to run.</summary>
public class PlanResult
{
    /// <summary>Gets or sets the ffmpeg arguments, already split into argv form.</summary>
    public IReadOnlyList<string> Arguments { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the second-pass arguments for two-pass encoding, if used.</summary>
    public IReadOnlyList<string>? SecondPassArguments { get; set; }

    /// <summary>Gets or sets the messages raised while planning.</summary>
    public IReadOnlyList<PlanWarning> Warnings { get; set; } = Array.Empty<PlanWarning>();

    /// <summary>Gets or sets the output file extension, without a dot.</summary>
    public string OutputExtension { get; set; } = "mkv";

    /// <summary>Gets a value indicating whether any blocker was raised.</summary>
    public bool IsRunnable
    {
        get
        {
            foreach (var w in Warnings)
            {
                if (w.Level == WarningLevel.Blocker)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Gets or sets a value indicating whether every operation in this plan is bit-exact.</summary>
    public bool IsLossless { get; set; }

    /// <summary>Gets or sets the audio stream indexes whose decoded output should hash-match the source.</summary>
    public IReadOnlyList<int> LosslessAudioIndexes { get; set; } = Array.Empty<int>();
}
