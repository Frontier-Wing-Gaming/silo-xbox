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
    }
}
