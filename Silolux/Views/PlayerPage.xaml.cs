using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Silolux.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Data.Json;
using Windows.System;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    /// <summary>
    /// Plays one server-issued plan in MediaPlayer, reports sequenced
    /// progress, recovers from decode failures through a replan, and stops
    /// the session on the way out.
    /// </summary>
    public sealed partial class PlayerPage : Page
    {
        static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

        PlaybackSession session;
        MediaPlayer player;
        MediaPlaybackItem playbackItem;
        DispatcherTimer timer;
        DisplayRequest keepAwake;
        bool leaving;
        bool recovering;
        double lastPosition;
        readonly List<KeyValuePair<TimedTextSource, string>> sidecars = new List<KeyValuePair<TimedTextSource, string>>();
        TimedMetadataTrack selectedSubtitle;
        // Sidecar subtitle tracks by server track id, as Media Foundation resolves them.
        readonly Dictionary<string, TimedMetadataTrack> subtitleTracks = new Dictionary<string, TimedMetadataTrack>();
        string currentSubtitleId;
        // The open stream's MediaSource (sidecars attach to it on demand) and its sidecars by track id.
        MediaSource mediaSource;
        readonly Dictionary<string, SidecarSubtitle> sidecarInfo = new Dictionary<string, SidecarSubtitle>();
        // The track to turn on once its source resolves.
        string pendingSubtitleId;
        // How each sidecar was loaded, for the playback report.
        readonly List<string> subtitleNotes = new List<string>();
        PlayArgs args;
        JsonObject item;
        bool pickerOpen;
        bool hintShown;
        VisualStateGroup controlPanelStates;
        DisplayModeMatcher display;
        // Set when neither the plan nor the catalog named a frame rate: read it from the opened video.
        bool matchFromPlayer;

        public PlayerPage()
        {
            InitializeComponent();
            // Menu / View on a controller, T on a keyboard: audio and subtitles. Registered with
            // handledEventsToo because the transport controls handle most keys themselves.
            AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            PreviewKeyDown += OnPreviewKeyDown;
            Video.TransportControls.Loaded += (s, e) => HookControlPanel();
            Loaded += (s, e) => FocusSink.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Follows the transport controls' fade in and out through their template's visual states;
        /// MediaTransportControls has no event for it.
        /// </summary>
        void HookControlPanel()
        {
            if (controlPanelStates != null) return;
            var controls = Video.TransportControls;
            var root = VisualTreeHelper.GetChildrenCount(controls) > 0 ? VisualTreeHelper.GetChild(controls, 0) as FrameworkElement : null;
            if (root == null) return;
            controlPanelStates = VisualStateManager.GetVisualStateGroups(root).FirstOrDefault(g => g.Name == "ControlPanelVisibilityStates");
            if (controlPanelStates != null) controlPanelStates.CurrentStateChanged += ControlPanelStateChanged;
        }

        void ControlPanelStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            if (leaving) return;
            if (e.NewState?.Name == "ControlPanelFadeOut")
            {
                // No focus highlight or mouse pointer over the picture while the controls are away.
                // (A PC mouse only; the console's pointer stays off, see App.)
                if (!Ui.Tv) Window.Current.CoreWindow.PointerCursor = null;
                if (!pickerOpen) FocusSink.Focus(FocusState.Programmatic);
                if (seekTarget == null) HideSeekStripLater();
            }
            else if (e.NewState?.Name == "ControlPanelFadeIn")
            {
                RestorePointer();
                // With the player's own seek bar on screen the strip would repeat it.
                if (ServerSeeking) ShowSeekStrip(true);
                if (FocusManager.GetFocusedElement() == FocusSink) FocusControls();
            }
        }

        /// <summary>Keys while the controls are hidden (focus on FocusSink): A / Space / Enter play or pause, others bring the controls back.</summary>
        void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (FocusManager.GetFocusedElement() != FocusSink) return;
            var skip = SkipFor(e.Key, true);
            if (skip != 0 && CanSkip)
            {
                e.Handled = true;
                Skip(skip);
                return;
            }
            switch (e.Key)
            {
                case VirtualKey.GamepadA:
                case VirtualKey.Space:
                case VirtualKey.Enter:
                    e.Handled = true;
                    if (!e.KeyStatus.WasKeyDown) TogglePause();
                    break;
                // The track picker and Back keep their usual handling.
                case VirtualKey.GamepadMenu:
                case VirtualKey.GamepadView:
                case VirtualKey.T:
                case VirtualKey.GamepadB:
                case VirtualKey.Escape:
                case VirtualKey.Back:
                case VirtualKey.GoBack:
                case VirtualKey.Menu:
                    break;
                default:
                    e.Handled = true;
                    Video.TransportControls.Show();
                    // Already showing (e.g. paused): the fade-in won't fire, so move focus here.
                    if (controlPanelStates?.CurrentState?.Name != "ControlPanelFadeOut") FocusControls();
                    break;
            }
        }

        void TogglePause()
        {
            if (player == null || recovering) return;
            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
            else player.Play();
        }

        /// <summary>Puts focus on the play / pause button, where the controls normally start.</summary>
        void FocusControls()
        {
            var target = FindNamed(Video.TransportControls, "PlayPauseButton") as Control;
            if (target == null || target.Visibility != Visibility.Visible)
                target = FindNamed(Video.TransportControls, "PlayPauseButtonOnLeft") as Control;
            if (target == null || !target.Focus(FocusState.Keyboard))
                Video.TransportControls.Focus(FocusState.Keyboard);
        }

        static DependencyObject FindNamed(DependencyObject parent, string name)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if ((child as FrameworkElement)?.Name == name) return child;
                var found = FindNamed(child, name);
                if (found != null) return found;
            }
            return null;
        }

        static void RestorePointer()
        {
            if (!Ui.Tv && Window.Current.CoreWindow.PointerCursor == null)
                Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        }

        /// <summary>Seconds a key skips: LB / RB anywhere, the D-pad and arrows only while the controls are hidden.</summary>
        static double SkipFor(VirtualKey key, bool controlsHidden)
        {
            switch (key)
            {
                case VirtualKey.GamepadLeftShoulder: return -SkipBackSeconds;
                case VirtualKey.GamepadRightShoulder: return SkipForwardSeconds;
                case VirtualKey.Left:
                case VirtualKey.GamepadDPadLeft:
                case VirtualKey.GamepadLeftThumbstickLeft:
                    return controlsHidden ? -SkipBackSeconds : 0;
                case VirtualKey.Right:
                case VirtualKey.GamepadDPadRight:
                case VirtualKey.GamepadLeftThumbstickRight:
                    return controlsHidden ? SkipForwardSeconds : 0;
                default: return 0;
            }
        }

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // LB / RB while the controls show (with them hidden, OnPreviewKeyDown has it).
            if (!pickerOpen && CanSkip && FocusManager.GetFocusedElement() != FocusSink)
            {
                var skip = SkipFor(e.Key, false);
                if (skip != 0)
                {
                    e.Handled = true;
                    Skip(skip);
                    return;
                }
            }
            if (e.Key == VirtualKey.GamepadMenu || e.Key == VirtualKey.GamepadView || e.Key == VirtualKey.T)
            {
                e.Handled = true;
                ShowTrackPicker();
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            args = (PlayArgs)e.Parameter;
            Status.Text = args.Title ?? "";
            keepAwake = new DisplayRequest();
            keepAwake.RequestActive();
            try
            {
                session = await PlaybackSession.StartAsync(args.FileId, args.StartPosition, args.PinFile);
                // Before the video loads, so the TV's resync blank falls on the loading screen.
                await MatchDisplayAsync();
                if (leaving)
                {
                    // Backed out while the TV was switching: OnNavigatedFrom's restore may have run first.
                    if (display != null) await display.RestoreAsync();
                    return;
                }
                player = new MediaPlayer { AutoPlay = true };
                player.MediaOpened += Player_MediaOpened;
                player.MediaFailed += Player_MediaFailed;
                player.MediaEnded += Player_MediaEnded;
                player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
                Video.SetMediaPlayer(player);
                await LoadPlanAsync();
                timer = new DispatcherTimer { Interval = ProgressEvery };
                timer.Tick += async (s, _) => await ReportAsync();
                timer.Start();
                paceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                paceTimer.Tick += (s, _) => CheckPace();
                paceTimer.Start();
            }
            catch (ApiException ex)
            {
                Fail(ex.Message);
            }
        }

        /// <summary>Loads the session's current plan at its player start.</summary>
        async Task LoadPlanAsync()
        {
            MediaSource source;
            if (session.IsHls)
            {
                var result = await AdaptiveMediaSource.CreateFromUriAsync(session.StreamUrl);
                if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                    throw new ApiException(0, "hls", "The stream could not be opened (" + result.Status + ").");
                source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
            }
            else
            {
                source = MediaSource.CreateFromUri(session.StreamUrl);
            }

            sidecars.Clear();
            subtitleNotes.Clear();
            subtitleTracks.Clear();
            selectedSubtitle = null;
            var selectedId = session.SelectedSubtitleId;
            currentSubtitleId = selectedId;
            // Only the chosen track loads with the stream. An embedded track costs the server a read
            // through the whole film; attaching all of them (16 on some films) made it read the film
            // over and over while streaming it, and the picture and subtitles lagged.
            mediaSource = source;
            sidecarInfo.Clear();
            // Already in the picture: its sidecar on top would show every line twice.
            var burnedIn = selectedId != null && session.SubtitleBurnedIn;
            if (burnedIn) subtitleNotes.Add("burned into the picture by the server");
            pendingSubtitleId = burnedIn ? null : selectedId;
            foreach (var sub in session.SidecarSubtitles())
            {
                if (sub.TrackId != null) sidecarInfo[sub.TrackId] = sub;
                if (!burnedIn && sub.TrackId != null && sub.TrackId == selectedId) await AttachSubtitleAsync(sub);
            }

            // No start time on the item: MediaPlaybackItem(source, start) trims the item, and its clock,
            // length and subtitle cues then count from the trim. StartAtPlanPosition's seek to the plan
            // start on top of that put the picture at twice the resume point (a resume at 6:38 showed
            // 13:16) while the clock and subtitles read 6:38.
            playbackItem = new MediaPlaybackItem(source);
            startPending = true;
            playbackItem.TimedMetadataTracksChanged += (s, a) => ApplySubtitleSelection();
            playbackItem.AudioTracksChanged += (s, a) => ApplyAudioSelection();
            ApplySeekMode();
            player.Source = playbackItem;
        }

        /// <summary>Fetches one sidecar and adds it to the open stream; it turns on if it's the pending choice.</summary>
        async Task AttachSubtitleAsync(SidecarSubtitle sub)
        {
            var source = mediaSource;
            var tts = await SubtitleSourceAsync(sub);
            if (source == null || !ReferenceEquals(source, mediaSource)) return; // the stream changed meanwhile
            var label = sub.Label;
            var trackId = sub.TrackId;
            tts.Resolved += (s, a) =>
            {
                foreach (var t in a.Tracks)
                {
                    t.Label = label;
                    if (trackId != null) subtitleTracks[trackId] = t;
                    if (trackId != null && trackId == pendingSubtitleId) selectedSubtitle = t;
                }
                // The track may already be in the item's list by now; turn it on either way.
                var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplySubtitleSelection);
            };
            source.ExternalTimedTextSources.Add(tts);
            sidecars.Add(new KeyValuePair<TimedTextSource, string>(tts, label));
        }

        /// <summary>
        /// The sidecar as a timed-text source. The server times subtitle files from the film's start;
        /// for a stream started mid-film the player's clock starts at the stream's origin, so the
        /// track is fetched here and moved earlier by that offset (see PlaybackSession.ShiftCues).
        /// </summary>
        async Task<TimedTextSource> SubtitleSourceAsync(SidecarSubtitle sub)
        {
            var offset = session.TimelineOffset;
            var name = sub.Label + " (" + (sub.Language ?? "?") + ")";
            var started = DateTimeOffset.UtcNow;
            try
            {
                using (var http = new Windows.Web.Http.HttpClient())
                {
                    var buffer = await http.GetBufferAsync(sub.Url);
                    var bytes = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
                    var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
                    PlaybackLog.Write("subtitle " + name + ": " + PlaybackLog.Describe(sub.Url, text));
                    var shifted = System.Text.Encoding.UTF8.GetBytes(Math.Abs(offset) < 0.05 ? text : PlaybackSession.ShiftCues(text, offset));
                    var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                    {
                        writer.WriteBytes(shifted);
                        await writer.StoreAsync();
                        writer.DetachStream();
                    }
                    stream.Seek(0);
                    subtitleNotes.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}: {1} bytes in {2:0.0} s{3}", name, shifted.Length, (DateTimeOffset.UtcNow - started).TotalSeconds,
                        Math.Abs(offset) < 0.05 ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture, ", shifted {0:0.###} s earlier", offset)));
                    return TimedTextSource.CreateFromStream(stream, sub.Language);
                }
            }
            catch (Exception ex)
            {
                subtitleNotes.Add(name + ": shift failed (" + ex.Message.Trim() + "), loaded unshifted");
                // Late subtitles beat none.
                return TimedTextSource.CreateFromUri(sub.Url, sub.Language);
            }
        }

        void ApplySubtitleSelection()
        {
            if (playbackItem == null || selectedSubtitle == null) return;
            var tracks = playbackItem.TimedMetadataTracks;
            for (var i = 0; i < tracks.Count; i++)
                tracks.SetPresentationMode((uint)i, ReferenceEquals(tracks[i], selectedSubtitle)
                    ? TimedMetadataTrackPresentationMode.PlatformPresented
                    : TimedMetadataTrackPresentationMode.Disabled);
        }

        void ApplyAudioSelection()
        {
            // Only an untouched original carries every audio track; remuxes
            // and transcodes already contain just the selected one.
            var index = session?.SelectedAudioIndex;
            if (playbackItem == null || !index.HasValue || session.Delivery != "original_http") return;
            if (index.Value < playbackItem.AudioTracks.Count && playbackItem.AudioTracks.SelectedIndex != index.Value)
                playbackItem.AudioTracks.SelectedIndex = index.Value;
        }

        async void Player_MediaOpened(MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (matchFromPlayer) await MatchDisplayFromPlayerAsync();
                StartAtPlanPosition();
                // The first seconds of a new stream fill its buffer; judge the pace after them.
                ResetPace(TimeSpan.FromSeconds(5));
                Overlay.Visibility = Visibility.Collapsed;
                Busy.IsActive = false;
                recovering = false;
                ShowHintOnce();
                // A few seconds in, once the TV has settled into whatever mode playback gets.
                await Task.Delay(6000);
                if (!leaving) await WritePlaybackReportAsync();
            });
        }

        /// <summary>
        /// LocalState\playback.txt: what the server sent, what the player decodes and what the TV is
        /// showing, mid-playback. Diagnostics can't see this itself (opening it leaves the player).
        /// </summary>
        static Task pendingStop;

        /// <summary>Waits (up to <paramref name="limit"/>) for the last player's stop report to reach the server.</summary>
        public static async Task WaitForStopAsync(TimeSpan limit)
        {
            var stop = pendingStop;
            if (stop == null || stop.IsCompleted) return;
            try { await Task.WhenAny(stop, Task.Delay(limit)); }
            catch (Exception) { }
        }

        static string CueText(IMediaCue cue)
        {
            var t = cue as TimedTextCue;
            if (t == null) return "";
            var text = string.Join(" / ", t.Lines.Select(l => l.Text));
            return text.Length > 80 ? text.Substring(0, 80) + "…" : text;
        }

        async Task WritePlaybackReportAsync()
        {
            try
            {
                var version = await PlayingVersionAsync(); // also loads item, for the title
                var b = new System.Text.StringBuilder();
                b.AppendLine("Last playback, " + DateTimeOffset.Now.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
                b.AppendLine("  Title: " + (item?.Str("title") ?? item?.Str("name") ?? args?.ContentId ?? "?"));
                b.AppendLine("  Delivery: " + session.Delivery + (session.ActiveQuality != null ? " · quality " + session.ActiveQuality : ""));
                var recipe = session.Plan.Path("effective_recipe");
                if (recipe != null)
                    foreach (var kv in recipe)
                        if (kv.Value.ValueType != JsonValueType.Object && kv.Value.ValueType != JsonValueType.Array && kv.Value.ValueType != JsonValueType.Null)
                            b.AppendLine("  Sent " + kv.Key + ": " + kv.Value.Stringify().Trim('"'));
                var source = version?.Objects("video_tracks").FirstOrDefault();
                if (source != null)
                    b.AppendLine("  Source video: " + source.Str("codec") + " " + source.Num("width") + "×" + source.Num("height") +
                                 " @ " + source.Str("frame_rate") + " fps, " + (source.Str("video_range_type") ?? "SDR"));
                var props = playbackItem?.VideoTracks.FirstOrDefault()?.GetEncodingProperties();
                // NaturalVideoWidth/Height: the frames actually decoded (HLS track properties hold a placeholder size).
                var ps = player?.PlaybackSession;
                if (props != null)
                    b.AppendLine("  Decoding: " + props.Subtype + " " +
                                 (ps != null && ps.NaturalVideoWidth > 0 ? ps.NaturalVideoWidth + "×" + ps.NaturalVideoHeight : props.Width + "×" + props.Height) +
                                 (props.FrameRate.Denominator != 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, " @ {0:0.###} fps",
                                     (double)props.FrameRate.Numerator / props.FrameRate.Denominator) : ""));
                var tl = session.Plan.Path("timeline");
                if (tl != null)
                    b.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "  Timeline: origin {0:0.###} s, offset {1:0.###} s, player start {2:0.###} s, seek anywhere {3}",
                        tl.Num("stream_origin_seconds"), tl.Num("timeline_offset_seconds"), tl.Num("player_start_seconds"),
                        tl.Bool("can_seek_anywhere", true) ? "yes" : "no"));
                foreach (var note in subtitleNotes) b.AppendLine("  Subtitle " + note);
                if (subtitleNotes.Count == 0) b.AppendLine("  Subtitles: none attached");
                if (ps != null)
                {
                    var pos = ps.Position.TotalSeconds;
                    b.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "  Position: player {0:0.###} s, film {1:0.###} s", pos, session.SourcePosition(pos)));
                }
                var showing = selectedSubtitle;
                if (showing != null)
                {
                    b.AppendLine("  Subtitle showing: " + showing.Label + " (" + showing.Language + ")");
                    foreach (var cue in showing.ActiveCues.Take(2))
                        b.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "    on screen now: {0:0.###}-{1:0.###} s player time  \"{2}\"", cue.StartTime.TotalSeconds,
                            (cue.StartTime + cue.Duration).TotalSeconds, CueText(cue)));
                }
                b.AppendLine("  Display match: " + (display?.LastDecision ?? "not on this device"));
                try
                {
                    var mode = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView()?.GetCurrentDisplayMode();
                    if (mode != null) b.AppendLine("  TV mode now: " + DisplayModeMatcher.Describe(mode));
                }
                catch (Exception) { }
                try
                {
                    var color = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().GetAdvancedColorInfo();
                    b.AppendLine("  TV colour now: " + color.CurrentAdvancedColorKind);
                }
                catch (Exception) { }
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync("playback.txt",
                    Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file, b.ToString());
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Sets the TV's refresh rate (and HDR) for this video on Xbox; see DisplayModeMatcher. The frame
        /// rate comes from what the server actually sends (the plan's recipe, which reflects a transcode),
        /// else the catalog's description of the file, else the player once the video has opened.
        /// </summary>
        async Task MatchDisplayAsync()
        {
            display = DisplayModeMatcher.Create();
            if (display == null) return;

            var recipe = session.Plan.Path("effective_recipe");
            var rate = recipe?.Num("frame_rate") ?? 0;
            var range = recipe?.Str("dynamic_range");
            if (rate <= 0 || string.IsNullOrEmpty(range))
            {
                var version = await PlayingVersionAsync();
                var video = version?.Objects("video_tracks").FirstOrDefault();
                if (rate <= 0) rate = ParseRate(video?.Str("frame_rate"));
                if (string.IsNullOrEmpty(range))
                    range = video?.Str("video_range_type") ?? (version != null && version.Bool("hdr") ? "hdr10" : null);
            }

            if (rate <= 0)
            {
                matchFromPlayer = true;
                return;
            }
            await display.MatchAsync(rate, IsHdr(range));
        }

        /// <summary>The fallback when nothing described the video: pause, switch, resume.</summary>
        async Task MatchDisplayFromPlayerAsync()
        {
            matchFromPlayer = false;
            var props = playbackItem?.VideoTracks.FirstOrDefault()?.GetEncodingProperties();
            if (props == null || props.FrameRate.Denominator == 0 || props.FrameRate.Numerator == 0) return;
            var rate = (double)props.FrameRate.Numerator / props.FrameRate.Denominator;
            var range = session.Plan.Path("effective_recipe")?.Str("dynamic_range");
            player.Pause();
            await display.MatchAsync(rate, IsHdr(range));
            if (!leaving) player.Play();
        }

        static double ParseRate(string text)
        {
            double rate;
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rate) ? rate : 0;
        }

        /// <summary>HDR10 / HDR10+ / Dolby Vision are sent as PQ; SDR and HLG are not.</summary>
        static bool IsHdr(string range)
        {
            if (string.IsNullOrEmpty(range)) return false;
            range = range.ToLowerInvariant();
            return !range.Contains("sdr") && !range.Contains("hlg")
                && (range.Contains("hdr") || range.Contains("pq") || range.Contains("2084") || range.Contains("dolby") || range.Contains("dovi") || range.Contains("dv"));
        }

        /// <summary>The catalog's entry for the file being played (the plan describes only the stream).</summary>
        async Task<JsonObject> PlayingVersionAsync()
        {
            if (item == null && !string.IsNullOrEmpty(args?.ContentId))
            {
                try { item = await SiloClient.Current.ItemAsync(args.ContentId); }
                catch (ApiException) { }
            }
            var versions = item?.Objects("versions").ToList() ?? new List<JsonObject>();
            return versions.FirstOrDefault(v => v.Str("file_id") == session.EffectiveFileId) ?? versions.FirstOrDefault();
        }

        async void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (leaving || recovering || session == null) return;
                recovering = true;
                Overlay.Visibility = Visibility.Visible;
                Busy.IsActive = true;
                Status.Text = "Switching to a compatible stream…";
                var classification = args.Error == MediaPlayerError.NetworkError ? "network_error" : "decoder_error";
                try
                {
                    if (await session.RecoverAsync(classification, args.ErrorMessage, CurrentPosition()))
                    {
                        await LoadPlanAsync();
                        return;
                    }
                    Fail("This title can't play on this console. " + args.ErrorMessage);
                }
                catch (ApiException ex)
                {
                    Fail(ex.Message);
                }
            });
        }

        /// <summary>
        /// Every stream opens at its beginning (the item has no start time; see LoadPlanAsync), so
        /// seek to the plan's start once it's open.
        /// </summary>
        void StartAtPlanPosition()
        {
            if (session == null || player == null) return;
            var start = session.PlayerStartSeconds;
            try
            {
                var ps = player.PlaybackSession;
                if (start > 1 && ps.CanSeek && Math.Abs(ps.Position.TotalSeconds - start) > 1)
                    ps.Position = TimeSpan.FromSeconds(start);
            }
            catch (Exception) { }
            finally { startPending = false; }
        }

        // Between building the item and the start seek, the player's clock reads the stream's
        // beginning; a progress report then would move the resume point there.
        bool startPending;

        // Skips: D-pad left / right with the controls hidden, LB / RB any time; SeekStrip shows the
        // film's time. Most streams skip in the player. A stream the server started mid-film (a remux;
        // timeline can_seek_anywhere false) has no length, so Media Foundation's seek bar can't show or
        // reach the film: there skips go to the server as seek_reanchor replans, and the strip replaces
        // the player's seek bar.
        const double SkipBackSeconds = 10;
        const double SkipForwardSeconds = 30;
        double? seekTarget;
        int seekToken;
        int stripToken;
        DispatcherTimer clockTimer;

        bool ServerSeeking => session != null && !session.CanSeekAnywhere;

        bool CanSkip
        {
            get
            {
                if (session == null || player == null) return false;
                if (ServerSeeking) return true;
                try { return player.PlaybackSession.CanSeek; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Hides the player's own seek controls for a stream they can't handle.</summary>
        void ApplySeekMode()
        {
            var own = ServerSeeking;
            var c = Video.TransportControls;
            c.IsSeekBarVisible = !own;
            c.IsSeekEnabled = !own;
            c.IsSkipForwardButtonVisible = !own;
            c.IsSkipBackwardButtonVisible = !own;
            c.IsFastForwardButtonVisible = !own;
            c.IsFastRewindButtonVisible = !own;
            SeekHelp.Text = Ui.Tv ? "\u25C0 \u25B6 or LB RB to skip" : "\u2190 \u2192 to skip";
            if (!own) SeekStrip.Visibility = Visibility.Collapsed;
        }

        void ShowSeekStrip(bool stay)
        {
            if (session == null || leaving) return;
            SeekStrip.Visibility = Visibility.Visible;
            UpdateClock();
            if (clockTimer == null)
            {
                clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                clockTimer.Tick += (s, e) => { if (SeekStrip.Visibility == Visibility.Visible) UpdateClock(); };
            }
            clockTimer.Start();
            if (!stay) HideSeekStripLater();
        }

        async void HideSeekStripLater()
        {
            var token = ++stripToken;
            await Task.Delay(3000);
            if (token != stripToken || seekTarget != null) return;
            // The controls keep it up while they show.
            if (controlPanelStates?.CurrentState?.Name == "ControlPanelFadeIn") return;
            SeekStrip.Visibility = Visibility.Collapsed;
            clockTimer?.Stop();
        }

        void UpdateClock()
        {
            if (session == null) return;
            var now = seekTarget ?? session.SourcePosition(CurrentPosition());
            var length = Length();
            SeekClock.Text = (seekTarget != null ? "\u2192 " : "") + Clock(now) + (length > 0 ? " / " + Clock(length) : "");
            SeekProgress.Value = length > 0 ? Math.Min(1, now / length) : 0;
        }

        /// <summary>The film's length: the plan's source duration, else what the player reports.</summary>
        double Length()
        {
            var length = session?.SourceDuration ?? 0;
            if (length > 0 || player == null) return length;
            try
            {
                var d = player.PlaybackSession.NaturalDuration.TotalSeconds;
                return d > 0 && d < 1e6 ? d : 0;
            }
            catch (Exception) { return 0; }
        }

        static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        /// <summary>
        /// Moves the pending target by <paramref name="delta"/> seconds; once the presses stop, one
        /// seek_reanchor replan loads a stream starting there.
        /// </summary>
        async void Skip(double delta)
        {
            if (!CanSkip || recovering || leaving) return;
            var length = Length();
            if (!ServerSeeking)
            {
                // The player holds the whole film: jump straight there.
                try
                {
                    var to = session.SourcePosition(CurrentPosition()) + delta;
                    if (length > 0) to = Math.Min(to, Math.Max(0, length - 5));
                    to = Math.Max(0, to);
                    player.PlaybackSession.Position = TimeSpan.FromSeconds(session.PlayerPosition(to));
                    lastPosition = session.PlayerPosition(to);
                }
                catch (Exception) { }
                ShowSeekStrip(false);
                return;
            }
            var target = (seekTarget ?? session.SourcePosition(CurrentPosition())) + delta;
            if (length > 0) target = Math.Min(target, Math.Max(0, length - 5));
            seekTarget = Math.Max(0, target);
            ShowSeekStrip(true);
            var token = ++seekToken;
            await Task.Delay(900);
            if (token != seekToken || seekTarget == null || leaving) return;
            var at = seekTarget.Value;
            seekTarget = null;
            await ReloadAsync(_ => session.ReanchorAsync(at), "Seeking to " + Clock(at) + "\u2026");
            HideSeekStripLater();
        }

        async void Player_MediaEnded(MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (Frame.CanGoBack && !leaving) Frame.GoBack();
            });
        }

        DateTimeOffset? bufferingSince;

        async void PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            var state = sender.PlaybackState;
            // Every wait for data, how long it lasted and how much was downloaded, in playback-log.txt.
            try
            {
                if (state == MediaPlaybackState.Buffering && bufferingSince == null)
                {
                    bufferingSince = DateTimeOffset.UtcNow;
                }
                else if (state != MediaPlaybackState.Buffering && bufferingSince is DateTimeOffset since)
                {
                    bufferingSince = null;
                    PlaybackLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "buffered {0:0.0} s at {1} (downloaded {2:0}% of the stream)",
                        (DateTimeOffset.UtcNow - since).TotalSeconds, PlaybackLog.Secs(sender.Position.TotalSeconds),
                        sender.DownloadProgress * 100));
                }
            }
            catch (Exception) { }
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowStall(state == MediaPlaybackState.Buffering));
            // Report pause and resume right away rather than at the next tick.
            if (state == MediaPlaybackState.Paused || state == MediaPlaybackState.Playing)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () => await ReportAsync());
        }

        readonly List<DateTimeOffset> stalls = new List<DateTimeOffset>();
        bool stallHintShown;
        int stallToken;

        /// <summary>
        /// Shows the buffering ring for stalls after the video has started (the loading overlay covers
        /// the first one). After three stalls in two minutes, points at the quality setting once.
        /// </summary>
        async void ShowStall(bool buffering)
        {
            var token = ++stallToken;
            if (!buffering || leaving || recovering || Overlay.Visibility == Visibility.Visible)
            {
                Stall.IsActive = false;
                return;
            }
            // Brief hiccups (a seek landing) don't need a ring.
            await Task.Delay(400);
            if (token != stallToken) return;
            Stall.IsActive = true;

            var now = DateTimeOffset.UtcNow;
            stalls.Add(now);
            stalls.RemoveAll(t => now - t > TimeSpan.FromMinutes(2));
            if (stalls.Count >= 3 && session != null)
            {
                stalls.Clear();
                // Three stalls in two minutes: the stream evidently doesn't fit, whatever the pace says.
                var pace = Pace(TimeSpan.FromSeconds(8));
                TryStepDown(Math.Min(pace ?? 0.8, 0.8), "3 stalls in 2 minutes");
            }
        }

        // ---- Pace: a stream the connection can't carry doesn't always stall. Media Foundation can
        // stay "playing" and creep along as data trickles in (1 s of film per 10 s on a 12 Mbps
        // original over a ~1 Mbps link), so compare the film's advance with the clock as well.
        DispatcherTimer paceTimer;
        readonly List<KeyValuePair<DateTimeOffset, double>> paceSamples = new List<KeyValuePair<DateTimeOffset, double>>();
        DateTimeOffset paceFrom;
        int stepDowns;
        bool steppingDown;
        const int MaxStepDowns = 3;
        const double SlowPace = 0.7;
        static readonly TimeSpan PaceWindow = TimeSpan.FromSeconds(20);

        void ResetPace(TimeSpan wait)
        {
            paceSamples.Clear();
            paceFrom = DateTimeOffset.UtcNow + wait;
        }

        void CheckPace()
        {
            if (session == null || player == null || leaving || recovering || steppingDown || Overlay.Visibility == Visibility.Visible)
            {
                paceSamples.Clear();
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (now < paceFrom) return;
            MediaPlaybackState state;
            double position;
            try
            {
                state = player.PlaybackSession.PlaybackState;
                position = player.PlaybackSession.Position.TotalSeconds;
            }
            catch (Exception) { return; }
            // Paused or opening: nothing to judge. Buffering counts: the clock runs, the film doesn't.
            if (state != MediaPlaybackState.Playing && state != MediaPlaybackState.Buffering)
            {
                paceSamples.Clear();
                return;
            }
            if (paceSamples.Count > 0)
            {
                var last = paceSamples[paceSamples.Count - 1];
                var moved = position - last.Value;
                // A seek, not playback.
                if (moved < -1 || moved > (now - last.Key).TotalSeconds + 3) paceSamples.Clear();
            }
            paceSamples.Add(new KeyValuePair<DateTimeOffset, double>(now, position));
            paceSamples.RemoveAll(p => now - p.Key > PaceWindow + TimeSpan.FromSeconds(4));

            var pace = Pace(PaceWindow);
            if (pace.HasValue && pace.Value < SlowPace)
            {
                paceSamples.Clear();
                TryStepDown(pace.Value, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "the film advanced {0:0}% as fast as the clock over {1:0} s", pace.Value * 100, PaceWindow.TotalSeconds));
            }
        }

        /// <summary>Film seconds per clock second over at least <paramref name="span"/>, or null without enough samples.</summary>
        double? Pace(TimeSpan span)
        {
            if (paceSamples.Count < 2) return null;
            var newest = paceSamples[paceSamples.Count - 1];
            var oldest = paceSamples[0];
            var seconds = (newest.Key - oldest.Key).TotalSeconds;
            if (seconds < span.TotalSeconds) return null;
            return Math.Max(0, newest.Value - oldest.Value) / seconds;
        }

        /// <summary>
        /// Auto quality on a stream that can't keep up: replan with what it actually received. A
        /// quality the viewer picked stays theirs (they get the hint); so does a title with nothing lower.
        /// </summary>
        async void TryStepDown(double pace, string why)
        {
            if (steppingDown || recovering || leaving || session == null) return;
            var plan = session.PlanKbps;
            var received = (int)(plan * Math.Max(0.05, pace));
            string cant = null;
            if (session.QualityPreference != "auto") cant = "quality " + session.QualityPreference + " was picked by hand";
            else if (!session.ReportsRebuffering) cant = "the server doesn't take observed throughput";
            else if (plan <= 0) cant = "the plan doesn't give its bitrate";
            else if (stepDowns >= MaxStepDowns) cant = "already stepped down " + stepDowns + " times";
            else if (!session.AvailableQualities().Any(q => q.Kbps > 0 && q.Kbps < plan)) cant = "nothing lower is offered";
            PlaybackLog.Write("slow playback (" + why + "; about " + received + " of " + plan + " kbps arriving) -> " +
                              (cant == null ? "stepping down" : "staying: " + cant));
            if (cant != null)
            {
                if (!stallHintShown && session.AvailableQualities().Any())
                {
                    stallHintShown = true;
                    // The cached speed was too optimistic: measure again for the next title.
                    BandwidthEstimator.Forget();
                    ShowHint((Ui.Tv ? "☰" : "T") + "  Buffering? Choose a lower quality", 8000);
                }
                return;
            }
            steppingDown = true;
            stepDowns++;
            try
            {
                await ReloadAsync(p => session.StepDownAsync(received, session.SelectedAudioIndex ?? 0, currentSubtitleId, p),
                    "Your connection can't keep up with this stream.\nSwitching to a lower quality\u2026", keepPlayingOnFailure: true);
            }
            finally
            {
                steppingDown = false;
            }
        }

        double CurrentPosition()
        {
            try
            {
                if (startPending && session != null) return session.PlayerStartSeconds;
                var p = player?.PlaybackSession.Position.TotalSeconds ?? lastPosition;
                if (p > 0) lastPosition = p;
            }
            catch (Exception) { }
            return lastPosition;
        }

        async Task ReportAsync()
        {
            if (session == null || leaving || recovering) return;
            var paused = player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
            await session.ReportProgressAsync(CurrentPosition(), paused);
            if (session.EndedByAdministrator)
            {
                player.Pause();
                Fail("An administrator ended this playback session.");
            }
        }

        void ShowHintOnce()
        {
            if (hintShown) return;
            hintShown = true;
            ShowHint((Ui.Tv ? "\u2630" : "T") + "  Audio, subtitles & quality", 5000);
        }

        int hintToken;

        async void ShowHint(string text, int milliseconds)
        {
            var token = ++hintToken;
            TracksHintText.Text = text;
            TracksHint.Visibility = Visibility.Visible;
            await Task.Delay(milliseconds);
            if (token == hintToken) TracksHint.Visibility = Visibility.Collapsed;
        }

        class TrackChoice
        {
            public string Label;
            public int AudioIndex = -1;
            public string SubtitleId;
            public string Quality;
        }

        static string Mbps(int kbps) => (kbps / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " Mbps";

        /// <summary>Audio tracks of the file playing, from the catalog (the plan names only the selected one).</summary>
        async Task<List<TrackChoice>> AudioChoicesAsync()
        {
            var file = await PlayingVersionAsync();
            var list = new List<TrackChoice>();
            var i = 0;
            foreach (var t in file?.Objects("audio_tracks") ?? Enumerable.Empty<JsonObject>())
            {
                list.Add(new TrackChoice { Label = AudioLabel(t, i), AudioIndex = i });
                i++;
            }
            return list;
        }

        static string AudioLabel(JsonObject t, int index)
        {
            var parts = new List<string>();
            var lang = t.Str("language");
            if (!string.IsNullOrEmpty(lang)) parts.Add(lang.ToUpperInvariant());
            var title = t.Str("title");
            if (!string.IsNullOrEmpty(title)) parts.Add(title);
            var detail = new List<string>();
            var codec = t.Str("codec");
            if (!string.IsNullOrEmpty(codec)) detail.Add(codec.ToUpperInvariant());
            var layout = t.Str("layout");
            if (!string.IsNullOrEmpty(layout)) detail.Add(layout);
            if (detail.Count > 0) parts.Add("(" + string.Join(" ", detail) + ")");
            return parts.Count > 0 ? string.Join(" ", parts) : "Track " + (index + 1);
        }

        async void ShowTrackPicker()
        {
            if (pickerOpen || session == null || recovering || leaving) return;
            pickerOpen = true;
            try
            {
                var audio = await AudioChoicesAsync();
                var subs = new List<TrackChoice> { new TrackChoice { Label = "Off" } };
                subs.AddRange(session.SidecarSubtitles().Where(s => s.TrackId != null)
                    .Select(s => new TrackChoice { Label = s.Label, SubtitleId = s.TrackId }));

                // "Auto" is the server's choice: the original when the measured connection carries it.
                var qualities = new List<TrackChoice> { new TrackChoice { Label = "Auto (best your connection carries)", Quality = "auto" } };
                qualities.AddRange(session.AvailableQualities().Select(q => new TrackChoice
                {
                    Label = q.Kbps > 0 ? q.Name + "  ·  " + Mbps(q.Kbps) : q.Name,
                    Quality = q.Label,
                }));
                var currentQuality = session.QualityPreference;

                var currentAudio = session.SelectedAudioIndex ?? 0;
                var audioList = MakeList(audio, audio.FindIndex(a => a.AudioIndex == currentAudio));
                var subList = MakeList(subs, Math.Max(0, subs.FindIndex(s => s.SubtitleId == currentSubtitleId)));
                var qualityList = MakeList(qualities, Math.Max(0, qualities.FindIndex(q => q.Quality == currentQuality)));

                var panel = new StackPanel();
                panel.Children.Add(Heading("Audio"));
                if (audio.Count > 0) panel.Children.Add(audioList);
                else panel.Children.Add(new TextBlock { Text = "No audio track list for this title.", FontSize = Ui.DialogItemFontSize });
                panel.Children.Add(Heading("Subtitles"));
                panel.Children.Add(subList);
                if (qualities.Count > 1)
                {
                    panel.Children.Add(Heading("Quality"));
                    panel.Children.Add(qualityList);
                }

                var dialog = new ContentDialog
                {
                    Title = "Audio, subtitles & quality",
                    Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    PrimaryButtonText = "Apply",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                var pickedAudio = Picked(audioList);
                var pickedSub = Picked(subList)?.SubtitleId;
                var pickedQuality = Picked(qualityList)?.Quality ?? currentQuality;
                var audioIndex = pickedAudio?.AudioIndex ?? currentAudio;
                // A quality change carries the picked tracks with it, so it covers an audio change too.
                if (pickedQuality != currentQuality)
                    await ReloadAsync(p => session.ChangeQualityAsync(pickedQuality, audioIndex, pickedSub, p), "Switching quality…");
                else if (audioIndex != currentAudio)
                    await ReloadAsync(p => session.ChangeTracksAsync(audioIndex, pickedSub, p), "Switching audio…");
                else if (pickedSub != currentSubtitleId && session.SubtitleBurnedIn)
                    // The current one is part of the picture: only a new stream removes or replaces it.
                    await ReloadAsync(p => session.ChangeTracksAsync(audioIndex, pickedSub, p), "Switching subtitles\u2026");
                else if (pickedSub != currentSubtitleId)
                    ShowSubtitle(pickedSub);
            }
            finally
            {
                pickerOpen = false;
            }
        }

        static TextBlock Heading(string text) => new TextBlock
        {
            Text = text,
            FontSize = Ui.DialogHeadingFontSize,
            FontWeight = Windows.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4),
        };

        static ListView MakeList(List<TrackChoice> items, int selected)
        {
            // SingleSelectionFollowsFocus off: moving the D-pad down through a list to reach Apply
            // must not select each row it passes (it used to leave the lowest quality picked).
            // A / Enter selects the focused row.
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                SingleSelectionFollowsFocus = false,
                MaxHeight = Ui.DialogListMaxHeight,
            };
            foreach (var c in items)
                list.Items.Add(new ListViewItem { Content = c.Label, Tag = c, FontSize = Ui.DialogItemFontSize });
            if (selected >= 0 && selected < items.Count) list.SelectedIndex = selected;
            return list;
        }

        static TrackChoice Picked(ListView list) => (list.SelectedItem as ListViewItem)?.Tag as TrackChoice;

        /// <summary>
        /// Subtitles switch locally. A track not used yet in this stream is fetched first (one
        /// request), then turned on when the player has read it.
        /// </summary>
        async void ShowSubtitle(string trackId)
        {
            if (playbackItem == null) return;
            pendingSubtitleId = trackId;
            SidecarSubtitle sub;
            if (trackId != null && !subtitleTracks.ContainsKey(trackId) && sidecarInfo.TryGetValue(trackId, out sub))
            {
                currentSubtitleId = trackId;
                ShowHint("Loading subtitles\u2026", 4000);
                selectedSubtitle = null;
                var current = playbackItem.TimedMetadataTracks;
                for (var i = 0; i < current.Count; i++)
                    current.SetPresentationMode((uint)i, TimedMetadataTrackPresentationMode.Disabled);
                await AttachSubtitleAsync(sub);
                // Resolved sets selectedSubtitle; TimedMetadataTracksChanged then turns it on.
                return;
            }
            var tracks = playbackItem.TimedMetadataTracks;
            TimedMetadataTrack wanted = null;
            if (trackId != null) subtitleTracks.TryGetValue(trackId, out wanted);
            for (var i = 0; i < tracks.Count; i++)
                tracks.SetPresentationMode((uint)i, ReferenceEquals(tracks[i], wanted)
                    ? TimedMetadataTrackPresentationMode.PlatformPresented
                    : TimedMetadataTrackPresentationMode.Disabled);
            selectedSubtitle = wanted;
            currentSubtitleId = wanted != null ? trackId : null;
        }

        /// <summary>
        /// Audio and quality changes need a new stream: ask the server for a plan at the current
        /// position (<paramref name="replan"/> gets it), then load that plan.
        /// </summary>
        /// <param name="keepPlayingOnFailure">For changes the app makes itself: if the server refuses, carry on
        /// with the current stream rather than stop with the error.</param>
        async Task ReloadAsync(Func<double, Task> replan, string message, bool keepPlayingOnFailure = false)
        {
            var position = CurrentPosition();
            recovering = true;
            Stall.IsActive = false;
            Overlay.Visibility = Visibility.Visible;
            Busy.IsActive = true;
            Status.Text = message;
            try
            {
                await replan(position);
                stalls.Clear();
                await LoadPlanAsync();
            }
            catch (ApiException ex) when (keepPlayingOnFailure)
            {
                PlaybackLog.Write("replan refused, keeping the current stream: " + ex.Status + " " + ex.Code + ": " + ex.Message);
                recovering = false;
                Overlay.Visibility = Visibility.Collapsed;
                Busy.IsActive = false;
                ResetPace(TimeSpan.FromSeconds(30));
            }
            catch (ApiException ex)
            {
                recovering = false;
                Fail(ex.Message);
            }
        }

        void Fail(string message)
        {
            Overlay.Visibility = Visibility.Visible;
            Busy.IsActive = false;
            Status.Text = message + "\n\nPress B to go back.";
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Registered first: the page we return to checks it straight away.
            var stopped = new TaskCompletionSource<bool>();
            pendingStop = stopped.Task;
            try { await LeaveAsync(); }
            finally { stopped.TrySetResult(true); }
        }

        async Task LeaveAsync()
        {
            if (session != null && player != null && Overlay.Visibility != Visibility.Visible)
                await WritePlaybackReportAsync();
            leaving = true;
            timer?.Stop();
            paceTimer?.Stop();
            if (controlPanelStates != null) controlPanelStates.CurrentStateChanged -= ControlPanelStateChanged;
            RestorePointer();
            var position = CurrentPosition();
            keepAwake?.RequestRelease();
            keepAwake = null;
            if (player != null)
            {
                player.MediaFailed -= Player_MediaFailed;
                player.Pause();
                player.Source = null;
                Video.SetMediaPlayer(null);
                player.Dispose();
                player = null;
            }
            if (display != null) await display.RestoreAsync();
            if (session != null) await session.StopAsync(position);
        }
    }
}
