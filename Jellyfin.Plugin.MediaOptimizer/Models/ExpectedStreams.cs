namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>
/// What a plan meant to put in the output, so that what came out can be checked against it.
/// </summary>
public class ExpectedStreams
{
    /// <summary>Gets or sets the number of video streams the plan mapped.</summary>
    public int Video { get; set; }

    /// <summary>Gets or sets the number of audio streams the plan mapped.</summary>
    public int Audio { get; set; }

    /// <summary>Gets or sets the number of subtitle streams the plan mapped.</summary>
    public int Subtitles { get; set; }

    /// <summary>Builds the expectation from a plan.</summary>
    /// <param name="plan">The plan that is about to run, or that has just run.</param>
    /// <returns>What its output should contain.</returns>
    public static ExpectedStreams From(PlanResult plan)
    {
        System.ArgumentNullException.ThrowIfNull(plan);

        return new ExpectedStreams
        {
            Video = plan.MappedVideoStreams,
            Audio = plan.MappedAudioStreams,
            Subtitles = plan.MappedSubtitleStreams
        };
    }
}

/// <summary>How many streams of each kind a file actually has.</summary>
public struct StreamCounts : System.IEquatable<StreamCounts>
{
    /// <summary>Gets or sets the video stream count.</summary>
    public int Video { get; set; }

    /// <summary>Gets or sets the audio stream count.</summary>
    public int Audio { get; set; }

    /// <summary>Gets or sets the subtitle stream count.</summary>
    public int Subtitles { get; set; }

    /// <inheritdoc />
    public bool Equals(StreamCounts other) =>
        Video == other.Video && Audio == other.Audio && Subtitles == other.Subtitles;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StreamCounts other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => System.HashCode.Combine(Video, Audio, Subtitles);

    /// <summary>Compares two counts.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(StreamCounts left, StreamCounts right) => left.Equals(right);

    /// <summary>Compares two counts.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(StreamCounts left, StreamCounts right) => !left.Equals(right);
}
