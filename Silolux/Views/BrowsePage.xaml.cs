using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Silolux.Models;
using Silolux.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Silolux.Views
{
    /// <summary>What a BrowsePage shows: one library in title order, or search when LibraryId is null.</summary>
    public class BrowseArgs
    {
        public string LibraryId;
        public string Name;
    }

    /// <summary>
    /// A poster grid over GET /api/v2/catalog, paged with the server's cursor as the
    /// grid scrolls. Used for "browse a library" and for search.
    /// </summary>
    public sealed partial class BrowsePage : Page
    {
        BrowseArgs args;
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        CatalogFeed feed;

        public BrowsePage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            debounce.Tick += (s, e) => { debounce.Stop(); Search(); };
        }

        bool IsSearch => args?.LibraryId == null;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Coming back from an item keeps the cached page, results and scroll position.
            if (e.NavigationMode == NavigationMode.Back && feed != null) return;
            args = e.Parameter as BrowseArgs ?? new BrowseArgs();
            if (IsSearch)
            {
                Heading.Text = "Search";
                Query.Visibility = Visibility.Visible;
                Loaded += FocusQuery;
                Show(null);
            }
            else
            {
                Heading.Text = args.Name ?? "Library";
                // The cached page may last have been the search page.
                Query.Visibility = Visibility.Collapsed;
                Show(new CatalogFeed("library_id=" + Uri.EscapeDataString(args.LibraryId) + "&sort=title", this));
            }
        }

        void FocusQuery(object sender, RoutedEventArgs e)
        {
            Loaded -= FocusQuery;
            Query.Focus(FocusState.Keyboard);
        }

        void Query_TextChanged(object sender, TextChangedEventArgs e)
        {
            debounce.Stop();
            debounce.Start();
        }

        void Query_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter && e.Key != VirtualKey.GamepadMenu) return;
            e.Handled = true;
            debounce.Stop();
            Search();
            // Hand focus to the results so the controller can move through them.
            if (Results.Items.Count > 0) Results.Focus(FocusState.Keyboard);
        }

        void Search()
        {
            var q = Query.Text.Trim();
            if (q.Length == 0) { Show(null); return; }
            // video_with_episodes: movies, shows and episodes, no books.
            Show(new CatalogFeed("q=" + Uri.EscapeDataString(q) + "&type=video_with_episodes", this));
        }

        void Show(CatalogFeed next)
        {
            feed = next;
            Message.Text = "";
            Busy.IsActive = next != null;
            Results.ItemsSource = next;
        }

        /// <summary>Called by the feed after each page, on the UI thread.</summary>
        void PageLoaded(CatalogFeed source, string error)
        {
            if (!ReferenceEquals(source, feed)) return;
            Busy.IsActive = false;
            if (error != null) Message.Text = error;
            else if (source.Count == 0) Message.Text = IsSearch ? "No matches." : "This library is empty.";
            else Message.Text = "";
        }

        void Results_ItemClick(object sender, ItemClickEventArgs e)
        {
            var card = (Card)e.ClickedItem;
            Frame.Navigate(typeof(ItemPage), card.ContentId);
        }

        /// <summary>Catalog cards loaded a page at a time as the grid asks for more.</summary>
        sealed class CatalogFeed : ObservableCollection<Card>, ISupportIncrementalLoading
        {
            const int PageSize = 48;
            readonly string query;
            readonly BrowsePage page;
            string cursor;
            bool loading;

            public CatalogFeed(string query, BrowsePage page)
            {
                this.query = query;
                this.page = page;
            }

            public bool HasMoreItems { get; private set; } = true;

            public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) =>
                AsyncInfo.Run(async ct => new LoadMoreItemsResult { Count = await LoadPageAsync() });

            async Task<uint> LoadPageAsync()
            {
                if (loading || !HasMoreItems) return 0;
                loading = true;
                try
                {
                    var path = "/api/v2/catalog?" + query + "&limit=" + PageSize + "&poster_width=300";
                    if (cursor != null) path += "&cursor=" + Uri.EscapeDataString(cursor);
                    var r = await SiloClient.Current.GetAsync(path);
                    var cards = r.Objects("items").Select(i => Card.From(i, false)).Where(c => c.ContentId != null).ToList();
                    foreach (var c in cards) Add(c);
                    cursor = r.Obj("page")?.Str("next_cursor");
                    HasMoreItems = r.Obj("page")?.Bool("has_more") == true && cursor != null;
                    page.PageLoaded(this, null);
                    return (uint)cards.Count;
                }
                catch (ApiException ex)
                {
                    HasMoreItems = false;
                    if (ex.Status == 401) App.Restart(typeof(SignInPage));
                    else page.PageLoaded(this, ex.Message);
                    return 0;
                }
                finally
                {
                    loading = false;
                }
            }
        }
    }
}
