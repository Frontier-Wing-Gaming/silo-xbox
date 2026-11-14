using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Silolux.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

        public PlayerPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            var args = (PlayArgs)e.Parameter;
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
            selectedSubtitle = null;
            var selectedId = session.SelectedSubtitleId;
            foreach (var sub in session.SidecarSubtitles())
            {
                var tts = TimedTextSource.CreateFromUri(sub.Url, sub.Language);
                var label = sub.Label;
                var isSelected = sub.TrackId != null && sub.TrackId == selectedId;
                tts.Resolved += (s, a) =>
                {
                    foreach (var t in a.Tracks)
                    {
                        t.Label = label;
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
