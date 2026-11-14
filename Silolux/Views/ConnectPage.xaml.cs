using System;
using Silolux.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    public sealed partial class ConnectPage : Page
    {
        public ConnectPage()
        {
            InitializeComponent();
            // Focus once the page is in the tree; Keyboard focus is what a controller user
            // sees (highlight) and what lets A open the on-screen keyboard.
            Loaded += (s, e) => Address.Focus(FocusState.Keyboard);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Address.Text = AppSettings.ServerUrl ?? "";
        }

        void Address_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.GamepadMenu)
            {
                e.Handled = true;
                Connect_Click(sender, null);
            }
        }

        async void Connect_Click(object sender, RoutedEventArgs e)
        {
            Error.Text = "";
            Uri uri;
            try
            {
                uri = SiloClient.Normalize(Address.Text);
            }
            catch (ArgumentException ex)
            {
                Error.Text = ex.Message;
                return;
            }
            ConnectButton.IsEnabled = false;
            Busy.IsActive = true;
            try
            {
                var client = SiloClient.Use(uri.AbsoluteUri);
                var info = await client.SystemInfoAsync();
                if (info.Num("api_major") != 2)
                {
                    Error.Text = "This server speaks a Silo API this app does not support. Update the server or the app.";
                    return;
                }
                if (AppSettings.ServerUrl != uri.AbsoluteUri) AppSettings.SignOut();
                AppSettings.ServerUrl = uri.AbsoluteUri;
                App.Restart(App.StartPage());
            }
            catch (ApiException ex)
            {
                Error.Text = "No Silo server answered at " + uri.Host + ". " + ex.Message;
                if (ex.Status == 0 && !PlaybackCapabilities.IsXbox)
                    Error.Text += "\n\nOn a Windows PC, this app can't reach a server on the same PC (including localhost) " +
                                  "until it is exempted. In an administrator PowerShell: CheckNetIsolation LoopbackExempt -a '-n=" +
                                  Windows.ApplicationModel.Package.Current.Id.FamilyName + "'";
            }
            finally
            {
                ConnectButton.IsEnabled = true;
                Busy.IsActive = false;
            }
        }
    }
}
