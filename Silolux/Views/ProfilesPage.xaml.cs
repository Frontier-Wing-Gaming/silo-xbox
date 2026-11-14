using System;
using System.Linq;
using Silolux.Models;
using Silolux.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    public sealed partial class ProfilesPage : Page
    {
        public ProfilesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                var r = await SiloClient.Current.ProfilesAsync();
                var list = r.Objects("items").Select(Profile.From).ToList();
                Profiles.ItemsSource = list;
                if (list.Count == 1 && !list[0].HasPin)
                {
                    Choose(list[0].Id, null);
                    return;
                }
                Profiles.Focus(FocusState.Programmatic);
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

        async void Profiles_ItemClick(object sender, ItemClickEventArgs e)
        {
            var p = (Profile)e.ClickedItem;
            if (!p.HasPin)
            {
                Choose(p.Id, null);
                return;
            }
            var pin = new PasswordBox { PlaceholderText = "PIN", FontSize = 28, MaxLength = 12 };
            pin.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.NumericPin) } };
            var dialog = new ContentDialog
            {
                Title = "Enter the PIN for " + p.Name,
                Content = pin,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var previous = AppSettings.ProfileId;
                AppSettings.ProfileId = null;
                var ok = await SiloClient.Current.VerifyPinAsync(p.Id, pin.Password);
                AppSettings.ProfileId = previous;
                if (ok) Choose(p.Id, AppSettings.ProfileToken);
                else Error.Text = "That PIN isn't right.";
            }
            catch (ApiException ex)
            {
                Error.Text = ex.Message;
            }
        }

        void Choose(string profileId, string profileToken)
        {
            AppSettings.ProfileId = profileId;
            AppSettings.ProfileToken = profileToken;
            App.Restart(typeof(HomePage));
        }

        async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await SiloClient.Current.SignOutAsync();
            App.Restart(typeof(SignInPage));
        }
    }
}
