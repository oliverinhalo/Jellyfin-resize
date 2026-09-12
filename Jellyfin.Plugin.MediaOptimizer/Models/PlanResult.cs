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

/// <summary>
/// One bit-exactness check to run after encoding: a source audio stream and the position the same
/// track occupies among the output's audio streams.
/// <para>
/// Both halves are needed. FFmpeg renumbers output streams from zero, so the Nth lossless track is
/// only the Nth output track when every track before it was also kept and also lossless. A file
/// whose first track is a copied AC-3 commentary and whose second is FLAC from DTS-HD would
/// otherwise be checked against the wrong stream and fail verification for no reason.
/// </para>
/// </summary>
public class LosslessAudioCheck
{
    /// <summary>Initializes a new instance of the <see cref="LosslessAudioCheck"/> class.</summary>
    public LosslessAudioCheck()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LosslessAudioCheck"/> class.</summary>
    /// <param name="sourceStreamIndex">The source stream index, as ffmpeg numbers all streams.</param>
    /// <param name="outputAudioIndex">The position among the output's audio streams.</param>
    public LosslessAudioCheck(int sourceStreamIndex, int outputAudioIndex)
    {
        SourceStreamIndex = sourceStreamIndex;
        OutputAudioIndex = outputAudioIndex;
    }

    /// <summary>Gets or sets the source stream index, across all stream types.</summary>
    public int SourceStreamIndex { get; set; }

    /// <summary>Gets or sets the zero-based position among the output's audio streams.</summary>
    public int OutputAudioIndex { get; set; }
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

    /// <summary>
    /// Gets or sets a value indicating whether the video stream is copied rather than re-encoded.
    /// Nothing that compares pictures needs to run in that case: they are the same pictures.
    /// </summary>
    public bool VideoIsCopied { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the output has no video stream, either because it
    /// was dropped or because the source had none. There is nothing to compare pictures against.
    /// </summary>
    public bool VideoIsAbsent { get; set; }

    /// <summary>
    /// Gets or sets the audio tracks whose decoded output must hash-match the source, each paired
    /// with the position it occupies in the output.
    /// </summary>
    public IReadOnlyList<LosslessAudioCheck> LosslessAudioChecks { get; set; } = Array.Empty<LosslessAudioCheck>();
}
