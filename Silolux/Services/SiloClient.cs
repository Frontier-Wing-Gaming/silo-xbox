using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace Silolux.Services
{
    public class ApiException : Exception
    {
        public int Status { get; }
        public string Code { get; }

        public ApiException(int status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }
    }

    /// <summary>
    /// Silo's native /api/v2 over Windows.Web.Http. Owns the access token,
    /// refresh (single flight), profile headers and URL resolution, so pages
    /// never handle credentials themselves.
    /// </summary>
    public class BandwidthSample
    {
        public long Bytes;
        public long DurationMs;
        public int Kbps;
        public bool LowerBound;
    }

    public class SiloClient
    {
        public const string Platform = "xbox";
        public const string ClientName = "Silolux for Xbox";
        public const string AppVersion = "0.2.1";

        public static SiloClient Current { get; private set; }

        public Uri BaseUri { get; }
        readonly HttpClient http;
        readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        string accessToken;

        SiloClient(Uri baseUri)
        {
            BaseUri = baseUri;
            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
            filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            http = new HttpClient(filter);
            http.DefaultRequestHeaders.UserAgent.TryParseAdd("Silolux/" + AppVersion);
        }

        public static SiloClient Use(string serverUrl)
        {
            Current = new SiloClient(Normalize(serverUrl));
            return Current;
        }

        public static Uri Normalize(string input)
        {
            var s = (input ?? "").Trim().TrimEnd('/');
            if (s.Length == 0) throw new ArgumentException("Enter your server's address.");
            if (!s.Contains("://")) s = "http://" + s;
            Uri uri;
            if (!Uri.TryCreate(s + "/", UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("That address is not valid. Use something like http://192.168.1.20:8090");
            return uri;
        }

        public bool SignedIn => !string.IsNullOrEmpty(AppSettings.RefreshToken);

        // ---- transport --------------------------------------------------------

        public Uri Resolve(string pathOrUrl)
        {
            if (string.IsNullOrEmpty(pathOrUrl)) return null;
            Uri u;
            return Uri.TryCreate(BaseUri, pathOrUrl, out u) ? u : null;
        }

        public bool SameOrigin(Uri u) =>
            u != null && Uri.Compare(u, BaseUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>
        /// A media URL for a player that cannot set headers. Server-relative
        /// paths get the account bearer as `token`, as the web player does;
        /// absolute URLs (distributed nodes, proxies) never carry it.
        /// </summary>
        public Uri MediaUrl(string pathOrUrl)
        {
            var absolute = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var u = Resolve(pathOrUrl);
            if (absolute || u == null || string.IsNullOrEmpty(accessToken)) return u;
            var sep = string.IsNullOrEmpty(u.Query) ? "?" : "&";
            return new Uri(u.AbsoluteUri + sep + "token=" + Uri.EscapeDataString(accessToken));
        }

        void AddDeviceHeaders(HttpRequestMessage req)
        {
            req.Headers["X-Silo-Device-Id"] = AppSettings.DeviceId;
            req.Headers["X-Silo-Device-Name"] = DeviceName;
            req.Headers["X-Silo-Device-Platform"] = Platform;
            req.Headers["X-Silo-Client"] = ClientName;
            req.Headers["X-Silo-Client-Version"] = AppVersion;
            req.Headers["X-Device-ID"] = AppSettings.DeviceId;
        }

        public static string DeviceName => "Xbox";

        async Task<JsonObject> RawAsync(HttpMethod method, string path, JsonObject body, bool authed, bool profiled)
        {
            var req = new HttpRequestMessage(method, Resolve(path));
            req.Headers["Accept"] = "application/json";
            AddDeviceHeaders(req);
            if (authed && !string.IsNullOrEmpty(accessToken))
                req.Headers["Authorization"] = "Bearer " + accessToken;
            if (profiled && !string.IsNullOrEmpty(AppSettings.ProfileId))
            {
                req.Headers["X-Profile-Id"] = AppSettings.ProfileId;
                if (!string.IsNullOrEmpty(AppSettings.ProfileToken))
                    req.Headers["X-Profile-Token"] = AppSettings.ProfileToken;
            }
            if (body != null)
                req.Content = new HttpStringContent(body.Stringify(), UnicodeEncoding.Utf8, "application/json");

            HttpResponseMessage res;
            try
            {
                res = await http.SendRequestAsync(req);
            }
            catch (Exception e)
            {
                // Windows.Web.Http failures carry an HRESULT whose text is often
                // "could not be found"; only the connection's own reason is useful.
                var reason = (e.Message ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .LastOrDefault(l => l.Length > 0 && !l.Contains("could not be found"));
                throw new ApiException(0, "network", "Could not reach the server." + (reason != null ? " " + reason + "." : ""));
            }
            var text = await res.Content.ReadAsStringAsync();
            JsonObject json = null;
            if (!string.IsNullOrWhiteSpace(text)) JsonObject.TryParse(text, out json);
            if (res.IsSuccessStatusCode) return json ?? new JsonObject();

            var status = (int)res.StatusCode;
            var code = json?.Str("code") ?? json?.Str("type") ?? "error";
            var detail = json?.Str("detail") ?? json?.Str("title") ?? res.ReasonPhrase;
            throw new ApiException(status, code, detail);
        }

        public async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject body = null, bool profiled = true)
        {
            if (string.IsNullOrEmpty(accessToken)) await RefreshAsync(null);
            var used = accessToken;
            try
            {
                return await RawAsync(method, path, body, true, profiled);
            }
            catch (ApiException e) when (e.Status == 401)
            {
                await RefreshAsync(used);
                return await RawAsync(method, path, body, true, profiled);
            }
        }

        public Task<JsonObject> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

        /// <summary>
        /// Times a download from /api/v2/playback/bandwidth-probe: up to <paramref name="maxBytes"/>
        /// or <paramref name="maxTime"/>, measured from the first byte so the server's response
        /// latency doesn't count as slowness. Null when the server refuses (429: probe limit) or fails.
        /// </summary>
        public async Task<BandwidthSample> ProbeBandwidthAsync(string probeUrl, int maxBytes, TimeSpan maxTime)
        {
            if (string.IsNullOrEmpty(accessToken)) await RefreshAsync(null);
            // The server's advertised URL takes the media route, like the video will; only send the
            // bearer to this server's own origin.
            var url = Resolve(string.IsNullOrEmpty(probeUrl) ? "/api/v2/playback/bandwidth-probe" : probeUrl);
            if (url == null || !SameOrigin(url)) url = Resolve("/api/v2/playback/bandwidth-probe");
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url.AbsoluteUri + (string.IsNullOrEmpty(url.Query) ? "?" : "&") + "size=" + maxBytes));
            AddDeviceHeaders(req);
            req.Headers["Authorization"] = "Bearer " + accessToken;
            if (!string.IsNullOrEmpty(AppSettings.ProfileId)) req.Headers["X-Profile-Id"] = AppSettings.ProfileId;
            try
            {
                using (var res = await http.SendRequestAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!res.IsSuccessStatusCode) return null;
                    using (var stream = await res.Content.ReadAsInputStreamAsync())
                    {
                        var buffer = new Windows.Storage.Streams.Buffer(256 * 1024);
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        long bytes = 0;
                        var first = true;
                        while (bytes < maxBytes && clock.Elapsed < maxTime)
                        {
                            var chunk = await stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial);
                            if (chunk.Length == 0) break;
                            if (first)
                            {
                                // Start timing once data flows; the first chunk only marks that point.
                                first = false;
                                clock.Restart();
                                continue;
                            }
                            bytes += chunk.Length;
                        }
                        var ms = Math.Max(1, (long)clock.Elapsed.TotalMilliseconds);
                        if (bytes < 256 * 1024) return null;
                        return new BandwidthSample
                        {
                            Bytes = bytes,
                            DurationMs = ms,
                            Kbps = (int)Math.Min(int.MaxValue, bytes * 8 / ms),
                            // Finished before the time ran out: the link can carry at least this much.
                            LowerBound = clock.Elapsed < maxTime,
                        };
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Rotates the refresh token. A caller holding a stale access token
        /// that lost the race reuses the winner's rotation.
        /// </summary>
        async Task RefreshAsync(string stale)
        {
            await refreshGate.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(accessToken) && accessToken != stale) return;
                var refresh = AppSettings.RefreshToken;
                if (string.IsNullOrEmpty(refresh)) throw new ApiException(401, "signed_out", "Sign in to continue.");
                JsonObject r;
                try
                {
                    r = await RawAsync(HttpMethod.Post, "/api/v2/auth/refresh",
                        new JsonObject().With("refresh_token", refresh), false, false);
                }
                catch (ApiException e) when (e.Status == 401)
                {
                    AppSettings.SignOut();
                    accessToken = null;
                    throw;
                }
                accessToken = r.Str("access_token");
                AppSettings.RefreshToken = r.Str("refresh_token");
            }
            finally
            {
                refreshGate.Release();
            }
        }

        void Adopt(JsonObject tokens)
        {
            accessToken = tokens.Str("access_token");
            AppSettings.RefreshToken = tokens.Str("refresh_token");
            AppSettings.AccountId = tokens.Path("user")?.Str("id");
        }

        // ---- discovery & auth ---------------------------------------------------

        public Task<JsonObject> SystemInfoAsync() => RawAsync(HttpMethod.Get, "/api/v2/system/info", null, false, false);

        public async Task LoginAsync(string username, string password)
        {
            var r = await RawAsync(HttpMethod.Post, "/api/v2/auth/login",
                new JsonObject().With("username", username).With("password", password), false, false);
            Adopt(r);
        }

        public Task<JsonObject> StartDeviceLoginAsync() =>
            RawAsync(HttpMethod.Post, "/api/v2/auth/device/start",
                new JsonObject().With("device_name", DeviceName).With("device_platform", Platform), false, false);

        /// <summary>Returns the poll answer; on approval the tokens are adopted.</summary>
        public async Task<JsonObject> PollDeviceLoginAsync(string deviceCode)
        {
            var r = await RawAsync(HttpMethod.Post, "/api/v2/auth/device/poll",
                new JsonObject().With("device_code", deviceCode), false, false);
            if (r.Str("status") == "approved" && r.Obj("tokens") != null)
            {
                Adopt(r.Obj("tokens"));
                var pid = r.Str("profile_id");
                if (!string.IsNullOrEmpty(pid))
                {
                    AppSettings.ProfileId = pid;
                    AppSettings.ProfileToken = r.Str("profile_token");
                }
            }
            return r;
        }

        public async Task SignOutAsync()
        {
            try
            {
                await RawAsync(HttpMethod.Post, "/api/v2/auth/logout", null, true, false);
            }
            catch (ApiException) { }
            accessToken = null;
            AppSettings.SignOut();
        }

        // ---- catalog ----------------------------------------------------------

        const string ImageQuery = "image_size=medium";

        public Task<JsonObject> ProfilesAsync() => SendAsync(HttpMethod.Get, "/api/v2/profiles", null, false);

        public async Task<bool> VerifyPinAsync(string profileId, string pin)
        {
            var r = await SendAsync(HttpMethod.Post, "/api/v2/profiles/" + Uri.EscapeDataString(profileId) + "/verify-pin",
                new JsonObject().With("pin", pin), false);
            if (!r.Bool("valid")) return false;
            AppSettings.ProfileToken = r.Str("profile_token");
            return true;
        }

        public Task<JsonObject> HomeSectionsAsync() =>
            SendAsync(HttpMethod.Get, "/api/v2/home/sections?item_limit=20&poster_width=300&backdrop_width=1280");

        public Task<JsonObject> UserLibrariesAsync() => GetAsync("/api/v2/user/libraries");

        public Task<JsonObject> ItemAsync(string id) =>
            GetAsync("/api/v2/catalog/items/" + Uri.EscapeDataString(id) + "?backdrop_width=1920&poster_width=400&logo_width=600");

        public Task<JsonObject> SeasonsAsync(string seriesId) =>
            GetAsync("/api/v2/catalog/series/" + Uri.EscapeDataString(seriesId) + "/seasons?" + ImageQuery);

        public Task<JsonObject> EpisodesAsync(string seriesId, int season) =>
            GetAsync("/api/v2/catalog/series/" + Uri.EscapeDataString(seriesId) + "/seasons/" + season + "/episodes?still_width=480");

        // ---- playback -----------------------------------------------------------

        public Task<JsonObject> PlaybackCapabilitiesAsync() => GetAsync("/api/v2/playback/capabilities");

        public Task<JsonObject> StartPlaybackAsync(JsonObject body) => SendAsync(HttpMethod.Post, "/api/v2/playback/start", body);

        public Task<JsonObject> ProgressAsync(string sessionId, JsonObject body) =>
            SendAsync(HttpMethod.Post, "/api/v2/playback/" + Uri.EscapeDataString(sessionId) + "/progress", body);

        public Task<JsonObject> StopAsync(string sessionId, JsonObject body) =>
            SendAsync(HttpMethod.Delete, "/api/v2/playback/" + Uri.EscapeDataString(sessionId), body);
    }
}
