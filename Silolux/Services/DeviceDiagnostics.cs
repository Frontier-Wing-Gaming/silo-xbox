using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation.Metadata;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Render;
using Windows.System.Profile;

namespace Silolux.Services
{
    public enum DiagState { Yes, No, Info }

    public class DiagRow
    {
        public string Name;
        public string Value;
        public DiagState State;
        /// <summary>What the app tells the server about this, when it says anything; null otherwise.</summary>
        public string Reported;
        /// <summary>The device can do it but the app doesn't report it: a candidate to add.</summary>
        public bool Unreported;
    }

    public class DiagSection
    {
        public string Title;
        public List<DiagRow> Rows = new List<DiagRow>();

        public DiagRow Add(string name, string value, DiagState state = DiagState.Info, string reported = null, bool unreported = false)
        {
            var row = new DiagRow { Name = name, Value = value, State = state, Reported = reported, Unreported = unreported };
            Rows.Add(row);
            return row;
        }

        public DiagRow Check(string name, bool ok, string reported = null, bool unreported = false) =>
            Add(name, ok ? "Yes" : "No", ok ? DiagState.Yes : DiagState.No, reported, unreported);
    }

    /// <summary>
    /// What this device can decode and output, next to what PlaybackCapabilities reports to the
    /// server, so reports can be extended only with formats the hardware really handles.
    /// Every probe is guarded: a missing API reads as "unknown", never as a crash.
    /// </summary>
    public static class DeviceDiagnostics
    {
        // Media Foundation subtypes for FourCC codes are the FourCC (little-endian) + this base GUID.
        static string FourCc(string code)
        {
            var b = Encoding.ASCII.GetBytes(code);
            var data1 = b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
            return "{" + data1.ToString("X8") + "-0000-0010-8000-00AA00389B71}";
        }

        static readonly string[][] Video =
        {
            // name, subtype, server codec name (as PlaybackCapabilities reports it)
            new[] { "H.264 / AVC", CodecSubtypes.VideoFormatH264, "h264" },
            new[] { "HEVC / H.265", CodecSubtypes.VideoFormatHevc, "hevc" },
            new[] { "AV1", CodecSubtypes.VideoFormatAv1, "av1" },
            new[] { "VP9", CodecSubtypes.VideoFormatVP90, "vp9" },
            new[] { "VP8", CodecSubtypes.VideoFormatVP80, "vp8" },
            new[] { "MPEG-2", CodecSubtypes.VideoFormatMpeg2, "mpeg2video" },
            new[] { "MPEG-4 Part 2", CodecSubtypes.VideoFormatMP4V, "mpeg4" },
            new[] { "VC-1", CodecSubtypes.VideoFormatWvc1, "vc1" },
            new[] { "Dolby Vision (dvhe)", FourCc("dvhe"), null },
            new[] { "Dolby Vision (dvh1)", FourCc("dvh1"), null },
            new[] { "Dolby Vision AV1 (dav1)", FourCc("dav1"), null },
        };

        static readonly string[][] Audio =
        {
            new[] { "AAC", CodecSubtypes.AudioFormatAac, "aac" },
            new[] { "MP3", CodecSubtypes.AudioFormatMP3, "mp3" },
            new[] { "MPEG audio (MP2)", CodecSubtypes.AudioFormatMPeg, "mp2" },
            new[] { "Dolby Digital (AC-3)", CodecSubtypes.AudioFormatDolbyAC3, "ac3" },
            new[] { "Dolby Digital Plus (E-AC-3)", CodecSubtypes.AudioFormatDolbyDDPlus, "eac3" },
            new[] { "DTS (core)", CodecSubtypes.AudioFormatDts, "dts" },
            new[] { "FLAC", CodecSubtypes.AudioFormatFlac, "flac" },
            new[] { "ALAC", CodecSubtypes.AudioFormatAlac, "alac" },
            new[] { "Opus", CodecSubtypes.AudioFormatOpus, "opus" },
        };

        public static async Task<List<DiagSection>> RunAsync()
        {
            await PlaybackCapabilities.ProbeAsync();
            var sections = new List<DiagSection>
            {
                DeviceSection(),
                DisplaySection(),
                await AudioOutputSection(),
                await DecoderSection("Video decoders", CodecKind.Video, Video, PlaybackCapabilities.ReportedVideo),
                await DecoderSection("Audio decoders", CodecKind.Audio, Audio, PlaybackCapabilities.ReportedAudio),
                ReportedSection(),
                await NetworkSection(),
            };
            return sections;
        }

