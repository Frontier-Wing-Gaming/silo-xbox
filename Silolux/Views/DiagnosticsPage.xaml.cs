using System;
using System.Collections.Generic;
using System.Linq;
using Silolux.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    /// <summary>
    /// Shows DeviceDiagnostics: what this device decodes and outputs next to what the app reports to
    /// the server. Also written to LocalState\diagnostics.txt (on Xbox, Device Portal's file explorer).
    /// </summary>
    public sealed partial class DiagnosticsPage : Page
    {
        static readonly Brush YesBrush = new SolidColorBrush(Color.FromArgb(255, 124, 217, 146));
        static readonly Brush NoBrush = new SolidColorBrush(Color.FromArgb(255, 154, 160, 174));
        static readonly Brush FlagBrush = new SolidColorBrush(Color.FromArgb(255, 255, 196, 92));

        string report;

        public DiagnosticsPage()
        {
            InitializeComponent();
            // Nothing to paste into on a console; no HDMI mode control on a PC.
            if (Ui.Tv) CopyButton.Visibility = Visibility.Collapsed;
            else HdrButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Asks the console for HDR10 at the current resolution and rate, holds it three seconds, then
        /// restores the user's mode. Settles whether HDR works when the mode list claims it doesn't.
        /// </summary>
        async void Hdr_Click(object sender, RoutedEventArgs e)
        {
            HdrButton.IsEnabled = false;
            var original = HdrButton.Content;
            HdrButton.Content = "Testing…";
            string result;
            try
            {
                var hdmi = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView();
                var current = hdmi.GetCurrentDisplayMode();
                // HDR can only be asked for on a mode that offers it (otherwise: invalid argument).
                var modes = hdmi.GetSupportedDisplayModes().Where(m => m.IsSmpte2084Supported).ToList();
                var mode = modes.FirstOrDefault(m => m.ResolutionWidthInRawPixels == current.ResolutionWidthInRawPixels &&
                                                     Math.Abs(m.RefreshRate - current.RefreshRate) < 0.01) ?? modes.FirstOrDefault();
                var ok = mode != null && await hdmi.RequestSetCurrentDisplayModeAsync(mode, Windows.Graphics.Display.Core.HdmiDisplayHdrOption.Eotf2084);
                if (mode == null)
                {
                    result = "The console offers this app no HDR mode (check Allow HDR10 and Colour depth in TV & display options)";
                }
                else if (ok)
                {
                    await System.Threading.Tasks.Task.Delay(3000);
                    var now = hdmi.GetCurrentDisplayMode();
                    result = "HDR10 switch accepted (now " + Services.DisplayModeMatcher.BitsPerChannel(now) + "-bit, " + now.ColorSpace + ")";
                    await hdmi.SetDefaultDisplayModeAsync();
                }
                else
                {
                    result = "HDR10 switch refused by the console";
                }
            }
            catch (Exception ex)
            {
                result = "HDR10 switch failed: " + ex.Message;
            }
            Saved.Text = result;
            if (report != null)
            {
                report += Environment.NewLine + "HDR switch test: " + result + Environment.NewLine;
                await Save(report);
                Saved.Text = result + " · added to diagnostics.txt";
            }
            HdrButton.Content = original;
            HdrButton.IsEnabled = true;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e) => Run();

        void Run_Click(object sender, RoutedEventArgs e) => Run();

        async void Run()
        {
            RunButton.IsEnabled = false;
            Busy.IsActive = true;
            Busy.Visibility = Visibility.Visible;
            LeftColumn.Children.Clear();
            RightColumn.Children.Clear();
            try
            {
                var sections = await DeviceDiagnostics.RunAsync();
                // Decoders on the right (long lists), everything else on the left.
                foreach (var s in sections)
                    (s.Title.EndsWith("decoders") ? RightColumn : LeftColumn).Children.Add(Render(s));
                report = DeviceDiagnostics.ToText(sections);
                var playback = await LastPlaybackAsync();
                if (playback != null)
                {
                    LeftColumn.Children.Insert(0, Render(PlaybackSection(playback)));
                    report += Environment.NewLine + playback;
                }
                await Save(report);
            }
            catch (Exception ex)
            {
                LeftColumn.Children.Add(new TextBlock { Text = "Diagnostics failed: " + ex.Message, TextWrapping = TextWrapping.Wrap });
            }
            finally
            {
                Busy.IsActive = false;
                Busy.Visibility = Visibility.Collapsed;
                RunButton.IsEnabled = true;
                Scroller.Focus(FocusState.Programmatic);
            }
        }

        /// <summary>The player's report from the last film (PlayerPage writes it mid-playback), or null.</summary>
        static async System.Threading.Tasks.Task<string> LastPlaybackAsync()
        {
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync("playback.txt") as StorageFile;
                return item == null ? null : await FileIO.ReadTextAsync(item);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static DiagSection PlaybackSection(string text)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var s = new DiagSection { Title = lines[0] };
            foreach (var line in lines.Skip(1))
            {
                var at = line.IndexOf(": ", StringComparison.Ordinal);
                if (at < 0) s.Add(line.Trim(), "");
                else s.Add(line.Substring(0, at).Trim(), line.Substring(at + 2));
            }
            return s;
        }

        async System.Threading.Tasks.Task Save(string text)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("diagnostics.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, text);
                Saved.Text = Ui.Tv
                    ? "Saved as LocalState\\diagnostics.txt (Device Portal → File explorer → LocalAppData → Silolux)"
                    : "Saved to " + file.Path;
            }
            catch (Exception)
            {
                Saved.Text = "";
            }
        }

        FrameworkElement Render(DiagSection section)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, Ui.Tv ? 12 : 20) };
            panel.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = Ui.DialogHeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var size = Ui.Tv ? 12.0 : 15.0;
            for (var i = 0; i < section.Rows.Count; i++)
            {
                var r = section.Rows[i];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(grid, i, 0, r.Name, size, null);
                Cell(grid, i, 1, r.Value, size, r.State == DiagState.Yes ? YesBrush : r.State == DiagState.No ? NoBrush : null);
                if (r.Reported != null)
                    Cell(grid, i, 2, r.Unreported ? "not reported ★" : r.Reported, size - 1, r.Unreported ? FlagBrush : NoBrush);
            }
            panel.Children.Add(grid);
            return panel;
        }

        static void Cell(Grid grid, int row, int column, string text, double size, Brush brush)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            if (brush != null) t.Foreground = brush;
            Grid.SetRow(t, row);
            Grid.SetColumn(t, column);
            grid.Children.Add(t);
        }

        void Scroller_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            double? delta = null;
            switch (e.Key)
            {
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    delta = 80; break;
                case VirtualKey.Up:
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    delta = -80; break;
                case VirtualKey.PageDown:
                case VirtualKey.GamepadRightTrigger:
                    delta = Scroller.ViewportHeight * 0.9; break;
                case VirtualKey.PageUp:
                case VirtualKey.GamepadLeftTrigger:
                    delta = -Scroller.ViewportHeight * 0.9; break;
            }
            if (delta == null) return;
            // At the top, Up leaves for the buttons instead of doing nothing.
            if (delta < 0 && Scroller.VerticalOffset <= 0) return;
            e.Handled = true;
            Scroller.ChangeView(null, Math.Max(0, Scroller.VerticalOffset + delta.Value), null);
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(report)) return;
            var data = new DataPackage();
            data.SetText(report);
            Clipboard.SetContent(data);
            CopyButton.Content = "Copied";
        }
    }
}
