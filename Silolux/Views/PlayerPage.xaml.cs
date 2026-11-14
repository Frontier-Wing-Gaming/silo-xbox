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
            }
            else if (e.NewState?.Name == "ControlPanelFadeIn")
            {
                RestorePointer();
                if (FocusManager.GetFocusedElement() == FocusSink) FocusControls();
            }
        }

        /// <summary>Keys while the controls are hidden (focus on FocusSink): A / Space / Enter play or pause, others bring the controls back.</summary>
        void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (FocusManager.GetFocusedElement() != FocusSink) return;
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

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
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
            subtitleTracks.Clear();
            selectedSubtitle = null;
            var selectedId = session.SelectedSubtitleId;
            currentSubtitleId = selectedId;
            foreach (var sub in session.SidecarSubtitles())
            {
                var tts = TimedTextSource.CreateFromUri(sub.Url, sub.Language);
                var label = sub.Label;
                var trackId = sub.TrackId;
                var isSelected = trackId != null && trackId == selectedId;
                tts.Resolved += (s, a) =>
                {
                    foreach (var t in a.Tracks)
                    {
                        t.Label = label;
                        if (trackId != null) subtitleTracks[trackId] = t;
                        if (isSelected) selectedSubtitle = t;
                    }
                };
                source.ExternalTimedTextSources.Add(tts);
                sidecars.Add(new KeyValuePair<TimedTextSource, string>(tts, label));
            }

            var start = TimeSpan.FromSeconds(Math.Max(0, session.PlayerStartSeconds));
            playbackItem = new MediaPlaybackItem(source, start);
            playbackItem.TimedMetadataTracksChanged += (s, a) => ApplySubtitleSelection();
            playbackItem.AudioTracksChanged += (s, a) => ApplyAudioSelection();
            player.Source = playbackItem;
        }

        void ApplySubtitleSelection()
        {
            if (playbackItem == null || selectedSubtitle == null) return;
            var tracks = playbackItem.TimedMetadataTracks;
            for (var i = 0; i < tracks.Count; i++)
                if (ReferenceEquals(tracks[i], selectedSubtitle))
                    tracks.SetPresentationMode((uint)i, TimedMetadataTrackPresentationMode.PlatformPresented);
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

        async void Player_MediaEnded(MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (Frame.CanGoBack && !leaving) Frame.GoBack();
            });
        }

        async void PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            var state = sender.PlaybackState;
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
            if (stalls.Count >= 3 && !stallHintShown && session != null && session.AvailableQualities().Any())
            {
                stallHintShown = true;
                // The cached speed was too optimistic: measure again for the next title.
                BandwidthEstimator.Forget();
                ShowHint((Ui.Tv ? "☰" : "T") + "  Buffering? Choose a lower quality", 8000);
            }
        }

        double CurrentPosition()
        {
            try
            {
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

        /// <summary>Subtitles are sidecar files already attached to the player: switch locally.</summary>
        void ShowSubtitle(string trackId)
        {
            if (playbackItem == null) return;
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
        async Task ReloadAsync(Func<double, Task> replan, string message)
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
            leaving = true;
            timer?.Stop();
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
