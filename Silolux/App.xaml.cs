using System;
using Silolux.Services;
using Silolux.Views;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Silolux
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            Ui.ApplyResources(Resources);
            FocusVisualKind = FocusVisualKind.Reveal;
            Suspending += OnSuspending;
        }

        public static Frame RootFrame => Window.Current.Content as Frame;

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame { Background = (Brush)Resources["SiloBackgroundBrush"] };
                Window.Current.Content = frame;
                // Gamepad B and the title-bar arrow arrive as BackRequested.
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
                // On a PC, Esc / Alt+Left / Backspace and the mouse back button do not.
                Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKey;
                Window.Current.CoreWindow.PointerPressed += OnPointerPressed;
                // Text fields: A on a controller opens the on-screen keyboard. Xbox does not
                // always do this itself (e.g. after focus was set in code), so ask for it.
                frame.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
                frame.Navigated += (s, a) => UpdateBackButton();
            }
            if (e.PrelaunchActivated) return;
            if (frame.Content == null) frame.Navigate(StartPage());
            Window.Current.Activate();
        }

        /// <summary>The first page for the stored state: server, sign-in, profile, home.</summary>
        public static Type StartPage()
        {
            if (string.IsNullOrEmpty(AppSettings.ServerUrl)) return typeof(ConnectPage);
            SiloClient.Use(AppSettings.ServerUrl);
            if (!SiloClient.Current.SignedIn) return typeof(SignInPage);
            if (string.IsNullOrEmpty(AppSettings.ProfileId)) return typeof(ProfilesPage);
            return typeof(HomePage);
        }

        /// <summary>Replaces the whole back stack, e.g. after sign-in.</summary>
        public static void Restart(Type page, object parameter = null)
        {
            RootFrame.Navigate(page, parameter);
            RootFrame.BackStack.Clear();
            UpdateBackButton();
        }

        static bool TryGoBack()
        {
            var frame = RootFrame;
            if (frame == null || !frame.CanGoBack) return false;
            frame.GoBack();
            return true;
        }

        static void UpdateBackButton()
        {
            var frame = RootFrame;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                frame != null && frame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!e.Handled) e.Handled = TryGoBack();
        }

        void OnAcceleratorKey(CoreDispatcher sender, AcceleratorKeyEventArgs e)
        {
            if (e.EventType != CoreAcceleratorKeyEventType.KeyDown && e.EventType != CoreAcceleratorKeyEventType.SystemKeyDown) return;
            if (e.Handled) return;
            var alt = (Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;
            var focused = FocusManager.GetFocusedElement();
            var typing = focused is TextBox || focused is PasswordBox;
            var back = e.VirtualKey == Windows.System.VirtualKey.Escape
                    || (alt && e.VirtualKey == Windows.System.VirtualKey.Left)
                    || (!typing && e.VirtualKey == Windows.System.VirtualKey.Back);
            if (back && !e.KeyStatus.WasKeyDown) e.Handled = TryGoBack();
        }

        static void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.GamepadA) return;
            var focused = FocusManager.GetFocusedElement();
            if (focused is TextBox || focused is PasswordBox) ShowKeyboard();
        }

        /// <summary>Opens the on-screen keyboard (controller / touch) for the focused text field.</summary>
        public static void ShowKeyboard()
        {
            try { CoreInputView.GetForCurrentView().TryShow(); }
            catch (Exception) { /* no on-screen keyboard on this device */ }
        }

        void OnPointerPressed(CoreWindow sender, PointerEventArgs e)
        {
            var props = e.CurrentPoint.Properties;
            if (props.IsXButton1Pressed) e.Handled = TryGoBack();
        }

        void OnSuspending(object sender, SuspendingEventArgs e)
        {
            e.SuspendingOperation.GetDeferral().Complete();
        }
    }
}
