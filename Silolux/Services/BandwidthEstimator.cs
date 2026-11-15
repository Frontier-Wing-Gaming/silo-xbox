using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Connectivity;
using Windows.Web.Http;

namespace Silolux.Services
{
    /// <summary>
    /// The connection speed sent with a playback start as bandwidth_estimate_kbps. With it, the
    /// server's "auto" quality sends the original only when its bitrate fits 80% of the link and
    /// otherwise transcodes to fit; without it, a remote server streams the original at full bitrate
    /// and busy scenes outrun the connection.
    /// </summary>
    public static class BandwidthEstimator
    {
        const int MaxProbeBytes = 32 * 1024 * 1024;
        const int MinProbeBytes = 2 * 1024 * 1024;
        static readonly TimeSpan ProbeTime = TimeSpan.FromSeconds(4);
        static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

        static string cachedServer;
        static int cachedKbps;
        static DateTimeOffset cachedAt;

        /// <summary>Where the last estimate came from, for the playback log.</summary>
        public static string LastSource { get; private set; } = "none";

        /// <summary>
        /// Kbps to send, or 0 for none: on the local network (no limit worth planning for), or when
        /// nothing could be measured. The API asks clients to omit the estimate rather than guess.
        /// </summary>
        /// <param name="capabilities">GET /api/v2/playback/capabilities: whether the server plans with an
        /// estimate (buffering_quality_v1) and how it wants to be probed.</param>
        public static async Task<int> EstimateAsync(SiloClient api, JsonObject capabilities)
        {
            if (!Supported(capabilities)) { LastSource = "server doesn't plan by bandwidth"; return 0; }
            var server = api.BaseUri.AbsoluteUri;
            if (server == cachedServer && DateTimeOffset.UtcNow - cachedAt < KeepFor)
            {
                LastSource = "kept from " + cachedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                return cachedKbps;
            }

            JsonObject connection;
            try { connection = await api.GetAsync("/api/v2/playback/connection"); }
            catch (ApiException) { LastSource = "older server, no estimate"; return 0; } // an older server without bandwidth planning

            var kbps = 0;
            if (connection.Str("connection") != "local")
            {
                // A recent stored probe for this device beats a new one. Send its raw speed, not
                // auto_budget_kbps: the budget already has the server's 80% margin taken off, and the
                // server takes it again from whatever estimate a start sends.
                kbps = (int)(connection.Obj("probe")?.Num("kbps") ?? 0);
                LastSource = "server's stored probe";
                // A stored probe far above what playback from this server recently got (106 Mbps
                // stored, 1.3 Mbps arriving) is stale or from another network: measure again.
                var got = RecentlyObserved(server);
                var doubt = got > 0 && kbps > 2 * got;
                if ((kbps <= 0 || doubt) && capabilities.Bool("bandwidth_probe_automatic", true))
                {
                    var stored = kbps;
                    kbps = await ProbeAsync(api, capabilities);
                    LastSource = doubt
                        ? "new probe; the stored " + stored + " kbps was far above the " + got + " kbps playback got recently"
                        : "new probe";
                }
            }
            else
            {
                LastSource = "server calls the connection local, no estimate";
            }
            cachedServer = server;
            cachedKbps = kbps;
            cachedAt = DateTimeOffset.UtcNow;
            return kbps;
        }

        /// <summary>The server takes bandwidth fields only from clients declaring buffering_quality_v1, which it advertises.</summary>
        public static bool Supported(JsonObject capabilities)
        {
            var features = capabilities.Get("features");
            return features != null && features.ValueType == JsonValueType.Array &&
                   features.GetArray().Any(f => f.ValueType == JsonValueType.String && f.GetString() == Feature);
        }

        public const string Feature = "buffering_quality_v1";

        static async Task<int> ProbeAsync(SiloClient api, JsonObject capabilities)
        {
            // Enough bytes to prove the server's target rate within the probe time; finishing early
            // then shows the link carries at least that (a lower bound), which is all auto needs.
            var target = capabilities.Num("bandwidth_probe_target_kbps");
            var bytes = target > 0 ? (int)Math.Min(MaxProbeBytes, Math.Max(MinProbeBytes, target * 125 * ProbeTime.TotalSeconds)) : MaxProbeBytes;
            var sample = await api.ProbeBandwidthAsync(capabilities.Str("bandwidth_probe_url"), bytes, ProbeTime);
            if (sample == null) return 0;
            try
            {
                // Stored per device, so the server's budget and other clients' views agree with ours.
                await api.SendAsync(HttpMethod.Post, "/api/v2/playback/bandwidth-probe/results", new JsonObject()
                    .With("kbps", sample.Kbps)
                    .With("bytes", sample.Bytes)
                    .With("duration_ms", sample.DurationMs)
                    .With("lower_bound", sample.LowerBound)
                    .With("manual", false)
                    .With("transport", Transport()));
            }
            catch (ApiException) { }
            return sample.Kbps;
        }

        /// <summary>The link type, as the probe results endpoint names it: wifi, ethernet, cellular or other.</summary>
        public static string Transport()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return "other";
                if (profile.IsWlanConnectionProfile) return "wifi";
                if (profile.IsWwanConnectionProfile) return "cellular";
                // IANA ifType 6: Ethernet.
                if (profile.NetworkAdapter?.IanaInterfaceType == 6) return "ethernet";
            }
            catch (Exception) { }
            return "other";
        }

        /// <summary>Forget the cached reading, e.g. after playback kept stalling on it.</summary>
        public static void Forget() => cachedServer = null;

        static readonly TimeSpan ObservedFor = TimeSpan.FromHours(24);

        /// <summary>
        /// What a stream from this server actually received when it couldn't keep up: used for the
        /// next 10 minutes instead of a fresh reading, and for a day to doubt stored probes far above it.
        /// </summary>
        public static void Observed(SiloClient api, int kbps)
        {
            var server = api.BaseUri.AbsoluteUri;
            cachedServer = server;
            cachedKbps = kbps;
            cachedAt = DateTimeOffset.UtcNow;
            try
            {
                Windows.Storage.ApplicationData.Current.LocalSettings.Values[ObservedKey(server)] =
                    kbps + "|" + DateTimeOffset.UtcNow.UtcTicks;
            }
            catch (Exception) { }
        }

        static int RecentlyObserved(string server)
        {
            try
            {
                var parts = (Windows.Storage.ApplicationData.Current.LocalSettings.Values[ObservedKey(server)] as string)?.Split('|');
                if (parts == null || parts.Length != 2) return 0;
                var at = new DateTimeOffset(long.Parse(parts[1]), TimeSpan.Zero);
                return DateTimeOffset.UtcNow - at < ObservedFor ? int.Parse(parts[0]) : 0;
            }
            catch (Exception) { return 0; }
        }

        static string ObservedKey(string server) => "observed_kbps:" + server;
    }
}
