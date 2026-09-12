using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Output;

/// <summary>Result of checking a freshly produced file.</summary>
public class VerificationResult
{
    /// <summary>Gets or sets a value indicating whether the file passed every check.</summary>
    public bool Passed { get; set; }

    /// <summary>Gets or sets why the file was rejected.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the verified output size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the verified duration in seconds.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Gets or sets whether a bit-exactness hash comparison ran and what it found.</summary>
    public bool? LosslessVerified { get; set; }
}

/// <summary>
/// The gate that stands between a finished encode and anything that touches the library.
/// Nothing destructive happens until this passes.
/// </summary>
public interface IVerificationService
{
    /// <summary>Checks a produced file against its source.</summary>
    /// <param name="sourcePath">The original file.</param>
    /// <param name="outputPath">The produced file.</param>
    /// <param name="expectedDurationSeconds">Duration the output should have.</param>
    /// <param name="deepScan">Whether to run a full decode pass looking for corruption.</param>
    /// <param name="losslessAudio">Audio tracks that should be bit-identical, if any.</param>
    /// <param name="expected">
    /// What the plan meant to produce, so the output can be checked against it, or null to skip
    /// that check.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    Task<VerificationResult> VerifyAsync(
        string sourcePath,
        string outputPath,
        double? expectedDurationSeconds,
        bool deepScan,
        IReadOnlyList<LosslessAudioCheck> losslessAudio,
        ExpectedStreams? expected,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public class VerificationService : IVerificationService
{
    private readonly IFfmpegRunner _runner;
    private readonly ILogger<VerificationService> _logger;

    /// <summary>Initializes a new instance of the <see cref="VerificationService"/> class.</summary>
    /// <param name="runner">FFmpeg runner.</param>
    /// <param name="logger">Logger.</param>
    public VerificationService(IFfmpegRunner runner, ILogger<VerificationService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        string sourcePath,
        string outputPath,
        double? expectedDurationSeconds,
        bool deepScan,
        IReadOnlyList<LosslessAudioCheck> losslessAudio,
        ExpectedStreams? expected,
        CancellationToken cancellationToken)
    {
        var result = new VerificationResult();

        if (!File.Exists(outputPath))
        {
            result.FailureReason = "FFmpeg reported success but produced no output file.";
            return result;
        }

        var info = new FileInfo(outputPath);
        result.SizeBytes = info.Length;
        if (info.Length == 0)
        {
            result.FailureReason = "The produced file is empty.";
            return result;
        }

        var duration = await ProbeDurationAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (duration is null)
        {
            result.FailureReason = "The produced file could not be parsed by ffprobe.";
            return result;
        }

        result.DurationSeconds = duration;

        if (expectedDurationSeconds is > 0)
        {
            // Container rounding and encoder padding move the duration slightly; anything
            // beyond one second or half a percent means frames went missing.
            var tolerance = Math.Max(1d, expectedDurationSeconds.Value * 0.005d);
            var delta = Math.Abs(duration.Value - expectedDurationSeconds.Value);
            if (delta > tolerance)
            {
                result.FailureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "Duration mismatch: expected {0:F1}s but the output is {1:F1}s.",
                    expectedDurationSeconds.Value,
                    duration.Value);
                return result;
            }
        }

        // Is everything the plan mapped actually in there? An encoder or muxer that drops a track
        // it could not write and still exits zero passes every check above: the file parses and
        // the duration is right, because the video is all there. Only the thing that went missing
        // is missing -- and the next step after this replaces the original.
        if (expected is not null)
        {
            var counts = await CountStreamsAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (counts is null)
            {
                result.FailureReason = "The produced file's streams could not be listed.";
                return result;
            }

            var missing = Describe(expected, counts.Value);
            if (missing is not null)
            {
                result.FailureReason = "The output is missing " + missing
                    + " The original has not been touched.";
                return result;
            }
        }

        if (deepScan)
        {
            var scanError = await DeepScanAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (scanError is not null)
            {
                result.FailureReason = "The produced file failed a full decode scan: " + scanError;
                return result;
            }
        }

        if (losslessAudio.Count > 0)
        {
            var verified = await VerifyLosslessAudioAsync(
                sourcePath,
                outputPath,
                losslessAudio,
                cancellationToken).ConfigureAwait(false);

            result.LosslessVerified = verified;
            if (verified == false)
            {
                result.FailureReason = "A track claimed to be lossless did not decode bit-identically to the source.";
                return result;
            }
        }

        result.Passed = true;
        return result;
    }

    /// <summary>
    /// Names what is short, or null when everything the plan mapped is present.
    /// <para>
    /// Only a shortfall is a failure. More streams than expected happens for reasons that are not
    /// data loss -- a muxer writing a timecode track, cover art carried as a video stream -- and
    /// failing a conversion over one would throw away a good encode.
    /// </para>
    /// </summary>
    /// <param name="expected">What the plan mapped.</param>
    /// <param name="actual">What the file has.</param>
    /// <returns>A description of the shortfall, or null.</returns>
    internal static string? Describe(ExpectedStreams expected, StreamCounts actual)
    {
        ArgumentNullException.ThrowIfNull(expected);

        var missing = new List<string>();

        if (actual.Video < expected.Video)
        {
            missing.Add(Phrase(expected.Video - actual.Video, "video stream", "video streams"));
        }

        if (actual.Audio < expected.Audio)
        {
            missing.Add(Phrase(expected.Audio - actual.Audio, "audio track", "audio tracks"));
        }

        if (actual.Subtitles < expected.Subtitles)
        {
            missing.Add(Phrase(expected.Subtitles - actual.Subtitles, "subtitle track", "subtitle tracks"));
        }

        return missing.Count == 0 ? null : string.Join(" and ", missing) + ".";
    }

