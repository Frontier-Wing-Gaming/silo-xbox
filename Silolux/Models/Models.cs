using System;
using System.Collections.Generic;
using System.Linq;
using Silolux.Services;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Silolux.Models
{
    public class Card
    {
        public string ContentId { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public BitmapImage Image { get; set; }
        public double Progress { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public Visibility ProgressVisibility => Progress > 0.01 && Progress < 0.97 ? Visibility.Visible : Visibility.Collapsed;

        static BitmapImage Img(string url, int width)
        {
            var u = SiloClient.Current.Resolve(url);
            return u == null ? null : new BitmapImage(u) { DecodePixelWidth = width };
        }

        public static Card From(JsonObject o, bool landscape)
        {
            var type = o.Str("type") ?? "";
            var isEpisode = type == "episode";
            string image;
            if (landscape)
                image = o.Str("backdrop_url") ?? o.Str("series_backdrop_url") ?? o.Str("poster_url");
            else
                image = isEpisode ? (o.Str("series_poster_url") ?? o.Str("poster_url")) : (o.Str("poster_url") ?? o.Str("series_poster_url"));

            string subtitle;
            if (isEpisode && o.NumOrNull("season_number").HasValue)
                subtitle = string.Format("S{0} · E{1} · {2}", o.Num("season_number"), o.Num("episode_number"), o.Str("title"));
            else
                subtitle = o.NumOrNull("year").HasValue ? o.Num("year").ToString() : "";

            var duration = o.Obj("user_data")?.NumOrNull("duration_seconds") ?? o.Num("duration_seconds", o.Num("runtime") * 60);
            var pos = o.ResumeSeconds();
            var width = (landscape ? 400 : 200) * Ui.CardScale;
            return new Card
            {
                ContentId = o.Str("content_id"),
                Type = type,
                Title = isEpisode ? (o.Str("series_title") ?? o.Str("title")) : o.Str("title"),
                Subtitle = subtitle,
                Image = Img(image, (int)(width * Ui.PixelsPerUnit)),
                Progress = duration > 0 ? Math.Min(1, pos / duration) : 0,
                Width = width,
                Height = landscape ? width * 9 / 16 : width * 1.5,
            };
        }
    }

    public class Section
    {
        public string Title { get; set; }
        public bool Landscape { get; set; }
        public List<Card> Cards { get; set; }

        public static Section From(JsonObject o)
        {
            var type = o.Str("section_type") ?? "";
            var landscape = type.Contains("continue") || type.Contains("next_up") || o.Bool("featured");
            return new Section
            {
                Title = o.Str("title"),
                Landscape = landscape,
                Cards = o.Objects("items").Select(i => Card.From(i, landscape)).Where(c => c.ContentId != null).ToList(),
            };
        }
    }

    public class Profile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool HasPin { get; set; }
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
        public BitmapImage Avatar { get; set; }
        public Visibility AvatarVisibility => Avatar != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LockVisibility => HasPin ? Visibility.Visible : Visibility.Collapsed;

        public static Profile From(JsonObject o)
        {
            var avatar = SiloClient.Current.Resolve(o.Str("avatar_url"));
            return new Profile
            {
                Id = o.Str("id"),
                Name = o.Str("name"),
                HasPin = o.Bool("has_pin"),
                Avatar = avatar == null ? null : new BitmapImage(avatar) { DecodePixelWidth = (int)(Ui.AvatarSize * Ui.PixelsPerUnit) },
            };
        }
    }

    public class Season
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public override string ToString() => Title;
    }

    public class Episode
    {
        public string ContentId { get; set; }
        public string FileId { get; set; }
        public string Heading { get; set; }
        public string Overview { get; set; }
        public BitmapImage Still { get; set; }
        public double Position { get; set; }
        public double Progress { get; set; }
        public bool Played { get; set; }
        public Visibility ProgressVisibility => Progress > 0.01 && Progress < 0.97 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlayedVisibility => Played ? Visibility.Visible : Visibility.Collapsed;

        public static Episode From(JsonObject o)
        {
            var still = SiloClient.Current.Resolve(o.Str("still_url"));
            var file = o.Objects("files").FirstOrDefault(f => !f.Bool("unreadable"));
            var user = o.Obj("user_data");
            var runtime = user?.NumOrNull("duration_seconds") ?? o.Num("runtime") * 60;
            var pos = user?.Num("position_seconds") ?? 0;
            return new Episode
            {
                ContentId = o.Str("content_id"),
                FileId = user?.Str("last_file_id") ?? file?.Str("file_id"),
                Heading = string.Format("{0}. {1}", o.Num("episode_number"), o.Str("title")),
                Overview = o.Str("overview") ?? "",
                Still = still == null ? null : new BitmapImage(still) { DecodePixelWidth = (int)(Ui.EpisodeStillWidth * Ui.PixelsPerUnit) },
                Position = pos,
                Progress = runtime > 0 ? Math.Min(1, pos / runtime) : 0,
                Played = user?.Bool("played") ?? false,
            };
        }
    }
}
