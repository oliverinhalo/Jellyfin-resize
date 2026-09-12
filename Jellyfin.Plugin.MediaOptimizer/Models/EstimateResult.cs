namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>How much weight to put on a predicted size.</summary>
public enum EstimateConfidence
{
    /// <summary>Nothing to base a prediction on.</summary>
    Unknown = 0,

    /// <summary>A guess; the source bitrate was not available to anchor it.</summary>
    Low = 1,

    /// <summary>Anchored on the source bitrate. Typically within a quarter either way.</summary>
    Medium = 2,

    /// <summary>Nothing is being re-encoded, so the size is essentially known.</summary>
    High = 3,

    /// <summary>Part of the conversion was actually run and the output measured.</summary>
    Measured = 4
}

/// <summary>Predicted outcome of a conversion, shown live under the dialog.</summary>
public class EstimateResult
{
    /// <summary>Gets or sets the current file size in bytes.</summary>
    public long CurrentSizeBytes { get; set; }

    /// <summary>Gets or sets the mid-point predicted output size.</summary>
    public long EstimatedSizeBytes { get; set; }

    /// <summary>Gets or sets the low end of the predicted range.</summary>
    public long EstimatedSizeLowBytes { get; set; }

    /// <summary>Gets or sets the high end of the predicted range.</summary>
    public long EstimatedSizeHighBytes { get; set; }

    /// <summary>Gets or sets the predicted saving as a fraction of the current size.</summary>
    public double SavingFraction { get; set; }

    /// <summary>Gets or sets how the estimate was produced: heuristic or sampled.</summary>
    public string Method { get; set; } = "heuristic";

    /// <summary>Gets or sets the estimated encode time in seconds, when it can be guessed.</summary>
    public double? EstimatedSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether every operation is bit-exact.</summary>
    public bool IsLossless { get; set; }

    /// <summary>Gets or sets how much weight to put on this prediction.</summary>
    public EstimateConfidence Confidence { get; set; }

    /// <summary>Gets or sets where the time estimate came from, or "unmeasured" when there is none.</summary>
    public string TimeBasis { get; set; } = "unmeasured";

    /// <summary>Gets or sets an explanation shown when the predicted saving is small.</summary>
    public string? SavingNote { get; set; }

    /// <summary>
    /// Gets or sets how the measurement was obtained, when one was: how many samples, how much
    /// of the file they covered, and how far apart they were. Null for a modelled estimate.
    /// </summary>
    public string? MeasurementNote { get; set; }

    /// <summary>Gets or sets the planner messages for this configuration.</summary>
    public System.Collections.Generic.IReadOnlyList<PlanWarning> Warnings { get; set; }
        = System.Array.Empty<PlanWarning>();
}