    private static string Phrase(int count, string singular, string plural) =>
        FormattableString.Invariant($"{count} {(count == 1 ? singular : plural)}");

    /// <summary>Counts the output's streams by kind.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counts, or null when they could not be read.</returns>
    private async Task<StreamCounts?> CountStreamsAsync(string path, CancellationToken cancellationToken)
    {
        string[] args =
        [
            "-v", "error",
            "-show_entries", "stream=codec_type",
            "-of", "default=noprint_wrappers=1:nokey=1",
            path
        ];

        try
        {
            var result = await _runner.RunAsync(_runner.FfprobePath, args, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            return CountStreams(result.StandardOutput);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Stream listing failed for {Path}", path);
            return null;
        }
    }

    /// <summary>Counts the stream kinds in ffprobe's answer.</summary>
    /// <param name="ffprobeOutput">One codec_type per line.</param>
    /// <returns>The counts.</returns>
    internal static StreamCounts CountStreams(string? ffprobeOutput)
    {
        var counts = default(StreamCounts);
        if (string.IsNullOrWhiteSpace(ffprobeOutput))
        {
            return counts;
        }

        foreach (var line in ffprobeOutput.Split('\n'))
        {
            switch (line.Trim())
            {
                case "video":
                    counts.Video++;
                    break;
                case "audio":
                    counts.Audio++;
                    break;
                case "subtitle":
                    counts.Subtitles++;
                    break;
                default:
                    // Attachments, data and timecode streams are none of this check's business.
                    break;
            }
        }

        return counts;
    }

    /// <summary>Decodes one audio stream and returns its MD5, for bit-exactness proof.</summary>
    /// <param name="path">File to read.</param>
    /// <param name="specifier">Stream specifier such as "0:1".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hash string, or null on failure.</returns>
    private async Task<string?> HashAudioStreamAsync(string path, string specifier, CancellationToken cancellationToken)
    {
        string[] args =
        [
            "-nostdin", "-v", "error",
            "-i", path,
            "-map", specifier,
            "-f", "hash", "-hash", "md5",
            "-"
        ];

        try
        {
            var result = await _runner.RunAsync(_runner.FfmpegPath, args, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            var text = result.StandardOutput.Trim();
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            return eq >= 0 ? text[(eq + 1)..].Trim() : text;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Audio hashing failed for {Path}", path);
            return null;
        }
    }

    private async Task<bool?> VerifyLosslessAudioAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<LosslessAudioCheck> checks,
        CancellationToken cancellationToken)
    {
        // Each check carries both halves of the pairing. Assuming the Nth lossless track is the
        // Nth output track is wrong the moment a copied track sits in front of it -- a file with a
        // copied AC-3 commentary before a FLAC-from-DTS-HD track then compared the FLAC against
        // the AC-3 and failed a job that was in fact bit-exact.
        foreach (var check in checks)
        {
            var sourceHash = await HashAudioStreamAsync(
                sourcePath,
                FormattableString.Invariant($"0:{check.SourceStreamIndex}"),
                cancellationToken).ConfigureAwait(false);

            var outputHash = await HashAudioStreamAsync(
                outputPath,
                FormattableString.Invariant($"0:a:{check.OutputAudioIndex}"),
                cancellationToken).ConfigureAwait(false);

            if (sourceHash is null || outputHash is null)
            {
                _logger.LogWarning("[MediaOptimizer] Could not compute lossless verification hashes");
                return null;
            }

            if (!string.Equals(sourceHash, outputHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "[MediaOptimizer] Lossless verification FAILED for source stream {Stream} vs output audio {Output}: {SourceHash} != {OutputHash}",
                    check.SourceStreamIndex,
                    check.OutputAudioIndex,
                    sourceHash,
                    outputHash);
                return false;
            }
        }

        return true;
    }

    private async Task<double?> ProbeDurationAsync(string path, CancellationToken cancellationToken)
    {
        string[] args =
        [
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            path
        ];

        try
        {
            var result = await _runner.RunAsync(_runner.FfprobePath, args, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            var text = result.StandardOutput.Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Duration probe failed for {Path}", path);
            return null;
        }
    }

    private async Task<string?> DeepScanAsync(string path, CancellationToken cancellationToken)
    {
        string[] args = ["-nostdin", "-v", "error", "-i", path, "-f", "null", "-"];

        try
        {
            var result = await _runner.RunAsync(_runner.FfmpegPath, args, cancellationToken).ConfigureAwait(false);
            if (result.Success && string.IsNullOrWhiteSpace(result.StandardError))
            {
                return null;
            }

            var error = result.StandardError.Trim();
            return string.IsNullOrEmpty(error) ? "ffmpeg exited with a non-zero status." : Truncate(error, 400);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "[MediaOptimizer] Deep scan failed for {Path}", path);
            return "the decode scan could not be run.";
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
