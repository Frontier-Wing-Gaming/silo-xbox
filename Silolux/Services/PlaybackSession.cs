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
        public double SourcePosition(double playerSeconds) => Math.Max(0, playerSeconds + Offset);
        public int? SelectedAudioIndex
        {
            get
            {
                var n = Plan.Path("selected_tracks/audio")?.NumOrNull("index");
                return n.HasValue ? (int?)(int)n.Value : null;
            }
        }
        public string SelectedSubtitleId => Plan.Path("selected_tracks/subtitle")?.Str("id");
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

        public static async Task<PlaybackSession> StartAsync(string fileId, double startPosition, bool pinFile)
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
            // Always sent, 0 included: without it the server resumes from the saved position,
            // which turned "Start from beginning" into another resume.
            body.With("start_position", Math.Max(0, startPosition));
            if (pinFile) body.With("allow_alternate_versions", false);
            s.AddBandwidth(body);

            s.Adopt(await s.api.StartPlaybackAsync(body));
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
            sequence++;
            var body = new JsonObject()
                .With("installation_id", installationId)
                .With("sequence", sequence)
                .With("position", SourcePosition(playerSeconds))
                .With("is_paused", paused);
            try
            {
                await api.ProgressAsync(SessionId, body);
            }
            catch (ApiException e) when (e.Status == 410)
            {
                EndedByAdministrator = true;
            }
            catch (ApiException)
            {
                // A missed sample is replaced by the next one.
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
                    await api.StopAsync(SessionId, body);
                    return;
                }
                catch (ApiException e) when (e.Status == 404 || e.Status == 410)
                {
                    return;
                }
                catch (ApiException)
                {
                    await Task.Delay(2000);
                }
            }
        }
    }
}
