using System.Linq;
using Silolux.Models;
using Silolux.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Coming back from the player refreshes Continue Watching.
            Load();
        }

        async void Load()
        {
            Error.Text = "";
            Busy.IsActive = Sections.ItemsSource == null;
            try
            {
                var r = await SiloClient.Current.HomeSectionsAsync();
                var sections = r.Objects("sections").Select(Section.From).Where(s => s.Cards.Count > 0).ToList();
                Sections.ItemsSource = sections;
                if (sections.Count == 0) Error.Text = "Nothing here yet. Add a library on the server, then refresh.";
            }
            catch (ApiException ex)
            {
                if (ex.Status == 401) { App.Restart(typeof(SignInPage)); return; }
                if (ex.Status == 403 || ex.Code == "profile_header_required") { App.Restart(typeof(ProfilesPage)); return; }
                Error.Text = ex.Message;
            }
            finally
            {
                Busy.IsActive = false;
            }
        }

        void Card_ItemClick(object sender, ItemClickEventArgs e)
        {
            var card = (Card)e.ClickedItem;
            Frame.Navigate(typeof(ItemPage), card.ContentId);
        }

        void Refresh_Click(object sender, RoutedEventArgs e) => Load();

        void SwitchProfile_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.ProfileId = null;
            AppSettings.ProfileToken = null;
            App.Restart(typeof(ProfilesPage));
        }
    }
}
