using System.Linq;
using System.Threading.Tasks;
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
                var libraries = LoadLibrariesAsync();
                var r = await SiloClient.Current.HomeSectionsAsync();
                var sections = r.Objects("sections").Select(Section.From).Where(s => s.Cards.Count > 0).ToList();
                Sections.ItemsSource = sections;
                if (sections.Count == 0) Error.Text = "Nothing here yet. Add a library on the server, then refresh.";
                await libraries;
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

        /// <summary>Library shortcuts in the header. Books libraries are left out: this app plays video.</summary>
        async Task LoadLibrariesAsync()
        {
            try
            {
                var r = await SiloClient.Current.UserLibrariesAsync();
                LibraryButtons.Children.Clear();
                foreach (var lib in r.Objects("items"))
                {
                    var type = lib.Str("type") ?? "";
                    // "series" is the API's name for TV libraries; older servers report "tv".
                    if (type != "movies" && type != "series" && type != "tv" && type != "mixed" && type != "videos") continue;
                    var args = new BrowseArgs { LibraryId = lib.Str("id"), Name = lib.Str("name") };
                    var button = new Button
                    {
                        Content = args.Name,
                        FontSize = Ui.ButtonFontSize,
                        Margin = new Thickness(0, 0, 10, 0),
                    };
                    button.Click += (s, e) => Frame.Navigate(typeof(BrowsePage), args);
                    LibraryButtons.Children.Add(button);
                }
            }
            catch (ApiException)
            {
                // The rows still work without the shortcuts.
            }
            LibraryScroller.Visibility = LibraryButtons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Search_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(BrowsePage), new BrowseArgs());

        void Diagnostics_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DiagnosticsPage));

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
            App.Restart(typeof(ProfilesPage), ProfilesPage.Switching);
        }
    }
}
