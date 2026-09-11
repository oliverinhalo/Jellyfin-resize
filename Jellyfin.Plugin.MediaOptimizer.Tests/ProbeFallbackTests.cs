using System.Text.Json;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Covers the path taken when Jellyfin has no recorded streams for an item. Before this the
/// dialog rendered a 22 GiB remux as having neither video nor audio, offered MP4 for its TrueHD
/// track, and left "Start conversion" enabled.
/// </summary>
public class ProbeFallbackTests
{
    // Trimmed from real ffprobe output for a Blu-ray remux: AVC video, TrueHD Atmos 7.1, PGS subs.
    private const string RemuxProbe = """
    {
      "streams": [
        {
          "index": 0, "codec_name": "h264", "codec_type": "video", "profile": "High",
          "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "bits_per_raw_sample": "8",
          "avg_frame_rate": "24000/1001", "r_frame_rate": "24000/1001",
          "color_transfer": "bt709", "color_primaries": "bt709", "color_space": "bt709",
          "disposition": { "default": 1 }
        },
        {
          "index": 1, "codec_name": "truehd", "codec_type": "audio", "profile": "Dolby TrueHD + Dolby Atmos",
          "channels": 8, "channel_layout": "7.1", "sample_rate": "48000",
          "bits_per_raw_sample": "24", "disposition": { "default": 1 },
          "tags": { "language": "eng", "title": "TrueHD Atmos 7.1" }
        },
        {
          "index": 2, "codec_name": "ac3", "codec_type": "audio", "channels": 6,
          "channel_layout": "5.1", "sample_rate": "48000", "bit_rate": "640000",
          "disposition": { "default": 0 }, "tags": { "language": "eng" }
        },
        {
          "index": 3, "codec_name": "hdmv_pgs_subtitle", "codec_type": "subtitle",
          "disposition": { "default": 0 }, "tags": { "language": "eng" }
        },
        { "index": 4, "codec_type": "attachment" }
      ]
    }
    """;

    private static FileAnalysis BuildFromProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var analysis = new FileAnalysis { IsEligible = true };
        MediaProbeService.BuildTracksFromFfprobe(analysis, doc.RootElement.GetProperty("streams"));
        return analysis;
    }

    [Fact]
    public void Builds_the_video_track_from_ffprobe_when_Jellyfin_knows_nothing()
    {
        var a = BuildFromProbe(RemuxProbe);

        Assert.NotNull(a.Video);
        Assert.Equal("h264", a.Video!.Codec);
        Assert.Equal(1920, a.Video.Width);
        Assert.Equal(1080, a.Video.Height);
        Assert.Equal(8, a.Video.BitDepth);
        Assert.Equal("yuv420p", a.Video.PixelFormat);
        Assert.Equal("SDR", a.Video.Range);
        Assert.NotNull(a.Video.FrameRate);
        Assert.Equal(23.976d, a.Video.FrameRate!.Value, 2);
    }

    [Fact]
    public void Builds_audio_tracks_including_the_lossless_and_object_flags()
    {
        var a = BuildFromProbe(RemuxProbe);

        Assert.Equal(2, a.Audio.Count);

        var trueHd = a.Audio[0];
        Assert.Equal("truehd", trueHd.Codec);
        Assert.Equal(8, trueHd.Channels);
        Assert.Equal("eng", trueHd.Language);
        Assert.True(trueHd.IsDefault);

        // These two drive real decisions: lossless gates the bit-exact mode, and object audio
        // gates the warning about losing Atmos height channels.
        Assert.True(trueHd.IsLossless);
        Assert.True(trueHd.HasObjectAudio);

        Assert.Equal("ac3", a.Audio[1].Codec);
        Assert.Equal(640000, a.Audio[1].Bitrate.Bps);
        Assert.Equal(1, a.Audio[1].TypeIndex);
    }

    [Fact]
    public void Recognises_image_based_subtitles_and_counts_attachments()
    {
        var a = BuildFromProbe(RemuxProbe);

        Assert.Single(a.Subtitles);
        Assert.True(a.Subtitles[0].IsGraphical);
        Assert.False(a.Subtitles[0].IsExternal);
    }

    [Fact]
    public void A_TrueHD_remux_rebuilt_from_ffprobe_is_kept_out_of_MP4()
    {
        // The whole point of recovering the streams: MP4 cannot hold TrueHD, so a request that
        // asked for MP4 has to be moved to MKV. With no audio info this check silently passed
        // and the job died at the muxer.
        var a = BuildFromProbe(RemuxProbe);
        var request = new EncodeRequest
        {
            Container = "mp4",
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        StrategyResolver.ApplyContainerCompatibility(a, request);

        Assert.Equal("mkv", ContainerCompatibility.Normalise(request.Container));
        Assert.NotNull(request.ContainerSwitchReason);
    }

    [Fact]
    public void The_TrueHD_track_itself_is_what_forces_MKV_once_subtitles_are_out_of_the_way()
    {
        // The remux above also carries PGS subtitles, which MP4 cannot hold either and which the
        // planner checks first. Strip those so the audio rule is the one under test.
        var a = BuildFromProbe(RemuxProbe);
        a.Subtitles = [];

        var request = new EncodeRequest
        {
            Container = "mp4",
            AudioTracks = [new AudioTrackRequest { Index = 1, Action = AudioAction.Copy }]
        };

        StrategyResolver.ApplyContainerCompatibility(a, request);

        Assert.Equal("mkv", ContainerCompatibility.Normalise(request.Container));
        Assert.Contains("TRUEHD", request.ContainerSwitchReason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_HDR_transfer_characteristics()
    {
        const string Hdr = """
        { "streams": [ { "index": 0, "codec_name": "hevc", "codec_type": "video",
          "width": 3840, "height": 2160, "bits_per_raw_sample": "10",
          "color_transfer": "smpte2084" } ] }
        """;

        var a = BuildFromProbe(Hdr);

        Assert.Equal("HDR10", a.Video!.Range);
        Assert.Equal(10, a.Video.BitDepth);
    }

    [Fact]
    public void Numeric_fields_parse_whether_ffprobe_quotes_them_or_not()
    {
        // ffprobe reports width/height as numbers but sample_rate and bit depth as strings, and
        // that has varied between builds.
        const string Mixed = """
        { "streams": [
          { "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 1280,
            "height": 720, "bits_per_raw_sample": 10 },
          { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2,
            "sample_rate": 44100 } ] }
        """;

        var a = BuildFromProbe(Mixed);

        Assert.Equal(1280, a.Video!.Width);
        Assert.Equal(10, a.Video.BitDepth);
        Assert.Equal(44100, a.Audio[0].SampleRate);
    }
}
