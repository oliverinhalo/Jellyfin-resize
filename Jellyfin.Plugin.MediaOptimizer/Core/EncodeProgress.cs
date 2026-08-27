namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>A progress sample parsed from ffmpeg's -progress stream.</summary>
public class EncodeProgress
{
    /// <summary>Gets or sets how far into the output ffmpeg has written, in seconds.</summary>
    public double OutTimeSeconds { get; set; }

    /// <summary>Gets or sets the frame count so far.</summary>
    public long? Frame { get; set; }

    /// <summary>Gets or sets encoding speed as a multiple of realtime.</summary>
    public double? Speed { get; set; }

    /// <summary>Gets or sets bytes written so far.</summary>
    public long? TotalSize { get; set; }

    /// <summary>Gets or sets completion between 0 and 100, when the duration is known.</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets the estimated seconds remaining.</summary>
    public double? EtaSeconds { get; set; }
}
