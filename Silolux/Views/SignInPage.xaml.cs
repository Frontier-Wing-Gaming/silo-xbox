using System;
using System.Threading;
using System.Threading.Tasks;
using Silolux.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    /// <summary>
    /// Device sign-in (code shown here, approved on another device), with a
    /// password form beside it for servers or people who prefer it.
    /// </summary>
    public sealed partial class SignInPage : Page
    {
        CancellationTokenSource polling;

        public SignInPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            StartDeviceLogin();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            polling?.Cancel();
        }

        async void StartDeviceLogin()
        {
            polling?.Cancel();
            polling = new CancellationTokenSource();
            var token = polling.Token;
            NewCode.Visibility = Visibility.Collapsed;
            DeviceStatus.Text = "";
            Code.Text = "····-····";
            try
            {
                var start = await SiloClient.Current.StartDeviceLoginAsync();
                var uri = start.Str("verification_uri") ?? SiloClient.Current.Resolve("/activate").AbsoluteUri;
                ActivateUrl.Text = uri;
                Code.Text = start.Str("user_code");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(start.Num("expires_in", 900));
                var wait = Math.Max(1, start.Num("interval", 5));
                await PollAsync(start.Str("device_code"), wait, deadline, token);
            }
            catch (TaskCanceledException) { }
            catch (ApiException ex)
            {
                DeviceStatus.Text = "Device sign-in isn't available: " + ex.Message;
                NewCode.Visibility = Visibility.Visible;
            }
        }

        async Task PollAsync(string deviceCode, double wait, DateTimeOffset deadline, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), token);
                if (DateTimeOffset.UtcNow > deadline) break;
                var r = await SiloClient.Current.PollDeviceLoginAsync(deviceCode);
                switch (r.Str("status"))
                {
                    case "pending":
                        if (r.Bool("opened")) DeviceStatus.Text = "Waiting for approval…";
                        wait = Math.Max(1, r.Num("poll_after", wait));
                        DateTimeOffset moved;
                        if (DateTimeOffset.TryParse(r.Str("expires_at") ?? "", out moved)) deadline = moved;
                        continue;
                    case "approved":
                        SignedIn();
                        return;
                    case "denied":
                        DeviceStatus.Text = "The request was declined.";
                        NewCode.Visibility = Visibility.Visible;
                        return;
                    default:
                        DeviceStatus.Text = "This code is no longer valid.";
                        NewCode.Visibility = Visibility.Visible;
                        return;
                }
            }
            if (!token.IsCancellationRequested)
            {
                DeviceStatus.Text = "This code expired.";
                NewCode.Visibility = Visibility.Visible;
            }
        }

        void SignedIn()
        {
            polling?.Cancel();
            App.Restart(string.IsNullOrEmpty(AppSettings.ProfileId) ? typeof(ProfilesPage) : typeof(HomePage));
        }

        void NewCode_Click(object sender, RoutedEventArgs e) => StartDeviceLogin();

        void Password_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SignIn_Click(sender, null);
            }
        }

        async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            Error.Text = "";
            if (string.IsNullOrWhiteSpace(Username.Text) || string.IsNullOrEmpty(Password.Password))
            {
                Error.Text = "Enter your username and password.";
                return;
            }
            SignInButton.IsEnabled = false;
            try
            {
                await SiloClient.Current.LoginAsync(Username.Text.Trim(), Password.Password);
                SignedIn();
            }
            catch (ApiException ex)
            {
                Error.Text = ex.Status == 401 ? "That username and password didn't match." : ex.Message;
            }
            finally
            {
                SignInButton.IsEnabled = true;
            }
        }

        void ChangeServer_Click(object sender, RoutedEventArgs e)
        {
            polling?.Cancel();
            App.Restart(typeof(ConnectPage));
        }
    }
}
