using System;
using System.Collections.Generic;
using System.Linq;
using Silolux.Models;
using Silolux.Services;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    public class PlayArgs
    {
        public string ContentId;
        public string FileId;
        public bool PinFile;
        /// <summary>
        /// Where to start: the resume point this page shows (read after the last stop reached the
        /// server; see OnNavigatedTo), or 0. Null leaves it to the server, which resumes from its
        /// saved position on some servers and starts over on others (silo.pfsystems.ac started a
        /// resume at 0:00 three seconds after saving 38:54).
        /// </summary>
        public double? StartPosition;
        public string Title;
    }

    /// <summary>A movie, episode or series. Series list seasons and episodes.</summary>
    public sealed partial class ItemPage : Page
    {
        JsonObject item;
        string seriesId;
        PlayArgs resume;

        public ItemPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            var id = e.Parameter as string;
            Error.Text = "";
            Busy.IsActive = true;
            // Back from the player: its stop report may still be in flight, and until the server has
            // it the title still carries the old resume point.
            await PlayerPage.WaitForStopAsync(TimeSpan.FromSeconds(5));
            try
            {
                item = await SiloClient.Current.ItemAsync(id);
                var type = item.Str("type") ?? "";
                if (type == "season" && item.Str("series_id") != null)
                    item = await SiloClient.Current.ItemAsync(item.Str("series_id"));
                Render();
                if (IsSeries) await LoadSeasonsAsync();
                // A series with nothing to resume still offers its first episode.
                if (IsSeries && resume == null && Episodes.Items.Count > 0)
                {
                    var first = (Episode)Episodes.Items[0];
                    resume = new PlayArgs { ContentId = first.ContentId, FileId = first.FileId, StartPosition = ResumeFrom(first.Position), Title = Display(first.Heading) };
                    PlayButton.Content = "Play";
                    Actions.Visibility = Visibility.Visible;
                    RestartButton.Visibility = Visibility.Collapsed;
                }
                (Actions.Visibility == Visibility.Visible ? (Control)PlayButton : Episodes).Focus(FocusState.Programmatic);
            }
            catch (ApiException ex)
            {
                if (ex.Status == 401) { App.Restart(typeof(SignInPage)); return; }
                Error.Text = ex.Message;
            }
            finally
            {
                Busy.IsActive = false;
            }
        }

        bool IsSeries => (item.Str("type") ?? "") == "series";

        string Display(string episodeHeading) => (item.Str("title") ?? "") + " · " + episodeHeading;

        void Render()
        {
            ItemTitle.Text = item.Str("title") ?? "";
            var backdrop = SiloClient.Current.Resolve(item.Str("backdrop_url") ?? item.Str("series_backdrop_url"));
            if (backdrop != null) Backdrop.Source = new BitmapImage(backdrop) { DecodePixelWidth = 1920 };
            var logo = SiloClient.Current.Resolve(item.Str("logo_url"));
            if (logo != null)
            {
                Logo.Source = new BitmapImage(logo);
                ItemTitle.Visibility = Visibility.Collapsed;
            }
            OverviewText.Text = item.Str("overview") ?? "";

            var meta = new List<string>();
            if (item.NumOrNull("year").HasValue) meta.Add(item.Num("year").ToString());
            if (!string.IsNullOrEmpty(item.Str("content_rating"))) meta.Add(item.Str("content_rating"));
            if (item.Num("runtime") > 0) meta.Add(item.Num("runtime") + " min");
            if (IsSeries && item.Num("season_count") > 0) meta.Add(item.Num("season_count") + " seasons");
            var genres = item.Get("genres");
            if (genres != null && genres.ValueType == JsonValueType.Array)
                meta.Add(string.Join(", ", genres.GetArray().Take(3).Select(g => g.GetString())));
            Meta.Text = string.Join("  ·  ", meta);

            if (IsSeries)
            {
                SeriesPanel.Visibility = Visibility.Visible;
                seriesId = item.Str("content_id");
                var next = item.Str("play_content_id");
                Actions.Visibility = Visibility.Collapsed;
                if (next != null)
                {
                    resume = new PlayArgs { ContentId = next, Title = item.Str("title") };
                    PlayButton.Content = (item.Path("user_data")?.Num("in_progress_count") ?? 0) > 0 ? "Resume" : "Play next episode";
                    Actions.Visibility = Visibility.Visible;
                    RestartButton.Visibility = Visibility.Collapsed;
                }
                return;
            }

            var versions = item.Objects("versions").ToList();
            if (versions.Count == 0)
            {
                Actions.Visibility = Visibility.Collapsed;
                Error.Text = "This title has no playable file.";
                return;
            }
            var lastFile = item.Path("user_data")?.Str("last_file_id");
            var file = versions.FirstOrDefault(v => v.Str("file_id") == lastFile) ?? versions[0];
            var pos = item.ResumeSeconds();
            var title = item.Str("type") == "episode" && item.Str("series_title") != null
                ? item.Str("series_title") + " · " + item.Str("title")
                : item.Str("title");
            resume = new PlayArgs { ContentId = item.Str("content_id"), FileId = file.Str("file_id"), StartPosition = ResumeFrom(pos), Title = title };
            PlayButton.Content = pos > 30 ? "Resume from " + TimeSpan.FromSeconds(pos).ToString(pos >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "Play";
            RestartButton.Visibility = pos > 30 ? Visibility.Visible : Visibility.Collapsed;
        }

        async System.Threading.Tasks.Task LoadSeasonsAsync()
        {
            var r = await SiloClient.Current.SeasonsAsync(seriesId);
            var seasons = r.Objects("items")
                .Select(s => new Season { Number = (int)s.Num("season_number"), Title = s.Str("title") ?? ("Season " + s.Num("season_number")) })
                .ToList();
            Seasons.ItemsSource = seasons;
            if (seasons.Count == 0) return;
            var firstRegular = seasons.FirstOrDefault(s => s.Number > 0) ?? seasons[0];
            var current = (int)item.Num("play_season_number", firstRegular.Number);
            Seasons.SelectedItem = seasons.FirstOrDefault(s => s.Number == current) ?? seasons[0];
            await LoadEpisodesAsync(((Season)Seasons.SelectedItem).Number);
        }

        async System.Threading.Tasks.Task LoadEpisodesAsync(int season)
        {
            var r = await SiloClient.Current.EpisodesAsync(seriesId, season);
            Episodes.ItemsSource = r.Objects("items").Select(Episode.From).ToList();
        }

        async void Seasons_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var s = Seasons.SelectedItem as Season;
            if (s == null || e.RemovedItems.Count == 0) return;
            try
            {
                await LoadEpisodesAsync(s.Number);
            }
            catch (ApiException ex)
            {
                Error.Text = ex.Message;
            }
        }

        void Episode_ItemClick(object sender, ItemClickEventArgs e)
        {
            var ep = (Episode)e.ClickedItem;
            Play(new PlayArgs
            {
                ContentId = ep.ContentId,
                FileId = ep.FileId,
                StartPosition = ep.Played ? 0 : ResumeFrom(ep.Position),
                Title = Display(ep.Heading),
            });
        }

        async void Play_Click(object sender, RoutedEventArgs e)
        {
            if (resume == null) return;
            if (resume.FileId == null)
            {
                // A series' next episode: read it for its file and position.
                try
                {
                    var ep = await SiloClient.Current.ItemAsync(resume.ContentId);
                    var v = ep.Objects("versions").FirstOrDefault();
                    if (v == null) { Error.Text = "This episode has no playable file."; return; }
                    var title = (ep.Str("series_title") ?? item.Str("title")) + " · " + ep.Str("title");
                    Play(new PlayArgs { ContentId = resume.ContentId, FileId = v.Str("file_id"), StartPosition = ResumeFrom(ep.ResumeSeconds()), Title = title });
                }
                catch (ApiException ex)
                {
                    Error.Text = ex.Message;
                }
                return;
            }
            Play(resume);
        }

        void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (resume == null) return;
            Play(new PlayArgs { ContentId = resume.ContentId, FileId = resume.FileId, Title = resume.Title, StartPosition = 0 });
        }

        void Play(PlayArgs args) => Frame.Navigate(typeof(PlayerPage), args);

        /// <summary>Where "Resume" starts: the saved position, as the button shows it (over 30 s), else the start.</summary>
        static double ResumeFrom(double saved) => saved > 30 ? saved : 0;
    }
}
