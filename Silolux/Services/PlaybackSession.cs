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
                    Url = api.MediaUrl(t.Str("url")),
                };
            }
        }

        public static async Task<PlaybackSession> StartAsync(string fileId, double startPosition, bool pinFile)
        {
            await PlaybackCapabilities.ProbeAsync();
            var s = new PlaybackSession();
            var caps = await s.api.PlaybackCapabilitiesAsync();
            if (!caps.Bool("allowed", true)) throw new ApiException(0, "not_configured", "Playback is not set up on this server.");
            s.installationId = caps.Str("installation_id");

            var body = new JsonObject()
                .With("installation_id", s.installationId)
                .With("protocol_version", 3)
                .With("client_features", Json.Strings(PlaybackCapabilities.ClientFeatures))
                .With("file_id", fileId)
                .With("profile_id", AppSettings.ProfileId)
                .With("playback_attempt_id", s.attemptId)
                .With("quality_preference", "auto")
                .With("subtitle_fidelity_preference", "compatible")
                .With("client_capabilities", PlaybackCapabilities.ClientCapabilities())
                .With("client_playback_context", PlaybackCapabilities.PlaybackContext());
            if (startPosition > 0) body.With("start_position", startPosition);
            if (pinFile) body.With("allow_alternate_versions", false);

            s.Adopt(await s.api.StartPlaybackAsync(body));
            return s;
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
            var tracks = new JsonObject().With("audio", new JsonObject()
                .With("id", AudioTrackId(audioIndex))
                .With("index", audioIndex));
            if (!string.IsNullOrEmpty(subtitleTrackId))
                tracks.With("subtitle", new JsonObject().With("id", subtitleTrackId));
            await ReplanAsync(ReplanBody("track_change", playerSeconds, tracks));
        }

        JsonObject ReplanBody(string operation, double playerSeconds, JsonObject selectedTracks)
        {
            var keys = new JsonArray();
            foreach (var k in attemptedKeys.Skip(Math.Max(0, attemptedKeys.Count - 16))) keys.Add(JsonValue.CreateStringValue(k));
            return new JsonObject()
                .With("installation_id", installationId)
                .With("protocol_version", 3)
                .With("client_features", Json.Strings(PlaybackCapabilities.ClientFeatures))
                .With("operation", operation)
                .With("playback_attempt_id", attemptId)
                .With("replan_request_id", Guid.NewGuid().ToString("D"))
                .With("failed_plan_id", Plan.Str("plan_id"))
                .With("plan_attempt_id", planAttemptId)
                .With("plan_attempt_key", Plan.Str("plan_attempt_key"))
                .With("attempted_plan_keys", keys)
                .With("attempt_count", attemptCount)
                .With("quality_preference", "auto")
                .With("position_seconds", SourcePosition(playerSeconds))
                .With("metered", false)
                .With("selected_tracks", selectedTracks)
                .With("client_capabilities", PlaybackCapabilities.ClientCapabilities())
                .With("client_playback_context", PlaybackCapabilities.PlaybackContext());
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
