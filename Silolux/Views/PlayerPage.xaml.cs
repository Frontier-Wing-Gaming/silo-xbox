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

        public PlayerPage()
        {
            InitializeComponent();
            // Menu / View on a controller, T on a keyboard: audio and subtitles. Registered with
            // handledEventsToo because the transport controls handle most keys themselves.
            AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
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
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Overlay.Visibility = Visibility.Collapsed;
                Busy.IsActive = false;
                recovering = false;
                ShowHintOnce();
            });
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
            // Report pause and resume right away rather than at the next tick.
            var state = sender.PlaybackState;
            if (state == MediaPlaybackState.Paused || state == MediaPlaybackState.Playing)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () => await ReportAsync());
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

        async void ShowHintOnce()
        {
            if (hintShown) return;
            hintShown = true;
            TracksHintText.Text = Ui.Tv ? "\u2630  Audio & subtitles" : "T  Audio & subtitles";
            TracksHint.Visibility = Visibility.Visible;
            await Task.Delay(5000);
            TracksHint.Visibility = Visibility.Collapsed;
        }

        class TrackChoice
        {
            public string Label;
            public int AudioIndex = -1;
            public string SubtitleId;
        }

        /// <summary>Audio tracks of the file playing, from the catalog (the plan names only the selected one).</summary>
        async Task<List<TrackChoice>> AudioChoicesAsync()
        {
            if (item == null && !string.IsNullOrEmpty(args?.ContentId))
            {
                try { item = await SiloClient.Current.ItemAsync(args.ContentId); }
                catch (ApiException) { }
            }
            var versions = item?.Objects("versions").ToList() ?? new List<JsonObject>();
            var file = versions.FirstOrDefault(v => v.Str("file_id") == session.EffectiveFileId) ?? versions.FirstOrDefault();
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

                var currentAudio = session.SelectedAudioIndex ?? 0;
                var audioList = MakeList(audio, audio.FindIndex(a => a.AudioIndex == currentAudio));
                var subList = MakeList(subs, Math.Max(0, subs.FindIndex(s => s.SubtitleId == currentSubtitleId)));

                var panel = new StackPanel();
                panel.Children.Add(Heading("Audio"));
                if (audio.Count > 0) panel.Children.Add(audioList);
                else panel.Children.Add(new TextBlock { Text = "No audio track list for this title.", FontSize = Ui.DialogItemFontSize });
                panel.Children.Add(Heading("Subtitles"));
                panel.Children.Add(subList);

                var dialog = new ContentDialog
                {
                    Title = "Audio & subtitles",
                    Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    PrimaryButtonText = "Apply",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                var pickedAudio = Picked(audioList);
                var pickedSub = Picked(subList)?.SubtitleId;
                if (pickedAudio != null && pickedAudio.AudioIndex != currentAudio)
                    await ChangeAudioAsync(pickedAudio.AudioIndex, pickedSub);
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
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = Ui.DialogListMaxHeight };
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

        /// <summary>Audio needs a new stream for remuxes and transcodes: ask the server, then reload.</summary>
        async Task ChangeAudioAsync(int audioIndex, string subtitleId)
        {
            var position = CurrentPosition();
            recovering = true;
            Overlay.Visibility = Visibility.Visible;
            Busy.IsActive = true;
            Status.Text = "Switching audio\u2026";
            try
            {
                await session.ChangeTracksAsync(audioIndex, subtitleId, position);
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
            if (session != null) await session.StopAsync(position);
        }
    }
}