        static DiagSection DeviceSection()
        {
            var s = new DiagSection { Title = "Device" };
            s.Add("Device family", AnalyticsInfo.VersionInfo.DeviceFamily);
            s.Add("OS version", PlaybackCapabilities.OsVersion());
            s.Add("App", SiloClient.ClientName + " " + SiloClient.AppVersion);
            try
            {
                var info = new Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation();
                if (!string.IsNullOrEmpty(info.SystemProductName)) s.Add("Model", info.SystemProductName);
            }
            catch (Exception) { }
            return s;
        }

        static DiagSection DisplaySection()
        {
            var s = new DiagSection { Title = "Display" };
            Windows.Graphics.Display.Core.HdmiDisplayInformation hdmi = null;
            try { hdmi = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView(); }
            catch (Exception) { }

            if (hdmi != null)
            {
                try
                {
                    var current = hdmi.GetCurrentDisplayMode();
                    var modes = hdmi.GetSupportedDisplayModes();
                    s.Add("Current mode", string.Format(CultureInfo.InvariantCulture, "{0}×{1} @ {2:0.###} Hz, {3}-bit, {4}",
                        current.ResolutionWidthInRawPixels, current.ResolutionHeightInRawPixels, current.RefreshRate,
                        current.BitsPerPixel / 3, current.ColorSpace));
                    var top = modes.OrderByDescending(m => m.ResolutionWidthInRawPixels * m.ResolutionHeightInRawPixels).First();
                    s.Add("Highest resolution", top.ResolutionWidthInRawPixels + "×" + top.ResolutionHeightInRawPixels);
                    var rates = modes.Where(m => m.ResolutionWidthInRawPixels == current.ResolutionWidthInRawPixels &&
                                                 m.ResolutionHeightInRawPixels == current.ResolutionHeightInRawPixels)
                        .Select(m => Math.Round(m.RefreshRate, 3)).Distinct().OrderBy(r => r).ToList();
                    s.Add("Refresh rates", string.Join(", ", rates.Select(r => r.ToString("0.###", CultureInfo.InvariantCulture))));
                    var film = rates.Any(r => Math.Abs(r - 24) < 0.1 || Math.Abs(r - 23.976) < 0.01);
                    s.Add("24 Hz for films", film ? "Allowed" : "Not offered: turn on Allow 24 Hz in Video modes",
                        film ? DiagState.Yes : DiagState.No);
                    if (modes.Any(m => m.IsSmpte2084Supported))
                        s.Check("HDR10 (PQ) output", true, Yes(PlaybackCapabilities.ReportsHdr10));
                    else if (PlaybackCapabilities.TvTakesHdr10())
                        s.Add("HDR10 (PQ) output", "No: the TV takes HDR10 but the console offers this app no HDR mode",
                            DiagState.No, Yes(PlaybackCapabilities.ReportsHdr10));
                    else
                        s.Check("HDR10 (PQ) output", false, Yes(PlaybackCapabilities.ReportsHdr10));
                    s.Check("HDR10 metadata (ST 2086)", modes.Any(m => m.Is2086MetadataSupported));
                    if (ApiInformation.IsPropertyPresent("Windows.Graphics.Display.Core.HdmiDisplayMode", "IsDolbyVisionLowLatencySupported"))
                        s.Check("Dolby Vision (low latency)", modes.Any(m => m.IsDolbyVisionLowLatencySupported), "not reported");

                    // Every mode the console hands the app, grouped by shape and colour setup, so a
                    // missing HDR or 10-bit mode shows up directly rather than as a single "No".
                    s.Add("Modes offered", modes.Count.ToString(CultureInfo.InvariantCulture), DiagState.Info);
                    var groups = modes
                        .GroupBy(m => string.Format(CultureInfo.InvariantCulture, "{0}×{1} {2}-bit {3} {4}{5}",
                            m.ResolutionWidthInRawPixels, m.ResolutionHeightInRawPixels, m.BitsPerPixel / 3,
                            m.ColorSpace, m.PixelEncoding, m.IsSmpte2084Supported ? " HDR10" : ""))
                        .OrderByDescending(g => g.First().ResolutionWidthInRawPixels * g.First().ResolutionHeightInRawPixels)
                        .ThenBy(g => g.Key);
                    foreach (var g in groups)
                        s.Add("  " + g.Key, string.Join(", ", g.Select(m => Math.Round(m.RefreshRate, 3)).Distinct().OrderBy(r => r)
                            .Select(r => r.ToString("0.###", CultureInfo.InvariantCulture))) + " Hz",
                            g.First().IsSmpte2084Supported ? DiagState.Yes : DiagState.Info);
                }
                catch (Exception e)
                {
                    s.Add("HDMI modes", "Unavailable: " + e.Message, DiagState.No);
                }
                // What Windows itself knows about the TV's HDR (independent of the mode list).
                try
                {
                    var color = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().GetAdvancedColorInfo();
                    s.Add("TV colour kind (Windows)", color.CurrentAdvancedColorKind.ToString(), DiagState.Info);
                    s.Add("TV reports HDR10", color.IsHdrMetadataFormatCurrentlySupported(Windows.Graphics.Display.HdrMetadataFormat.Hdr10) ? "Yes" : "No",
                        DiagState.Info);
                    s.Add("TV peak brightness", color.MaxLuminanceInNits > 0 ? color.MaxLuminanceInNits.ToString("0", CultureInfo.InvariantCulture) + " nits" : "Unknown",
                        DiagState.Info);
                }
                catch (Exception e)
                {
                    s.Add("TV colour info", "Unavailable: " + e.Message, DiagState.Info);
                }
            }
            else
            {
                s.Add("HDMI mode control", "Not available on this device (a PC)", DiagState.Info);
                try
                {
                    var color = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().GetAdvancedColorInfo();
                    s.Add("Colour mode", color.CurrentAdvancedColorKind.ToString(),
                        color.CurrentAdvancedColorKind == Windows.Graphics.Display.AdvancedColorKind.HighDynamicRange ? DiagState.Yes : DiagState.Info);
                    s.Check("HDR10 metadata accepted", color.IsHdrMetadataFormatCurrentlySupported(Windows.Graphics.Display.HdrMetadataFormat.Hdr10),
                        Yes(PlaybackCapabilities.ReportsHdr10));
                    s.Add("Peak brightness", ((int)color.MaxLuminanceInNits) + " nits");
                    var info = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    s.Add("Screen", info.ScreenWidthInRawPixels + "×" + info.ScreenHeightInRawPixels,
                        DiagState.Info, PlaybackCapabilities.ReportedMaxResolution);
                }
                catch (Exception) { }
            }
            return s;
        }

