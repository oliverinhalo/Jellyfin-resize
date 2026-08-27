namespace Jellyfin.Plugin.MediaOptimizer.Models;

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

    /// <summary>Gets or sets the planner messages for this configuration.</summary>
    public System.Collections.Generic.IReadOnlyList<PlanWarning> Warnings { get; set; }
        = System.Array.Empty<PlanWarning>();
}
