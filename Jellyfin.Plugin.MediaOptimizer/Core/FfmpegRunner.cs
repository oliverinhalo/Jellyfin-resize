using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Core;

/// <summary>Runs the server's own ffmpeg/ffprobe binaries and reports progress.</summary>
public interface IFfmpegRunner
{
    /// <summary>Gets the ffmpeg binary path reported by Jellyfin.</summary>
    string FfmpegPath { get; }

    /// <summary>Gets the ffprobe binary path reported by Jellyfin.</summary>
    string FfprobePath { get; }

    /// <summary>Runs a binary to completion and captures its output.</summary>
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">Arguments in argv form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured result.</returns>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>Runs an encode, reporting progress parsed from ffmpeg's own progress stream.</summary>
    /// <param name="arguments">FFmpeg arguments in argv form, excluding -progress.</param>
    /// <param name="totalDurationSeconds">Source duration, used to turn output time into a percentage.</param>
    /// <param name="onProgress">Progress callback.</param>
    /// <param name="lowPriority">Whether to drop the process priority.</param>
    /// <param name="cancellationToken">Cancellation token. Cancelling kills the process.</param>
    /// <returns>The captured result.</returns>
    Task<ProcessResult> RunEncodeAsync(
        IReadOnlyList<string> arguments,
        double? totalDurationSeconds,
        Action<EncodeProgress>? onProgress,
        bool lowPriority,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public class FfmpegRunner : IFfmpegRunner
{
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly ILogger<FfmpegRunner> _logger;
    private readonly string? _ffmpegOverride;
    private readonly string? _ffprobeOverride;

    /// <summary>Initializes a new instance of the <see cref="FfmpegRunner"/> class.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, used only for the binary paths.</param>
    /// <param name="logger">Logger.</param>
    public FfmpegRunner(IMediaEncoder mediaEncoder, ILogger<FfmpegRunner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegRunner"/> class with explicit binary
    /// paths, bypassing Jellyfin's media encoder. Used by tests against a real ffmpeg.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg binary.</param>
    /// <param name="ffprobePath">Path to the ffprobe binary.</param>
    /// <param name="logger">Logger.</param>
    internal FfmpegRunner(string ffmpegPath, string ffprobePath, ILogger<FfmpegRunner> logger)
    {
        _ffmpegOverride = ffmpegPath;
        _ffprobeOverride = ffprobePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public string FfmpegPath => _ffmpegOverride ?? _mediaEncoder!.EncoderPath;

    /// <inheritdoc />
    public string FfprobePath => _ffprobeOverride ?? _mediaEncoder!.ProbePath;

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunEncodeAsync(
        IReadOnlyList<string> arguments,
        double? totalDurationSeconds,
        Action<EncodeProgress>? onProgress,
        bool lowPriority,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -progress writes machine-readable key=value pairs to stdout; -nostats silences the
        // human-readable duplicate on stderr so stderr stays useful for real errors.
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        var startedAt = DateTime.UtcNow;
        var current = new EncodeProgress();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                // Keep the tail only; a failing encode can emit a great deal of output.
                stderr.AppendLine(e.Data);
                if (stderr.Length > 64_000)
                {
                    stderr.Remove(0, stderr.Length - 48_000);
                }
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || onProgress is null)
            {
                return;
            }

            if (!TryApplyProgressLine(e.Data, current))
            {
                return;
            }

            if (totalDurationSeconds is > 0)
            {
                var pct = current.OutTimeSeconds / totalDurationSeconds.Value * 100d;
                current.Percent = Math.Clamp(pct, 0d, 100d);

                var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
                if (current.OutTimeSeconds > 1d && elapsed > 1d)
                {
                    var rate = current.OutTimeSeconds / elapsed;
                    if (rate > 0d)
                    {
                        var remaining = totalDurationSeconds.Value - current.OutTimeSeconds;
                        current.EtaSeconds = Math.Max(0d, remaining / rate);
                    }
                }
            }

            onProgress(current);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (lowPriority)
        {
            TrySetLowPriority(process);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = string.Empty,
            StandardError = stderr.ToString()
        };
    }

    /// <summary>Applies one "key=value" progress line. Returns true when a sample is complete.</summary>
    /// <param name="line">The raw line from ffmpeg.</param>
    /// <param name="progress">The sample being accumulated.</param>
    /// <returns>True when ffmpeg signalled the end of a progress block.</returns>
    internal static bool TryApplyProgressLine(string line, EncodeProgress progress)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return false;
        }

        var key = line.AsSpan(0, eq).Trim().ToString();
        var value = line.AsSpan(eq + 1).Trim().ToString();

        switch (key)
        {
            case "out_time_us":
            case "out_time_ms":
                // Both keys are microseconds in practice; out_time_ms is a long-standing ffmpeg misnomer.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    progress.OutTimeSeconds = us / 1_000_000d;
                }

                return false;

            case "frame":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                {
                    progress.Frame = f;
                }

                return false;

            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                {
                    progress.TotalSize = ts;
                }

                return false;

            case "speed":
                var trimmed = value.TrimEnd('x');
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                {
                    progress.Speed = sp;
                }

                return false;

            case "progress":
                // "continue" ends each block, "end" ends the run.
                return true;

            default:
                return false;
        }
    }

    private void TrySetLowPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not lower ffmpeg process priority");
        }
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "[MediaOptimizer] Could not kill ffmpeg process");
        }
    }
}
