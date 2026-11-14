using Windows.UI.Xaml;

namespace Silolux.Services
{
    /// <summary>
    /// Device-dependent sizing. Xbox lays the app out at 960x540 effective pixels (2x
    /// scale on a 1080p TV), half the PC layout this UI was drawn for, so the home rows
    /// are shrunk there. Values are App resources so XAML can read them with StaticResource.
    /// </summary>
    public static class Ui
    {
        public static bool Tv => PlaybackCapabilities.IsXbox;

        /// <summary>Card size relative to the PC layout.</summary>
        public static double CardScale => Tv ? 0.6 : 1.0;

        /// <summary>Physical pixels per layout unit, for image decode sizes.</summary>
        public static double PixelsPerUnit => Tv ? 2.0 : 1.0;

        /// <summary>Overrides the PC defaults in App.xaml. Call before the first page loads.</summary>
        public static void ApplyResources(ResourceDictionary r)
        {
            if (!Tv) return;
            r["CardTitleFontSize"] = 13.0;
            r["CardSubtitleFontSize"] = 11.0;
            r["RowTitleFontSize"] = 18.0;
            r["HomeTitleFontSize"] = 24.0;
            // The system already keeps the app inside the TV-safe area; don't add a full PC margin on top.
            r["HomeHeaderPadding"] = new Thickness(24, 12, 24, 0);
            r["HomeRowsPadding"] = new Thickness(24, 0, 0, 24);
            r["CardSpacing"] = new Thickness(0, 0, 10, 0);
        }
    }
}