        static async Task<DiagSection> AudioOutputSection()
        {
            var s = new DiagSection { Title = "Audio output" };
            try
            {
                var result = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media));
                if (result.Status == AudioGraphCreationStatus.Success)
                {
                    using (var graph = result.Graph)
                    {
                        var p = graph.EncodingProperties;
                        s.Add("Output", p.ChannelCount + " channels, " + p.SampleRate + " Hz");
                    }
                }
            }
            catch (Exception) { }

            try
            {
                var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                var spatial = SpatialAudioDeviceConfiguration.GetForDeviceId(id);
                s.Check("Spatial audio", spatial.IsSpatialAudioSupported);
                s.Add("Active spatial format", Spatial(spatial.ActiveSpatialAudioFormat) ?? "None");
                foreach (var f in SpatialFormats)
                    s.Check(f[0], spatial.IsSpatialAudioFormatSupported(f[1]));
            }
            catch (Exception e)
            {
                s.Add("Spatial audio", "Unavailable: " + e.Message, DiagState.Info);
            }
            s.Add("Passthrough to a receiver", "Not reported: the app always decodes audio itself", DiagState.Info, "none");
            return s;
        }

        static string[][] spatialFormats;
        static string[][] SpatialFormats => spatialFormats ?? (spatialFormats = new[]
        {
            new[] { "Dolby Atmos for home theater", SpatialAudioFormatSubtype.DolbyAtmosForHomeTheater },
            new[] { "Dolby Atmos for headphones", SpatialAudioFormatSubtype.DolbyAtmosForHeadphones },
            new[] { "DTS:X for home theater", SpatialAudioFormatSubtype.DTSXForHomeTheater },
            new[] { "DTS:X Ultra", SpatialAudioFormatSubtype.DTSXUltra },
            new[] { "DTS Headphone:X", SpatialAudioFormatSubtype.DTSHeadphoneX },
            new[] { "Windows Sonic", SpatialAudioFormatSubtype.WindowsSonic },
        });

        static string Spatial(string subtype)
        {
            // No active format reads as the empty GUID rather than null.
            Guid g;
            if (string.IsNullOrEmpty(subtype) || (Guid.TryParse(subtype, out g) && g == Guid.Empty)) return null;
            var match = SpatialFormats.FirstOrDefault(f => string.Equals(f[1], subtype, StringComparison.OrdinalIgnoreCase));
            return match != null ? match[0] : subtype;
        }

        static async Task<DiagSection> DecoderSection(string title, CodecKind kind, string[][] codecs, IReadOnlyCollection<string> reported)
        {
            var s = new DiagSection { Title = title };
            var query = new CodecQuery();
            foreach (var c in codecs)
            {
                IReadOnlyList<CodecInfo> found;
                try { found = await query.FindAllAsync(kind, CodecCategory.Decoder, c[1]); }
                catch (Exception) { found = new List<CodecInfo>(); }
                var ok = found.Count > 0;
                var isReported = c[2] != null && reported.Contains(c[2]);
                var row = s.Check(c[0], ok, isReported ? "reported" : "not reported", ok && !isReported);
                if (ok)
                {
                    var names = string.Join(", ", found.Select(f => f.DisplayName).Where(n => !string.IsNullOrEmpty(n)).Distinct());
                    if (!string.IsNullOrEmpty(names)) row.Value = "Yes · " + names;
                }
            }
            if (kind == CodecKind.Audio)
                s.Add("Dolby TrueHD, DTS-HD MA", "No Windows decoder to query", DiagState.Info, "not reported");
            return s;
        }

        static DiagSection ReportedSection()
        {
            var s = new DiagSection { Title = "Reported to the server" };
            s.Add("Video codecs", string.Join(", ", PlaybackCapabilities.ReportedVideo));
            s.Add("Audio codecs", string.Join(", ", PlaybackCapabilities.ReportedAudio));
            s.Add("Max resolution", PlaybackCapabilities.ReportedMaxResolution);
            s.Add("HDR", PlaybackCapabilities.ReportsHdr10 ? "HDR10" : "None (SDR)");
            s.Add("Dolby Vision", "None");
            s.Add("Audio passthrough", "None");
            s.Add("Subtitles", "Sidecar text only (SRT)");
            return s;
        }

        static async Task<DiagSection> NetworkSection()
        {
            var s = new DiagSection { Title = "Network" };
            s.Add("Link", BandwidthEstimator.Transport());
            var api = SiloClient.Current;
            if (api == null || !api.SignedIn) return s;
            s.Add("Server", api.BaseUri.Host);
            try
            {
                var c = await api.GetAsync("/api/v2/playback/connection");
                var remote = c.Str("connection") != "local";
                s.Add("Server sees this device as", c.Str("connection") ?? "unknown");
                var caps = await api.GetAsync("/api/v2/playback/capabilities");
                var planned = BandwidthEstimator.Supported(caps);
                s.Add("Quality fitted to connection", planned ? "Yes" : "No: this server doesn't plan by bandwidth",
                    planned ? DiagState.Yes : DiagState.No);
                // Playback probes only when it starts; on a remote server with no stored result, test
                // now so the page shows what auto quality will work with.
                if (planned && remote && c.Obj("probe") == null)
                {
                    BandwidthEstimator.Forget();
                    await BandwidthEstimator.EstimateAsync(api, caps);
                    c = await api.GetAsync("/api/v2/playback/connection");
                }
                var probe = c.Obj("probe");
                if (probe != null)
                    s.Add("Speed test", Mbps(probe.Num("kbps")) + (probe.Bool("lower_bound") ? " or more" : ""));
                else if (remote)
                    s.Add("Speed test", "No result (the server's test download failed)", DiagState.No);
                var budget = c.Num("auto_budget_kbps");
                if (remote && budget > 0)
                    s.Add("Auto quality budget", Mbps(budget) + " (originals above this get transcoded)");
                var limit = c.Obj("learned_limit");
                if (limit != null)
                    s.Add("Learned limit", Mbps(limit.Num("kbps")) + " (from earlier stalls)", DiagState.No);
            }
            catch (ApiException) { }
            return s;
        }

        static string Yes(bool on) => on ? "reported" : "not reported";

        static string Mbps(double kbps) => (kbps / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " Mbps";

        /// <summary>Plain text of the report, for diagnostics.txt and the clipboard.</summary>
        public static string ToText(IEnumerable<DiagSection> sections)
        {
            var b = new StringBuilder();
            b.AppendLine("Silolux device diagnostics, " + DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture));
            foreach (var s in sections)
            {
                b.AppendLine();
                b.AppendLine("== " + s.Title);
                foreach (var r in s.Rows)
                {
                    b.Append("  ").Append(r.Name).Append(": ").Append(r.Value);
                    if (r.Reported != null) b.Append("   [server: ").Append(r.Reported).Append(']');
                    if (r.Unreported) b.Append("   <- supported but not reported");
                    b.AppendLine();
                }
            }
            return b.ToString();
        }
    }
}
