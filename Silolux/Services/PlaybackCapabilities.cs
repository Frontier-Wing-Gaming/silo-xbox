using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.System.Profile;

namespace Silolux.Services
{
    /// <summary>
    /// What this console's Media Foundation stack decodes, probed with
    /// CodecQuery, declared to the playback planner (protocol v3). Evidence
    /// stays `declared`: the planner then matches the flat lists only.
    /// </summary>
    public static class PlaybackCapabilities
    {
        static List<string> video;
        static List<string> audio;
        static bool hdr10;

        static readonly Dictionary<string, string> VideoSubtypes = new Dictionary<string, string>
        {
            { "h264", CodecSubtypes.VideoFormatH264 },
            { "hevc", CodecSubtypes.VideoFormatHevc },
            { "vp9", CodecSubtypes.VideoFormatVP90 },
            { "vp8", CodecSubtypes.VideoFormatVP80 },
            { "av1", CodecSubtypes.VideoFormatAv1 },
            { "mpeg2video", CodecSubtypes.VideoFormatMpeg2 },
            // MPEG-4 Part 2 (DivX / Xvid): hardware-decoded on Xbox Series X per the diagnostics page.
            { "mpeg4", CodecSubtypes.VideoFormatMP4V },
            { "vc1", CodecSubtypes.VideoFormatWvc1 },
        };

        static readonly Dictionary<string, string> AudioSubtypes = new Dictionary<string, string>
        {
            { "aac", CodecSubtypes.AudioFormatAac },
            { "mp3", CodecSubtypes.AudioFormatMP3 },
            { "mp2", CodecSubtypes.AudioFormatMPeg },
            { "ac3", CodecSubtypes.AudioFormatDolbyAC3 },
            { "eac3", CodecSubtypes.AudioFormatDolbyDDPlus },
            { "flac", CodecSubtypes.AudioFormatFlac },
            { "alac", CodecSubtypes.AudioFormatAlac },
            { "opus", CodecSubtypes.AudioFormatOpus },
        };

        public static bool IsXbox => AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

        // What the reports below contain, for the diagnostics page (valid after ProbeAsync).
        public static IReadOnlyCollection<string> ReportedVideo => video;
        public static IReadOnlyCollection<string> ReportedAudio => audio;
        public static bool ReportsHdr10 => hdr10 && video.Contains("hevc");
        public static string ReportedMaxResolution => IsXbox ? "2160p" : "1080p";

        public static string OsVersion()
        {
            ulong n;
            return ulong.TryParse(AnalyticsInfo.VersionInfo.DeviceFamilyVersion, out n)
                ? string.Format("{0}.{1}.{2}.{3}", (n >> 48) & 0xFFFF, (n >> 32) & 0xFFFF, (n >> 16) & 0xFFFF, n & 0xFFFF)
                : "";
        }

        public static async Task ProbeAsync()
        {
            if (video != null) return;
            var q = new CodecQuery();
            var v = new List<string>();
            foreach (var kv in VideoSubtypes)
                if ((await q.FindAllAsync(CodecKind.Video, CodecCategory.Decoder, kv.Value)).Count > 0) v.Add(kv.Key);
            var a = new List<string>();
            foreach (var kv in AudioSubtypes)
                if ((await q.FindAllAsync(CodecKind.Audio, CodecCategory.Decoder, kv.Value)).Count > 0) a.Add(kv.Key);
            if (!v.Contains("h264")) v.Add("h264");
            if (!a.Contains("aac")) a.Add("aac");
            a.Add("pcm_s16le");
            video = v;
            audio = a;
            hdr10 = DetectHdr10();
        }

        /// <summary>
        /// HDR10 output: only when the console offers the app an HDMI mode flagged for PQ. A TV that
        /// takes HDR10 isn't enough: tested on a Series X with no PQ modes offered, HDR10 playback
        /// stayed SDR (the console doesn't switch on its own), so the server must tone-map instead.
        /// </summary>
        static bool DetectHdr10()
        {
            return ModesOfferHdr10();
        }

        public static bool ModesOfferHdr10()
        {
            try
            {
                var hdmi = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView();
                return hdmi != null && hdmi.GetSupportedDisplayModes().Any(m => m.IsSmpte2084Supported);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TvTakesHdr10()
        {
            try
            {
                return Windows.Graphics.Display.DisplayInformation.GetForCurrentView().GetAdvancedColorInfo()
                    .IsHdrMetadataFormatCurrentlySupported(Windows.Graphics.Display.HdrMetadataFormat.Hdr10);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static JsonObject Hdr(bool on) => new JsonObject()
            .With("hdr10", on && video.Contains("hevc"))
            .With("hdr10_plus", false)
            .With("hlg", false)
            .With("dolby_vision_profiles", new JsonArray());

        static JsonObject Subtitles() => new JsonObject()
            .With("embedded_text", false)
            .With("sidecar_text", true)
            .With("ass_styling", false)
            .With("embedded_bitmap", false)
            .With("sidecar_bitmap", false)
            .With("font_attachments", false);

        static JsonArray Arr(IEnumerable<string> values) => Json.Strings(values.ToArray());

        static JsonObject Delivery(string[] containers, bool hdr) => new JsonObject()
            .With("enabled", true)
            .With("supported_on_device", true)
            .With("containers", Json.Strings(containers))
            .With("video_codecs", Arr(video))
            .With("audio_decode_codecs", Arr(audio))
            .With("audio_passthrough_codecs", new JsonArray())
            .With("hdr_details", Hdr(hdr))
            .With("subtitles", Subtitles())
            .With("features", new JsonArray())
            .With("auth_header_refresh", false)
            .With("validated_claims", new JsonArray())
            .With("transformations", new JsonArray());

        public static readonly string[] ClientFeatures = { "seek_reanchor_v1", "direct_stream_resume_v1", "subrip_sidecar_v1" };

        public static JsonObject ClientCapabilities() => new JsonObject()
            .With("video_evidence", "declared")
            .With("audio_evidence", "declared")
            .With("codecs_video", Arr(video))
            .With("codecs_video_hardware", Arr(video))
            .With("codecs_audio", Arr(audio))
            .With("containers", Json.Strings("mp4", "mkv", "mov", "ts", "m2ts", "webm"))
            .With("max_resolution", ReportedMaxResolution)
            .With("hdr", hdr10)
            .With("hdr_details", Hdr(hdr10));

        public static JsonObject PlaybackContext()
        {
            var deliveries = new JsonObject()
                .With("original_http", Delivery(new[] { "mp4", "mkv", "mov", "ts", "m2ts" }, hdr10))
                .With("progressive", Delivery(new[] { "mp4", "mkv" }, hdr10))
                .With("hls", Delivery(new[] { "hls" }, hdr10));
            var os = OsVersion();
            return new JsonObject()
                .With("protocol_version", 3)
                .With("form_factor", "tv")
                .With("app_version", SiloClient.AppVersion)
                .With("app_channel", "community")
                .With("device", new JsonObject()
                    .With("platform", IsXbox ? "xbox" : "windows")
                    .With("os_version", os)
                    .With("manufacturer", "Microsoft"))
                .With("output", new JsonObject().With("hdr_details", Hdr(hdr10)))
                .With("deliveries", deliveries);
        }
    }
}
