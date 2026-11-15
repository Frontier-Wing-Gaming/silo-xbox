using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Silolux.Services
{
    public class SidecarSubtitle
    {
        public string TrackId;
        public string Label;
        public string Language;
        public Uri Url;
    }

    /// <summary>
    /// LocalState\playback-log.txt: each playback start, progress report and stop with the server's
    /// answer, newest last. Failed reports are otherwise silent, and a server that drops them
    /// leaves the resume point where it was.
    /// </summary>
    public static class PlaybackLog
    {
        static readonly System.Threading.SemaphoreSlim Gate = new System.Threading.SemaphoreSlim(1, 1);

        public static string Secs(double seconds) =>
            TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) +
            string.Format(System.Globalization.CultureInfo.InvariantCulture, " ({0:0.#} s)", seconds);

        static readonly System.Text.RegularExpressions.Regex Timing = new System.Text.RegularExpressions.Regex(
            @"((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})");

        /// <summary>A fetched subtitle file in one line: its URL without credentials, cue count, first and last cues.</summary>
        public static string Describe(Uri url, string text)
        {
            var query = string.Join("&", (url.Query ?? "").TrimStart('?').Split('&')
                .Where(p => p.Length > 0 && !p.StartsWith("token=") && !p.StartsWith("st="))
                .Select(p => p.Length > 60 ? p.Substring(0, 60) + "…" : p));
            var cues = Timing.Matches(text);
            var line = url.AbsolutePath + (query.Length > 0 ? "?" + query : "") + " · " + text.Length + " chars, " + cues.Count + " cues";
            if (cues.Count == 0) return line + " · starts: " + Snippet(text, 0);
            var first = cues[0];
            var last = cues[cues.Count - 1];
            return line + " · first " + first.Groups[1].Value + " \"" + Snippet(text, first.Index + first.Length) + "\"" +
                   " · last " + last.Groups[1].Value + " \"" + Snippet(text, last.Index + last.Length) + "\"";
        }

        static string Snippet(string text, int from)
        {
            var s = text.Substring(Math.Min(from, text.Length)).TrimStart();
            var end = s.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0) s = s.Substring(0, end);
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        public static string Outcome(JsonObject answer)
        {
            if (answer == null) return "ok";
            var outcome = answer.Str("outcome") ?? "ok";
            var accepted = answer.Obj("accepted");
            return outcome + (accepted != null ? ", saved " + Secs(accepted.Num("position")) : "");
        }

        public static async void Write(string line)
        {
            await Gate.WaitAsync();
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync("playback-log.txt", Windows.Storage.CreationCollisionOption.OpenIfExists);
                var props = await file.GetBasicPropertiesAsync();
                if (props.Size > 64 * 1024)
                {
                    // Keep the newer half.
                    var old = await Windows.Storage.FileIO.ReadTextAsync(file);
                    await Windows.Storage.FileIO.WriteTextAsync(file, old.Substring(old.Length / 2));
                }
                await Windows.Storage.FileIO.AppendTextAsync(file,
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
            }
            catch (Exception) { }
            finally { Gate.Release(); }
        }
    }

    public class QualityOption
    {
        public string Label;
        public string Name;
        public int Kbps;
    }

    /// <summary>
    /// One playback attempt under protocol v3: start, failure replans,
    /// sequenced progress and the stop. The plan's URLs and timeline are
    /// authoritative; nothing here invents a stream URL or recipe.
    /// </summary>
    public class PlaybackSession
    {
        const int MaxAttempts = 8;

        readonly SiloClient api = SiloClient.Current;
        readonly string attemptId = Guid.NewGuid().ToString("D");
        readonly List<string> attemptedKeys = new List<string>();
        string installationId;
        string planAttemptId;
        long sequence;
        int attemptCount = 1;
        bool stopped;
        int bandwidthKbps;
        // Off once KeepSurroundAsync moved this session to HLS; later replans keep that.
        bool progressive = true;
        string[] features = PlaybackCapabilities.ClientFeatures;

        public string SessionId { get; private set; }
        public JsonObject Plan { get; private set; }
        public bool EndedByAdministrator { get; private set; }

        public string Delivery => Plan.Str("delivery");
        public bool IsHls => Plan.Path("stream")?.Str("protocol") == "hls";
        public Uri StreamUrl => api.MediaUrl(Plan.Path("stream").Str("url"));
        public double PlayerStartSeconds => Plan.Path("timeline")?.Num("player_start_seconds") ?? 0;
        double Offset => Plan.Path("timeline")?.Num("timeline_offset_seconds") ?? 0;
        /// <summary>False for a stream that starts mid-film (a server remux or transcode begun at the resume point).</summary>
        public bool CanSeekAnywhere => Plan.Path("timeline")?.Bool("can_seek_anywhere", true) ?? true;
        /// <summary>The earliest film position this stream holds; seeking before it needs a new stream.</summary>
        public double SeekWindowStart => Plan.Path("timeline")?.Num("seek_window_start_seconds") ?? 0;
        /// <summary>The film's length in seconds, or 0 when the plan doesn't say.</summary>
        public double SourceDuration => Plan.Path("source")?.Num("duration_seconds") ?? 0;
        public double SourcePosition(double playerSeconds) => Math.Max(0, playerSeconds + Offset);
        public double PlayerPosition(double sourceSeconds) => Math.Max(0, sourceSeconds - Offset);
        /// <summary>Film time at the player's 0: non-zero for a stream the server started mid-film.</summary>
        public double TimelineOffset => Offset;

        static readonly System.Text.RegularExpressions.Regex CueTime = new System.Text.RegularExpressions.Regex(
            @"(?:(\d+):)?(\d{1,2}):(\d{2})([.,])(\d{1,3})");

        /// <summary>
        /// A WebVTT or SubRip track moved <paramref name="seconds"/> earlier: subtitle files are timed
        /// from the film's start, but a stream the server started mid-film puts the player's 0 at its
        /// origin, so unshifted lines arrive that much late. Cues that end before 0 are dropped.
        /// </summary>
        public static string ShiftCues(string text, double seconds)
        {
            var blocks = System.Text.RegularExpressions.Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n{2,}");
            var kept = new List<string>();
            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                var at = Array.FindIndex(lines, l => l.Contains("-->"));
                if (at < 0) { kept.Add(block); continue; }
                var times = CueTime.Matches(lines[at]);
                if (times.Count < 2) { kept.Add(block); continue; }
                var start = Seconds(times[0]) - seconds;
                var end = Seconds(times[1]) - seconds;
                if (end <= 0) continue;
                var sep = times[0].Groups[4].Value;
                // Rebuild the timing line, keeping any WebVTT cue settings after the end time.
                var rest = lines[at].Substring(times[1].Index + times[1].Length);
                lines[at] = Stamp(Math.Max(0, start), sep) + " --> " + Stamp(end, sep) + rest;
                kept.Add(string.Join("\n", lines));
            }
            return string.Join("\n\n", kept);
        }

        static double Seconds(System.Text.RegularExpressions.Match m)
        {
            var h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            var frac = m.Groups[5].Value;
            return h * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value)
                 + int.Parse(frac) / Math.Pow(10, frac.Length);
        }

        static string Stamp(double seconds, string sep)
        {
            var ms = (long)Math.Round(seconds * 1000);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}{3}{4:000}",
                ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, sep, ms % 1000);
        }
        public int? SelectedAudioIndex
        {
            get
            {
                var n = Plan.Path("selected_tracks/audio")?.NumOrNull("index");
                return n.HasValue ? (int?)(int)n.Value : null;
            }
        }
        public string SelectedSubtitleId => Plan.Path("selected_tracks/subtitle")?.Str("id");

        /// <summary>
        /// The server drew the chosen subtitle into the picture (subtitle mode burn_in; a transcode can,
        /// such as one an automatic step-down switches to). Its sidecar must not be shown on top, and
        /// another choice needs a new stream. Other modes (render, off, hls_rendition, embedded) leave
        /// the picture clean.
        /// </summary>
        public bool SubtitleBurnedIn => Plan.Path("subtitle")?.Str("mode") == "burn_in";
        /// <summary>The file actually playing (a fallback can differ from the one requested).</summary>
        public string EffectiveFileId => Plan.Str("effective_media_file_id") ?? Plan.Str("requested_media_file_id");

        /// <summary>The server's track id for the nth audio track of the playing file.</summary>
        public string AudioTrackId(int index) => "file:" + EffectiveFileId + ":audio:" + index;

        public IEnumerable<SidecarSubtitle> SidecarSubtitles()
        {
            var inventory = Plan.Path("subtitle")?.Objects("inventory") ?? Enumerable.Empty<JsonObject>();
            foreach (var t in inventory)
            {
                if (t.Str("delivery") != "sidecar" || string.IsNullOrEmpty(t.Str("url"))) continue;
                var codec = (t.Str("codec") ?? "").ToLowerInvariant();
                // Media Foundation's timed-text source reads text formats only.
                if (codec == "pgs" || codec == "dvd_subtitle" || codec == "hdmv_pgs_subtitle") continue;
                yield return new SidecarSubtitle
                {
                    TrackId = t.Str("track_id"),
                    Label = t.Str("label") ?? t.Str("language") ?? "Subtitles",
                    Language = t.Str("language") ?? "",
                    Url = api.MediaUrl(codec == "ass" || codec == "ssa" ? AsWebVtt(t.Str("url")) : t.Str("url")),
                };
            }
        }

        /// <summary>
        /// The same subtitle as WebVTT: the server converts by extension (…/subtitles/0.ass → 0.vtt).
        /// Styled ASS/SSA can't be styled here anyway, and the timed-text reader takes WebVTT reliably.
        /// </summary>
        static string AsWebVtt(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var q = url.IndexOf('?');
            var path = q < 0 ? url : url.Substring(0, q);
            var dot = path.LastIndexOf('.');
            if (dot < 0 || dot < path.LastIndexOf('/')) return url;
            return path.Substring(0, dot) + ".vtt" + (q < 0 ? "" : url.Substring(q));
        }

        /// <param name="startPosition">Film seconds to start at, or null to resume where the server saved.</param>
        public static async Task<PlaybackSession> StartAsync(string fileId, double? startPosition, bool pinFile)
        {
            await PlaybackCapabilities.ProbeAsync();
            var s = new PlaybackSession();
            var caps = await s.api.PlaybackCapabilitiesAsync();
            if (!caps.Bool("allowed", true)) throw new ApiException(0, "not_configured", "Playback is not set up on this server.");
            s.installationId = caps.Str("installation_id");
            // Bandwidth planning is opt-in: the server rejects its fields from clients that don't declare it.
            s.features = BandwidthEstimator.Supported(caps)
                ? PlaybackCapabilities.ClientFeatures.Concat(new[] { BandwidthEstimator.Feature }).ToArray()
                : PlaybackCapabilities.ClientFeatures;
            s.bandwidthKbps = await BandwidthEstimator.EstimateAsync(s.api, caps);

            var body = new JsonObject()
                .With("installation_id", s.installationId)
                .With("protocol_version", 3)
                .With("client_features", Json.Strings(s.features))
                .With("file_id", fileId)
                .With("profile_id", AppSettings.ProfileId)
                .With("playback_attempt_id", s.attemptId)
                .With("quality_preference", "auto")
                .With("subtitle_fidelity_preference", "compatible")
                .With("client_capabilities", PlaybackCapabilities.ClientCapabilities())
                .With("client_playback_context", PlaybackCapabilities.PlaybackContext());
            // Sent, it's where playback begins (the title page sends the resume point it shows, or 0
            // for "Play from start"). Omitted, the server decides, and not every server resumes.
            if (startPosition.HasValue) body.With("start_position", Math.Max(0, startPosition.Value));
            if (pinFile) body.With("allow_alternate_versions", false);
            s.AddBandwidth(body);

            s.Adopt(await s.api.StartPlaybackAsync(body));
            PlaybackLog.Write("start file " + fileId + ": asked " +
                              (startPosition.HasValue ? "start_position " + PlaybackLog.Secs(startPosition.Value) : "no start_position (server resumes)") +
                              "; plan starts at " + PlaybackLog.Secs(s.SourcePosition(s.PlayerStartSeconds)) + " via " + s.Delivery +
                              ", " + (s.Plan.Path("effective_recipe")?.NumOrNull("bitrate_kbps") is double sent ? sent + " kbps" : "bitrate unknown") +
                              " for a " + s.bandwidthKbps + " kbps estimate (" + BandwidthEstimator.LastSource + ")" +
                              ", session " + s.SessionId + ", profile " + AppSettings.ProfileId + ", server " + AppSettings.ServerUrl);
            await s.KeepSurroundAsync();
            return s;
        }

        /// <summary>
        /// The server's progressive route converts audio the console can't decode (DTS, TrueHD) to
        /// stereo AAC; its HLS route keeps 5.1. When that downmix would throw away channels the
        /// console's output actually has, ask again without the progressive route.
        /// </summary>
        async Task KeepSurroundAsync()
        {
            if (!(Delivery ?? "").Contains("progressive")) return;
            var source = Plan.Path("source")?.Num("audio_channels") ?? 0;
            var sent = Plan.Path("effective_recipe")?.Num("audio_channels") ?? 0;
            if (source <= 2 || sent <= 0 || sent > 2) return;
            if (await PlaybackCapabilities.OutputChannelsAsync() <= 2) return;
            progressive = false;
            try
            {
                var tracks = Plan.Obj("selected_tracks") ?? new JsonObject();
                await ReplanAsync(ReplanBody("track_change", PlayerStartSeconds, JsonObject.Parse(tracks.Stringify())));
            }
            catch (ApiException)
            {
                // Keep the stereo plan rather than fail playback.
                progressive = true;
            }
        }

        void Adopt(JsonObject decision)
        {
            if (decision.Str("outcome") != "playable" || decision.Obj("playback_plan") == null)
            {
                var msg = decision.Path("terminal")?.Str("message") ?? "This title can't play on this console.";
                throw new ApiException(0, decision.Path("terminal")?.Str("reason") ?? "unplayable", msg);
            }
            SessionId = decision.Str("session_id") ?? SessionId;
            Plan = decision.Obj("playback_plan");
            planAttemptId = Guid.NewGuid().ToString("D");
            var key = Plan.Str("plan_attempt_key");
            if (!string.IsNullOrEmpty(key) && !attemptedKeys.Contains(key)) attemptedKeys.Add(key);
        }

        /// <summary>
        /// Asks the server for another route after the console failed this one.
        /// Returns false once the attempt budget is spent.
        /// </summary>
        public async Task<bool> RecoverAsync(string classification, string message, double playerSeconds)
        {
            if (attemptCount >= MaxAttempts) return false;
            attemptCount++;
            var failure = new JsonObject().With("classification", classification);
            if (!string.IsNullOrEmpty(message))
                failure.With("message", message.Length > 400 ? message.Substring(0, 400) : message);
            var tracks = Plan.Obj("selected_tracks") ?? new JsonObject();
            var body = ReplanBody("failure_recovery", playerSeconds, JsonObject.Parse(tracks.Stringify()))
                .With("failure", failure);
            await ReplanAsync(body);
            return true;
        }

        /// <summary>
        /// The viewer picked an audio track (and keeps <paramref name="subtitleTrackId"/>, or
        /// null for subtitles off). A track_change replan: the answer is a new plan for the
        /// same title at <paramref name="playerSeconds"/>, which the caller loads.
        /// </summary>
        public async Task ChangeTracksAsync(int audioIndex, string subtitleTrackId, double playerSeconds)
        {
            await ReplanAsync(ReplanBody("track_change", playerSeconds, Tracks(audioIndex, subtitleTrackId)));
        }

        /// <summary>
        /// The viewer picked a quality: "auto" (the original when the connection carries it) or an
        /// available_qualities label. Kept for later replans, which would otherwise fall back to auto.
        /// </summary>
        public async Task ChangeQualityAsync(string preference, int audioIndex, string subtitleTrackId, double playerSeconds)
        {
            var previous = QualityPreference;
            QualityPreference = string.IsNullOrEmpty(preference) ? "auto" : preference;
            try
            {
                await ReplanAsync(ReplanBody("quality_change", playerSeconds, Tracks(audioIndex, subtitleTrackId)));
            }
            catch
            {
                QualityPreference = previous;
                throw;
            }
        }

        /// <summary>The bitrate this plan sends, or 0 when the plan doesn't say.</summary>
        public int PlanKbps => (int)(Plan.Path("effective_recipe")?.NumOrNull("bitrate_kbps") ?? 0);

        /// <summary>Whether the server takes observed throughput (it advertised buffering_quality_v1 and we declared it).</summary>
        public bool ReportsRebuffering => features.Contains(BandwidthEstimator.Feature);

        /// <summary>
        /// Auto quality couldn't keep up: a quality_change replan that reports what the stream really
        /// received (observed_rebuffer). The server plans with, and remembers for this device, the lower
        /// of that and the estimate, so this and later starts get a stream the connection carries.
        /// </summary>
        public async Task StepDownAsync(int receivedKbps, int audioIndex, string subtitleTrackId, double playerSeconds)
        {
            var received = Math.Max(100, Math.Min(1000000, receivedKbps));
            var before = PlanKbps;
            var body = ReplanBody("quality_change", playerSeconds, Tracks(audioIndex, subtitleTrackId))
                .With("bandwidth_estimate_kbps", bandwidthKbps > 0 ? bandwidthKbps : received)
                .With("bandwidth_estimate_source", "observed_rebuffer")
                .With("observed_throughput_kbps", received);
            await ReplanAsync(body);
            // Later replans (seeks, track changes) plan with what got through.
            bandwidthKbps = received;
            BandwidthEstimator.Observed(api, received);
            PlaybackLog.Write("stepped down: " + before + " kbps stream received about " + received + " kbps; now " +
                              PlanKbps + " kbps via " + Delivery + (ActiveQuality != null ? " (" + ActiveQuality + ")" : "") +
                              "; subtitle " + (SelectedSubtitleId ?? "none") +
                              (SelectedSubtitleId != null ? " (" + (Plan.Path("subtitle")?.Str("mode") ?? "?") + ")" : "") +
                              (string.IsNullOrEmpty(subtitleTrackId) || subtitleTrackId == SelectedSubtitleId ? "" : ", asked for " + subtitleTrackId));
        }

        /// <summary>
        /// The viewer sought outside what this stream holds: a seek_reanchor replan for a stream
        /// that starts at <paramref name="sourceSeconds"/> (film time), with the same tracks and quality.
        /// </summary>
        public async Task ReanchorAsync(double sourceSeconds)
        {
            var tracks = Plan.Obj("selected_tracks") ?? new JsonObject();
            await ReplanAsync(ReplanBody("seek_reanchor", sourceSeconds - Offset, JsonObject.Parse(tracks.Stringify())));
        }

        JsonObject Tracks(int audioIndex, string subtitleTrackId)
        {
            var tracks = new JsonObject().With("audio", new JsonObject()
                .With("id", AudioTrackId(audioIndex))
                .With("index", audioIndex));
            if (!string.IsNullOrEmpty(subtitleTrackId))
                tracks.With("subtitle", new JsonObject().With("id", subtitleTrackId));
            return tracks;
        }

        /// <summary>"auto" or the available_qualities label the viewer chose.</summary>
        public string QualityPreference { get; private set; } = "auto";

        /// <summary>The rung this plan delivers; null for the original or an encode between rungs.</summary>
        public string ActiveQuality => Plan.Str("active_quality");

        /// <summary>Fixed quality rungs this title offers, in menu order (the original isn't listed).</summary>
        public IEnumerable<QualityOption> AvailableQualities() =>
            Plan.Objects("available_qualities")
                .Select(q => new QualityOption
                {
                    Label = q.Str("label"),
                    Name = q.Str("display_name") ?? q.Str("label"),
                    Kbps = (int)q.Num("bitrate_kbps"),
                })
                .Where(q => !string.IsNullOrEmpty(q.Label));

        /// <summary>The measured connection speed, when there is one (see BandwidthEstimator).</summary>
        void AddBandwidth(JsonObject body)
        {
            if (bandwidthKbps <= 0) return;
            body.With("bandwidth_estimate_kbps", bandwidthKbps).With("bandwidth_estimate_source", "probe");
        }

        JsonObject ReplanBody(string operation, double playerSeconds, JsonObject selectedTracks)
        {
            var keys = new JsonArray();
            foreach (var k in attemptedKeys.Skip(Math.Max(0, attemptedKeys.Count - 16))) keys.Add(JsonValue.CreateStringValue(k));
            var body = new JsonObject()
                .With("installation_id", installationId)
                .With("protocol_version", 3)
                .With("client_features", Json.Strings(features))
                .With("operation", operation)
                .With("playback_attempt_id", attemptId)
                .With("replan_request_id", Guid.NewGuid().ToString("D"))
                .With("failed_plan_id", Plan.Str("plan_id"))
                .With("plan_attempt_id", planAttemptId)
                .With("plan_attempt_key", Plan.Str("plan_attempt_key"))
                .With("attempted_plan_keys", keys)
                .With("attempt_count", attemptCount)
                .With("quality_preference", QualityPreference)
                .With("position_seconds", SourcePosition(playerSeconds))
                .With("metered", false)
                .With("selected_tracks", selectedTracks)
                .With("client_capabilities", PlaybackCapabilities.ClientCapabilities())
                .With("client_playback_context", PlaybackCapabilities.PlaybackContext(progressive));
            AddBandwidth(body);
            return body;
        }

        async Task ReplanAsync(JsonObject body)
        {
            var decision = await api.SendAsync(Windows.Web.Http.HttpMethod.Post,
                "/api/v2/playback/" + Uri.EscapeDataString(SessionId) + "/replan", body);
            Adopt(decision);
        }

        public async Task ReportProgressAsync(double playerSeconds, bool paused)
        {
            if (stopped || EndedByAdministrator) return;
            var seq = ++sequence; // reports overlap; each logs its own number
            var body = new JsonObject()
                .With("installation_id", installationId)
                .With("sequence", seq)
                .With("position", SourcePosition(playerSeconds))
                .With("is_paused", paused);
            try
            {
                var answer = await api.ProgressAsync(SessionId, body);
                PlaybackLog.Write("progress #" + seq + " at " + PlaybackLog.Secs(SourcePosition(playerSeconds)) +
                                  (paused ? " paused" : "") + " -> " + PlaybackLog.Outcome(answer));
            }
            catch (ApiException e) when (e.Status == 410)
            {
                PlaybackLog.Write("progress #" + seq + " -> 410 ended by administrator");
                EndedByAdministrator = true;
            }
            catch (ApiException e)
            {
                // A missed sample is replaced by the next one.
                PlaybackLog.Write("progress #" + seq + " at " + PlaybackLog.Secs(SourcePosition(playerSeconds)) +
                                  " -> FAILED " + e.Status + " " + e.Code + ": " + e.Message);
            }
        }

        public async Task StopAsync(double playerSeconds)
        {
            if (stopped) return;
            stopped = true;
            sequence++;
            var body = new JsonObject()
                .With("installation_id", installationId)
                .With("stop_id", Guid.NewGuid().ToString("D"))
                .With("sequence", sequence)
                .With("position", SourcePosition(playerSeconds))
                .With("is_paused", true);
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    var answer = await api.StopAsync(SessionId, body);
                    PlaybackLog.Write("stop #" + sequence + " at " + PlaybackLog.Secs(SourcePosition(playerSeconds)) + " -> " + PlaybackLog.Outcome(answer));
                    return;
                }
                catch (ApiException e) when (e.Status == 404 || e.Status == 410)
                {
                    PlaybackLog.Write("stop #" + sequence + " -> " + e.Status + " " + e.Code + ": " + e.Message);
                    return;
                }
                catch (ApiException e)
                {
                    PlaybackLog.Write("stop #" + sequence + " try " + (i + 1) + " -> FAILED " + e.Status + " " + e.Code + ": " + e.Message);
                    await Task.Delay(2000);
                }
            }
        }
    }
}
