using Windows.UI.Xaml;

namespace Silolux.Services
{
    /// <summary>
    /// Device-dependent sizing, read from XAML with {x:Bind services:Ui.Name}. Xbox lays the
    /// app out at 960x540 effective pixels (2x scale on a 1080p TV), half the PC layout this
    /// UI was drawn for, so the home rows are shrunk there. Static values, never written to
    /// Application.Resources (changing those at startup crashes on Xbox with E_UNEXPECTED).
    /// </summary>
    public static class Ui
    {
        public static bool Tv { get; } = PlaybackCapabilities.IsXbox;

        /// <summary>Card size relative to the PC layout.</summary>
        public static double CardScale => Tv ? 0.6 : 1.0;

        /// <summary>Physical pixels per layout unit, for image decode sizes.</summary>
        public static double PixelsPerUnit => Tv ? 2.0 : 1.0;

        public static double CardTitleFontSize => Tv ? 13 : 17;
        public static double CardSubtitleFontSize => Tv ? 11 : 15;
        public static double RowTitleFontSize => Tv ? 18 : 24;
        public static double HomeTitleFontSize => Tv ? 24 : 34;

        // The system already keeps an Xbox app inside the TV-safe area; don't add a full PC margin on top.
        public static Thickness HomeHeaderPadding => Tv ? new Thickness(24, 12, 24, 0) : new Thickness(48, 24, 48, 0);
        public static Thickness HomeRowsPadding => Tv ? new Thickness(24, 0, 0, 24) : new Thickness(48, 0, 0, 48);

        // Item (movie / show) page.
        public static Thickness ItemPagePadding => Tv ? new Thickness(24, 16, 24, 24) : new Thickness(72, 48, 72, 48);
        public static double ItemTitleFontSize => Tv ? 30 : 48;
        public static double LogoMaxHeight => Tv ? 72 : 140;
        public static double LogoMaxWidth => Tv ? 280 : 520;
        public static Thickness LogoMargin => Tv ? new Thickness(0, 8, 0, 8) : new Thickness(0, 24, 0, 16);
        public static double MetaFontSize => Tv ? 13 : 18;
        public static double OverviewFontSize => Tv ? 14 : 20;
        public static double OverviewMaxWidth => Tv ? 560 : 900;
        public static Thickness OverviewMargin => Tv ? new Thickness(0, 10, 0, 0) : new Thickness(0, 20, 0, 0);
        public static Thickness ActionsMargin => Tv ? new Thickness(0, 16, 0, 0) : new Thickness(0, 32, 0, 0);
        public static Thickness SeriesMargin => Tv ? new Thickness(0, 20, 0, 0) : new Thickness(0, 40, 0, 0);
        public static double SeasonFontSize => Tv ? 15 : 20;
        public static double EpisodeStillWidth => Tv ? 176 : 288;
        public static GridLength EpisodeStillColumn => new GridLength(EpisodeStillWidth);
        public static double EpisodeStillHeight => EpisodeStillWidth * 9 / 16;
        public static double EpisodeHeadingFontSize => Tv ? 16 : 22;
        public static double EpisodeOverviewFontSize => Tv ? 13 : 18;
        public static Thickness EpisodeMargin => Tv ? new Thickness(0, 6, 0, 6) : new Thickness(0, 10, 0, 10);

        // Profile picker.
        public static double PageTitleFontSize => Tv ? 26 : 40;
        public static double ProfileTileWidth => Tv ? 128 : 200;
        public static double AvatarSize => Tv ? 96 : 160;
        public static CornerRadius AvatarCorner => new CornerRadius(AvatarSize / 2);
        public static double AvatarInitialFontSize => Tv ? 40 : 64;
        public static double ProfileNameFontSize => Tv ? 15 : 22;
        public static Thickness ProfileTileMargin => Tv ? new Thickness(8) : new Thickness(12);

        // Player.
        public static double PlayerStatusFontSize => Tv ? 16 : 22;
        public static double PlayerStatusMaxWidth => Tv ? 600 : 900;
        public static double PlayerHintFontSize => Tv ? 13 : 16;
        public static Thickness PlayerHintMargin => Tv ? new Thickness(0, 16, 24, 0) : new Thickness(0, 24, 32, 0);
        public static double DialogHeadingFontSize => Tv ? 16 : 20;
        public static double DialogItemFontSize => Tv ? 14 : 18;
        public static double DialogListMaxHeight => Tv ? 180 : 260;

        // Buttons (the shared button styles hold the PC values; a Style setter can't x:Bind).
        public static double ButtonFontSize => Tv ? 15 : 20;
        public static Thickness ButtonPadding => Tv ? new Thickness(18, 8, 18, 8) : new Thickness(28, 12, 28, 12);
        public static double ButtonMinWidth => Tv ? 120 : 200;
    }
}
