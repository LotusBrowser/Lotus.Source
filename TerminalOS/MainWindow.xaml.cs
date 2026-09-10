using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TerminalOS
{
    public partial class MainWindow : Window
    {
        private const string HomeUrl = "lotus://newtab/";
        private const string SearchUrl = "https://start.duckduckgo.com?q=";
        private const string NewTabUrl = "lotus://newtab/";
        private const string SettingsUrl = "lotus://settings/";

        // These used to live as CheckBox.IsChecked values on the old app-menu
        // flyout. Now that lotus://settings is a real page rendered into the
        // WebView2 (like lotus://newtab), these plain fields are the source of
        // truth; the settings page reads them when it renders and writes back
        // to them over the WebMessage bridge (see HandleSettingsPageMessage).
        private bool _trackerBlockEnabled = true;
        private bool _httpsOnlyEnabled = true;
        private bool _popupBlockEnabled = true;

        // WebView2's default UA reports itself as "Edg/<version>" alongside the
        // Chrome token, and depending on the installed WebView2 Runtime the
        // Chrome version it reports can lag behind what Google's device/UA
        // checks (e.g. reCAPTCHA) expect from a normal, current browser. Since
        // Lotus is Chromium under the hood anyway, overriding the UA to a
        // plain, current desktop Chrome string makes it present the same way
        // any other Chromium browser would, which avoids Lotus getting flagged
        // as a possible bot/automation client. Bump this string's version
        // number periodically to track a real current Chrome release.
        private const string StandardUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

        private CoreWebView2Environment _normalEnv;
        private string _dataDir;

        private readonly Dictionary<TabItem, WebView2> _browsers = new Dictionary<TabItem, WebView2>();

        private readonly List<BookmarkEntry> _bookmarks = new List<BookmarkEntry>();
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private readonly Stack<string> _closedTabs = new Stack<string>();
        public ObservableCollection<DownloadItem> Downloads { get; } = new ObservableCollection<DownloadItem>();

        private int _blockedCount = 0;
        private TabItem _draggedTab;
        private readonly string _startupUrl;

        private static readonly string[] TrackerDomains =
        {
            "doubleclick.net", "googlesyndication.com", "google-analytics.com",
            "googletagmanager.com", "googletagservices.com", "scorecardresearch.com",
            "adsystem.com", "adnxs.com", "hotjar.com", "facebook.com/tr",
            "connect.facebook.net", "criteo.com", "taboola.com", "outbrain.com"
        };

        public MainWindow(string startupUrl = null)
        {
            InitializeComponent();
            DataContext = this;
            _startupUrl = startupUrl;
            Loaded += async (s, e) => await InitializeAsync();
            PreviewKeyDown += Window_PreviewKeyDown;
            StateChanged += (s, e) => UpdateMaximizeRestoreIcon();

            // Best-effort, cheap, and self-healing (re-writes the registered
            // exe path every launch) - see DefaultBrowserRegistrar for why this
            // can't just flip the OS default on its own.
            DefaultBrowserRegistrar.EnsureRegistered();
        }

        // Called by App.xaml.cs when a second Lotus launch (e.g. the user
        // clicked a link while Lotus is set as their default browser) hands
        // its URL off to this already-running window instead of opening a
        // duplicate one.
        public async void OpenUrlFromExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            await AddNewTab(url);
        }

        // ================================================================
        // Custom title bar
        // ================================================================

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdateMaximizeRestoreIcon()
        {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeRestoreIcon.Data = (Geometry)FindResource(maximized ? "IconWinRestore" : "IconWinMaximize");
            MaximizeRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
        }

        // ================================================================
        // Taskbar-aware maximize
        // ================================================================
        // With WindowStyle="None" (custom chrome), WPF's normal "maximize to the
        // monitor's work area" behavior doesn't kick in on its own - the window
        // just grows to the full monitor bounds and ends up drawing over the
        // taskbar. Handling WM_GETMINMAXINFO ourselves and clamping to the
        // correct monitor's work area (via GetMonitorInfo) fixes that, including
        // on multi-monitor setups.

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            {
                hwndSource.AddHook(WindowProc);
            }
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                ApplyWorkAreaToMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ApplyWorkAreaToMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));

            const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref monitorInfo);

                RECT workArea = monitorInfo.rcWork;
                RECT monitorArea = monitorInfo.rcMonitor;

                // Offset/size relative to the monitor, so the maximized window fills
                // the work area (i.e. stops short of the taskbar) rather than the
                // full monitor bounds.
                mmi.ptMaxPosition.x = workArea.left - monitorArea.left;
                mmi.ptMaxPosition.y = workArea.top - monitorArea.top;
                mmi.ptMaxSize.x = workArea.right - workArea.left;
                mmi.ptMaxSize.y = workArea.bottom - workArea.top;
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ================================================================
        // Tab strip sizing (shrink tabs to fit, Edge/Chrome-style)
        // ================================================================

        private const double MaxTabWidth = 220;

        // Smallest a tab is ever assigned - just enough room for the close
        // button (20px + margins) plus a sliver for the favicon. Below this,
        // tabs are left at this width and simply overlap each other rather
        // than shrinking further, so the close button never has to compete
        // with the title for space.
        private const double MinTabWidth = 30;

        private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidths();

        // Recomputes an even per-tab width that fits whatever room is left in the
        // title bar after the brand mark, new-tab button, app menu, and window
        // caption buttons. Tabs shrink together as more are opened, down to
        // MinTabWidth - past that point they hold at MinTabWidth and the strip
        // simply grows wider than the available space rather than crushing the
        // close button out of existence.
        private void UpdateTabWidths()
        {
            int count = BrowserTabs.Items.Count;
            if (count == 0 || TitleBar.ActualWidth <= 0) return;

            double reserved = 32 /* brand mark */
                              + NewTabButton.ActualWidth + 4
                              + MinimizeButton.ActualWidth
                              + MaximizeRestoreButton.ActualWidth
                              + CloseWindowButton.ActualWidth
                              + 20 /* always-available drag margin */;

            double available = Math.Max(0, TitleBar.ActualWidth - reserved);
            double perTab = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, available / count));

            foreach (var item in BrowserTabs.Items)
            {
                if (item is TabItem tab) tab.Width = perTab;
            }
        }

        private async Task InitializeAsync()
        {
            _dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LotusBrowser");
            Directory.CreateDirectory(_dataDir);

            LoadBookmarks();
            LoadHistory();
            RefreshBookmarksUI();

            _normalEnv = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _dataDir,
                options: CreateEnvironmentOptions());
            await AddNewTab(_startupUrl);
        }

        // Registers the "lotus" URI scheme (lotus://newtab/, etc.) so the browser can
        // navigate to and serve its own internal pages as real, addressable documents
        // instead of faking them with NavigateToString.
        private static CoreWebView2EnvironmentOptions CreateEnvironmentOptions()
        {
            var lotusScheme = new CoreWebView2CustomSchemeRegistration("lotus")
            {
                TreatAsSecure = true,
                HasAuthorityComponent = true
            };

            // Some WebView2 runtime/SDK combinations return null from the
            // CustomSchemeRegistrations property getter instead of a lazily-created
            // list, which throws on .Add(). Passing the list through the constructor
            // sidesteps that entirely.
            return new CoreWebView2EnvironmentOptions(
                additionalBrowserArguments: null,
                language: null,
                targetCompatibleBrowserVersion: null,
                allowSingleSignOnUsingOSPrimaryAccount: false,
                customSchemeRegistrations: new List<CoreWebView2CustomSchemeRegistration> { lotusScheme });
        }

        // ================================================================
        // Persistence
        // ================================================================

        private string BookmarksPath => Path.Combine(_dataDir, "bookmarks.json");
        private string HistoryPath => Path.Combine(_dataDir, "history.json");

        private void LoadBookmarks()
        {
            try
            {
                if (File.Exists(BookmarksPath))
                {
                    var items = JsonSerializer.Deserialize<List<BookmarkEntry>>(File.ReadAllText(BookmarksPath));
                    if (items != null) _bookmarks.AddRange(items);
                }
            }
            catch { }
        }

        private void SaveBookmarks()
        {
            try { File.WriteAllText(BookmarksPath, JsonSerializer.Serialize(_bookmarks)); } catch { }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath));
                    if (items != null) _history.AddRange(items);
                }
            }
            catch { }
        }

        private void SaveHistory()
        {
            try
            {
                var trimmed = _history.OrderByDescending(h => h.VisitedAt).Take(5000).ToList();
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(trimmed));
            }
            catch { }
        }

        // ================================================================
        // Tab management
        // ================================================================

        private async void NewTab_Click(object sender, RoutedEventArgs e) { await AddNewTab(null); }

        private async Task AddNewTab(string url)
        {
            var browser = new WebView2();

            // Start the tab in its loading state so the spinner is what the user
            // sees the instant the tab is created - before any NavigationStarting
            // event has had a chance to fire from the WebView2 below.
            var header = new TabHeaderInfo { Title = "New Tab", IsLoading = true };
            var tab = new TabItem { Header = header };

            _browsers[tab] = browser;
            BrowserTabs.Items.Add(tab);
            BrowserTabs.SelectedItem = tab;
            UpdateTabWidths();

            await browser.EnsureCoreWebView2Async(_normalEnv);

            // Present as a normal desktop Chrome browser rather than WebView2's
            // default UA, so Google's bot/device checks (reCAPTCHA in particular)
            // treat Lotus like any other Chromium browser instead of flagging it.
            // Must be set before the first Navigate call below.
            browser.CoreWebView2.Settings.UserAgent = StandardUserAgent;

            // WebView2 paints an opaque white background by default, which stays visible
            // before a page's own CSS background paints over it. Making it transparent
            // lets the page's own background - including dark themes - be what's shown.
            browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            ConfigureSecurity(browser, tab, header);

            browser.CoreWebView2.SourceChanged += (s, e) =>
            {
                if (BrowserTabs.SelectedItem == tab)
                {
                    UpdateAddressBar();
                    UpdateNavButtons();
                    UpdateSecurityIcon(browser);
                    UpdateBookmarkStar();
                }
            };

            browser.CoreWebView2.Navigate(string.IsNullOrEmpty(url) ? NewTabUrl : url);
        }

        private void ConfigureSecurity(WebView2 browser, TabItem tab, TabHeaderInfo header)
        {
            var core = browser.CoreWebView2;

            core.DocumentTitleChanged += (s, e) =>
            {
                var title = core.DocumentTitle;
                header.Title = string.IsNullOrWhiteSpace(title) ? "New Tab" : title;
            };

            core.FaviconChanged += async (s, e) =>
            {
                try
                {
                    using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                    if (stream == null) { header.Favicon = null; return; }
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    header.Favicon = bmp;
                }
                catch { header.Favicon = null; }
            };

            core.NavigationStarting += (s, e) =>
            {
                // Flip the tab into its loading state: the favicon slot will show
                // the Lotus spinner. Clear any previous favicon too, so that if the
                // new page turns out to have no favicon at all, we fall through to
                // the fallback globe rather than briefly redisplaying the old page's
                // icon while we're still deciding.
                header.IsLoading = true;
                header.Favicon = null;
                UpdateSecurityIcon(browser);
            };

            core.NavigationCompleted += (s, e) =>
            {
                // Page (or navigation attempt) is done - drop the spinner. The
                // favicon slot will now resolve to either the real page favicon
                // (if FaviconChanged delivered one) or the fallback globe.
                header.IsLoading = false;

                if (e.IsSuccess && core.Source != null && !core.Source.StartsWith("data:"))
                {
                    var entry = new HistoryEntry
                    {
                        Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle,
                        Url = core.Source,
                        VisitedAt = DateTime.Now
                    };
                    _history.Insert(0, entry);
                    SaveHistory();
                }
            };

            core.NewWindowRequested += (s, e) =>
            {
                if (_popupBlockEnabled)
                {
                    e.Handled = true;
                }
                else
                {
                    e.Handled = true;
                    _ = AddNewTab(e.Uri);
                }
            };

            core.PermissionRequested += (s, e) =>
            {
                switch (e.PermissionKind)
                {
                    case CoreWebView2PermissionKind.Camera:
                    case CoreWebView2PermissionKind.Microphone:
                    case CoreWebView2PermissionKind.Geolocation:
                        var result = MessageBox.Show(
                            $"{e.Uri} is requesting {e.PermissionKind}. Allow?",
                            "Permission Request", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        e.State = result == MessageBoxResult.Yes
                            ? CoreWebView2PermissionState.Allow
                            : CoreWebView2PermissionState.Deny;
                        break;
                    default:
                        e.State = CoreWebView2PermissionState.Allow;
                        break;
                }
            };

            // Messages posted by lotus:// pages via window.chrome.webview.postMessage
            // (currently just the settings page's toggles/buttons). e.Source is the
            // origin of the frame that actually sent the message, so a regular web
            // page can't spoof one of these by posting the same JSON shape - only
            // content we served ourselves on the lotus:// scheme can reach this.
            core.WebMessageReceived += (s, e) =>
            {
                if (e.Source?.StartsWith("lotus://", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string raw;
                    try { raw = e.TryGetWebMessageAsString(); }
                    catch { return; }
                    HandleSettingsPageMessage(raw);
                }
            };

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) =>
            {
                var url = e.Request.Uri;

                if (url.StartsWith("lotus://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = BuildLotusResponse(core, url);
                    return;
                }

                if (_trackerBlockEnabled && TrackerDomains.Any(d => url.Contains(d)))
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                    _blockedCount++;
                    return;
                }

                if (_httpsOnlyEnabled && url.StartsWith("http://") && !IsLocalOrDirectIpHost(url))
                {
                    if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                    {
                        var upgraded = "https://" + url.Substring("http://".Length);
                        e.Response = core.Environment.CreateWebResourceResponse(
                            null, 307, "Temporary Redirect", $"Location: {upgraded}\r\n");
                    }
                }
            };

            core.DownloadStarting += (s, e) =>
            {
                e.Handled = true; // suppress default WebView2 download popup, use our shelf instead

                var item = new DownloadItem
                {
                    FileName = Path.GetFileName(e.ResultFilePath),
                    FilePath = e.ResultFilePath,
                    IsInProgress = true,
                    StatusText = "Starting…"
                };

                var op = e.DownloadOperation;

                Dispatcher.Invoke(() =>
                {
                    Downloads.Insert(0, item);
                    DownloadShelf.Visibility = Visibility.Visible;
                });

                op.BytesReceivedChanged += (s2, e2) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var total = op.TotalBytesToReceive ?? 0;
                        item.Progress = total > 0 ? (double)op.BytesReceived * 100.0 / total : 0;
                        item.StatusText = FormatBytes((ulong)op.BytesReceived) +
                            (total > 0 ? $" of {FormatBytes((ulong)total)}" : "");
                    });
                };

                op.StateChanged += (s2, e2) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (op.State == CoreWebView2DownloadState.Completed)
                        {
                            item.IsInProgress = false;
                            item.Progress = 100;
                            item.StatusText = "Done";
                        }
                        else if (op.State == CoreWebView2DownloadState.Interrupted)
                        {
                            item.IsInProgress = false;
                            item.StatusText = "Failed";
                        }
                    });
                };
            };

            // Let our own shortcuts (Ctrl+F, Ctrl+T, Ctrl+P, etc.) win instead of
            // Chromium's built-in accelerator handling, so they bubble up to
            // Window_PreviewKeyDown even while focus is inside the page.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        }

        // Sites reached by "localhost", "127.0.0.1", a bare LAN/direct IP, or a
        // hostname with no dot (e.g. a dev box or router at "http://router/")
        // very often only ever speak plain HTTP - there's no public cert for
        // them to have HTTPS in the first place. Forcing the HTTPS-only upgrade
        // on those just breaks the page, so they're excluded from it. Regular
        // public domains (e.g. http://example.com) are still upgraded as before.
        private static bool IsLocalOrDirectIpHost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            var host = uri.Host;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(host, out var ip)) return true; // IPv4/IPv6 literal
            if (uri.IsLoopback) return true;
            if (!host.Contains('.')) return true; // single-label hostnames (dev/LAN boxes)

            return false;
        }

        private static string FormatBytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double b = bytes;
            int i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {units[i]}";
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TabItem tab)
            {
                CloseTab(tab);
            }
        }

        private void CloseTab(TabItem tab)
        {
            if (!_browsers.ContainsKey(tab)) return;

            var browser = _browsers[tab];
            var url = browser.CoreWebView2?.Source;

            if (!string.IsNullOrEmpty(url))
            {
                _closedTabs.Push(url);
            }

            browser.Dispose();
            _browsers.Remove(tab);
            BrowserTabs.Items.Remove(tab);
            UpdateTabWidths();

            if (BrowserTabs.Items.Count == 0)
            {
                Close();
            }
        }

        private async void ReopenClosedTab_Click(object sender, RoutedEventArgs e)
        {
            await ReopenClosedTab();
        }

        private async Task ReopenClosedTab()
        {
            if (_closedTabs.Count == 0) return;
            var url = _closedTabs.Pop();
            await AddNewTab(url);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            SaveBookmarks();
            SaveHistory();
        }

        private void BrowserTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowActiveBrowser();
            UpdateAddressBar();
            UpdateNavButtons();
            UpdateBookmarkStar();
            if (CurrentBrowser != null) UpdateSecurityIcon(CurrentBrowser);
        }

        private void ShowActiveBrowser()
        {
            ContentHost.Children.Clear();
            if (BrowserTabs.SelectedItem is TabItem tab && _browsers.TryGetValue(tab, out var browser))
            {
                ContentHost.Children.Add(browser);
            }
        }

        // ================================================================
        // Tab drag-to-reorder
        // ================================================================

        private Point _dragStart;

        private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            if ((sender as FrameworkElement)?.TemplatedParent is TabItem tab)
            {
                _draggedTab = tab;
            }
        }

        private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedTab == null) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < 10) return;

            DragDrop.DoDragDrop((DependencyObject)sender, _draggedTab, DragDropEffects.Move);
        }

        private void Tab_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(typeof(TabItem)) is TabItem source)) return;
            if (!((sender as FrameworkElement)?.TemplatedParent is TabItem target)) return;
            if (source == target) return;

            int sourceIndex = BrowserTabs.Items.IndexOf(source);
            int targetIndex = BrowserTabs.Items.IndexOf(target);
            if (sourceIndex < 0 || targetIndex < 0) return;

            BrowserTabs.Items.Remove(source);
            BrowserTabs.Items.Insert(targetIndex, source);
            BrowserTabs.SelectedItem = source;
            _draggedTab = null;
        }

        // ================================================================
        // Navigation
        // ================================================================

        private WebView2 CurrentBrowser =>
            BrowserTabs.SelectedItem is TabItem tab && _browsers.TryGetValue(tab, out var b) ? b : null;

        private TabItem CurrentTab => BrowserTabs.SelectedItem as TabItem;

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentBrowser?.CoreWebView2?.CanGoBack == true) CurrentBrowser.CoreWebView2.GoBack();
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentBrowser?.CoreWebView2?.CanGoForward == true) CurrentBrowser.CoreWebView2.GoForward();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => CurrentBrowser?.CoreWebView2?.Reload();

        private void Home_Click(object sender, RoutedEventArgs e) => CurrentBrowser?.CoreWebView2?.Navigate(HomeUrl);

        private void AddressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) NavigateToAddressBar();
        }

        private void NavigateToAddressBar()
        {
            var input = AddressBar.Text.Trim();
            if (string.IsNullOrEmpty(input) || CurrentBrowser?.CoreWebView2 == null) return;

            if (input.Equals("lotus:newtab", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("lotus://newtab", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("lotus://newtab/", StringComparison.OrdinalIgnoreCase))
            {
                input = NewTabUrl;
            }
            else if (IsLotusSettingsUrl(input))
            {
                input = SettingsUrl;
            }
            else if (!input.StartsWith("http://") && !input.StartsWith("https://") &&
                !input.StartsWith("view-source:") && !input.StartsWith("data:") &&
                !input.StartsWith("lotus://", StringComparison.OrdinalIgnoreCase))
            {
                input = (input.Contains(".") && !input.Contains(" "))
                    ? "https://" + input
                    : SearchUrl + Uri.EscapeDataString(input);
            }

            CurrentBrowser.CoreWebView2.Navigate(input);
        }

        // ================================================================
        // Lotus-scheme pages (lotus://newtab/, lotus://settings/)
        // ================================================================

        // Handles every request on the "lotus" scheme. lotus://newtab/ and
        // lotus://settings/ resolve to real pages; anything else on the
        // scheme comes back as a 404, same as a normal web server would.
        private CoreWebView2WebResourceResponse BuildLotusResponse(CoreWebView2 core, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLotusPageRoot(uri))
            {
                string html = null;
                if (string.Equals(uri.Host, "newtab", StringComparison.OrdinalIgnoreCase))
                {
                    html = BuildNewTabPageHtml();
                }
                else if (string.Equals(uri.Host, "settings", StringComparison.OrdinalIgnoreCase))
                {
                    html = BuildSettingsPageHtml();
                }

                if (html != null)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                    return core.Environment.CreateWebResourceResponse(
                        new MemoryStream(bytes), 200, "OK", "Content-Type: text/html; charset=utf-8");
                }
            }

            return core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }

        private static bool IsLotusPageRoot(Uri uri) => uri.AbsolutePath == "/" || uri.AbsolutePath.Length == 0;

        // The real lotus flower mark, reused as the on-page logo on every lotus://
        // page and (via data URI) as each page's real favicon, so it flows through
        // the same GetFaviconAsync pipeline that every other site's icon goes through.
        // Fills are expressed as rgb() rather than hex so the '#' character never has to
        // survive being embedded in a data: URI (a literal '#' would truncate the URL).
        private static string LotusSvg(int size) => $@"<svg width='{size}' height='{size}' viewBox='0 0 512 512' xmlns='http://www.w3.org/2000/svg'>
                <path style='fill:rgb(255,130,140);' d='M189.37,375.172c-2.521,5.258-3.991,10.68-3.991,16.26c0,25.565,27.584,48.348,70.621,63.188c43.037-14.84,70.621-37.623,70.621-63.188c0-5.4-1.389-10.651-3.756-15.75l-66.869-35.821L189.37,375.172z'/>
                <path style='fill:rgb(255,150,160);' d='M384,286.897H128c-51.787,0-97.939,11.86-128,44.138C30.061,363.311,76.213,384,128,384s97.939-11.861,128-44.138C286.061,372.139,332.213,384,384,384s97.939-20.689,128-52.966C481.94,298.756,435.788,286.897,384,286.897z'/>
                <path style='fill:rgb(255,166,177);' d='M256,57.379c-37.657,33.171-61.793,84.096-61.793,141.241c0,13.591,1.405,26.815,3.993,39.548h115.601c2.588-12.733,3.992-25.955,3.992-39.548C317.793,141.476,293.657,90.55,256,57.379z'/>
                <path style='fill:rgb(255,166,177);' d='M194.207,216.276c-39.988-32.025-95.218-53.686-141.213-52.941c-0.905,42.244,19.834,87.86,60.902,123.57S207.42,340.651,256,339.864'/>
                <path style='fill:rgb(255,166,177);' d='M256,339.864c48.58,0.787,101.037-17.248,142.104-52.959s61.806-81.325,60.902-123.57c-45.364-0.735-101.518,21.783-141.213,52.94'/>
                <path style='fill:rgb(255,150,160);' d='M105.952,216.294c-0.669,29.572,14.66,61.505,45.014,86.505c30.354,24.999,69.127,37.624,105.034,37.074l-52.965-88.286C173.479,229.166,139.948,215.772,105.952,216.294z'/>
                <path style='fill:rgb(255,150,160);' d='M308.966,251.586L256,339.871c35.907,0.552,74.68-12.074,105.034-37.074c30.354-24.999,45.683-56.931,45.014-86.505C372.519,215.779,338.305,229.775,308.966,251.586z'/>
                <path style='fill:rgb(255,130,140);' d='M194.207,260.414c0,32.143,24.136,60.79,61.793,79.448c37.657-18.658,61.793-47.305,61.793-79.448s-24.136-60.79-61.793-79.448C218.343,199.624,194.207,228.27,194.207,260.414z'/>
            </svg>";

        private static string LotusFaviconDataUri() => string.Join(" ",
            LotusSvg(64).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

        private string BuildNewTabPageHtml()
        {
            var tiles = _bookmarks.Take(8).Select(b =>
                $"<a class='tile' href='{System.Security.SecurityElement.Escape(b.Url)}'>" +
                $"<div class='dot'></div><span>{System.Security.SecurityElement.Escape(b.Title)}</span></a>");

            return $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>New Tab</title>
                <link rel='icon' type='image/svg+xml' href=""data:image/svg+xml,{LotusFaviconDataUri()}"">
                <style>
                body {{ background:#F6D9DF; font-family:'Segoe UI',sans-serif; display:flex; flex-direction:column;
                        align-items:center; padding-top:110px; margin:0; }}
                .logo {{ display:flex; align-items:center; gap:14px; margin-bottom:32px; }}
                .logo span {{ font-size:42px; font-weight:600; color:#8C3A4D; letter-spacing:.5px; }}
                form {{ width:480px; }}
                input {{ width:100%; height:44px; border-radius:22px; border:1px solid #F3D3D9;
                         padding:0 20px; font-size:15px; outline:none; box-shadow:0 1px 4px rgba(140,56,77,.12); background:#FBE4E9;}}
                .tiles {{ display:flex; gap:20px; margin-top:48px; flex-wrap:wrap; justify-content:center; max-width:520px; }}
                .tile {{ display:flex; flex-direction:column; align-items:center; text-decoration:none; width:64px; }}
                .dot {{ width:40px; height:40px; border-radius:50%; background:#F0BFC9; margin-bottom:6px; }}
                .tile span {{ font-size:11px; color:#5C3944; text-align:center; white-space:nowrap;
                              overflow:hidden; text-overflow:ellipsis; width:64px; }}
                .settings-btn {{ position:fixed; top:18px; right:18px; width:38px; height:38px; border-radius:50%;
                                  display:flex; align-items:center; justify-content:center;
                                  background:#FBE4E9; box-shadow:0 1px 4px rgba(140,56,77,.12);
                                  text-decoration:none; }}
                .settings-btn:hover {{ background:#F0BFC9; }}
                </style></head><body>
                <a class='settings-btn' href='{SettingsUrl}' title='Settings'>
                    <svg width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='#8C3A4D' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'>
                        <circle cx='12' cy='12' r='3'></circle>
                        <path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'></path>
                    </svg>
                </a>
                <div class='logo'>{LotusSvg(48)}<span>Lotus</span></div>
                <form onsubmit=""window.location.href='{SearchUrl}'+encodeURIComponent(document.getElementById('q').value);return false;"">
                    <input id='q' autofocus placeholder='Search the web'/>
                </form>
                <div class='tiles'>{string.Join("", tiles)}</div>
                </body></html>";
        }

        // lotus://settings/ - a real page rendered the same way lotus://newtab/ is,
        // sharing its color layout (background, card, and accent colors, logo mark,
        // typography). Toggles/buttons post JSON messages back to the host over
        // window.chrome.webview.postMessage; see HandleSettingsPageMessage.
        private string BuildSettingsPageHtml()
        {
            string Toggle(string action, string label, bool isOn) => $@"
                <label class='toggle-row'>
                    <span>{System.Security.SecurityElement.Escape(label)}</span>
                    <input type='checkbox' {(isOn ? "checked" : "")}
                           onchange=""post('{action}', this.checked)"">
                </label>";

            bool showBookmarksBar = BookmarksBar.Visibility == Visibility.Visible;
            bool isDefaultBrowser = DefaultBrowserRegistrar.IsCurrentDefault();

            string defaultBrowserSection = isDefaultBrowser
                ? "<p class='muted'>Lotus is your default browser.</p>"
                : @"<p class='muted'>Lotus is not currently your default browser.</p>
                   <button class='accent-btn' onclick=""post('setDefaultBrowser')"">Make Lotus my default browser</button>";

            return $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Settings</title>
                <link rel='icon' type='image/svg+xml' href=""data:image/svg+xml,{LotusFaviconDataUri()}"">
                <style>
                body {{ background:#F6D9DF; font-family:'Segoe UI',sans-serif; display:flex; flex-direction:column;
                        align-items:center; padding:70px 20px 60px; margin:0; }}
                .logo {{ display:flex; align-items:center; gap:14px; margin-bottom:36px; }}
                .logo span {{ font-size:32px; font-weight:600; color:#8C3A4D; letter-spacing:.5px; }}
                .page {{ width:100%; max-width:520px; }}
                .card {{ background:#FBE4E9; border-radius:14px; padding:20px 22px; margin-bottom:20px;
                         box-shadow:0 1px 4px rgba(140,56,77,.12); }}
                .card h2 {{ margin:0 0 14px; font-size:14px; font-weight:600; color:#8C3A4D;
                            text-transform:uppercase; letter-spacing:.5px; }}
                .toggle-row {{ display:flex; align-items:center; justify-content:space-between;
                               padding:9px 0; font-size:14px; color:#3A1F27; cursor:pointer; }}
                .toggle-row input {{ accent-color:#FF6F82; width:18px; height:18px; cursor:pointer; }}
                .muted {{ font-size:12.5px; color:#8C6570; margin:2px 0 14px; }}
                .accent-btn {{ font-family:inherit; font-size:13px; border:none; border-radius:18px;
                               padding:9px 18px; background:#FF6F82; color:#FFF6F8; cursor:pointer; }}
                .accent-btn:hover {{ background:#E85A70; }}
                </style></head><body>
                <div class='logo'>{LotusSvg(40)}<span>Settings</span></div>
                <div class='page'>

                    <div class='card'>
                        <h2>Browsing</h2>
                        {Toggle("setShowBookmarksBar", "Show bookmarks bar", showBookmarksBar)}
                    </div>

                    <div class='card'>
                        <h2>Privacy &amp; Security</h2>
                        {Toggle("setTrackerBlock", "Block trackers & ad scripts", _trackerBlockEnabled)}
                        {Toggle("setHttpsOnly", "HTTPS-only mode", _httpsOnlyEnabled)}
                        {Toggle("setPopupBlock", "Block pop-ups", _popupBlockEnabled)}
                        <p class='muted' style='margin-top:14px;'>{_blockedCount} trackers blocked this session</p>
                        <button class='accent-btn' onclick=""post('clearBrowsingData')"">Clear browsing data</button>
                    </div>

                    <div class='card'>
                        <h2>Default browser</h2>
                        {defaultBrowserSection}
                    </div>

                </div>
                <script>
                function post(action, value) {{
                    window.chrome.webview.postMessage(JSON.stringify({{ action: action, value: !!value }}));
                }}
                </script>
                </body></html>";
        }

        // ================================================================
        // Bookmarks
        // ================================================================

        private void RefreshBookmarksUI()
        {
            BookmarksList.ItemsSource = null;
            BookmarksList.ItemsSource = _bookmarks;
        }

        private bool IsBookmarked(string url) => !string.IsNullOrEmpty(url) && _bookmarks.Any(b => b.Url == url);

        private void UpdateBookmarkStar()
        {
            var url = CurrentBrowser?.CoreWebView2?.Source;
            bool marked = IsBookmarked(url);
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(marked ? "#FF6F82" : "#8C6570"));
            BookmarkStar.Stroke = brush;
            BookmarkStar.Fill = marked ? brush : Brushes.Transparent;
        }

        private void ToggleBookmarkCurrent()
        {
            var url = CurrentBrowser?.CoreWebView2?.Source;
            if (string.IsNullOrEmpty(url)) return;

            var existing = _bookmarks.FirstOrDefault(b => b.Url == url);
            if (existing != null)
            {
                _bookmarks.Remove(existing);
            }
            else
            {
                var title = CurrentBrowser.CoreWebView2.DocumentTitle;
                _bookmarks.Insert(0, new BookmarkEntry
                {
                    Title = string.IsNullOrWhiteSpace(title) ? url : title,
                    Url = url
                });
            }

            SaveBookmarks();
            RefreshBookmarksUI();
            UpdateBookmarkStar();
        }

        private void BookmarkStar_Click(object sender, MouseButtonEventArgs e) => ToggleBookmarkCurrent();

        private void BookmarkChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                CurrentBrowser?.CoreWebView2?.Navigate(url);
            }
        }

        private void SetShowBookmarksBar(bool show)
        {
            BookmarksBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================================================================
        // History
        // ================================================================

        private void ToggleHistory_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                HistoryPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                HistoryList.ItemsSource = null;
                HistoryList.ItemsSource = _history;
                HistoryPanel.Visibility = Visibility.Visible;
            }
        }

        private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HistoryList.SelectedItem is HistoryEntry entry)
            {
                CurrentBrowser?.CoreWebView2?.Navigate(entry.Url);
                HistoryPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            SaveHistory();
            HistoryList.ItemsSource = null;
            HistoryList.ItemsSource = _history;
        }

        // ================================================================
        // Downloads
        // ================================================================

        private void ToggleDownloads_Click(object sender, RoutedEventArgs e)
        {
            DownloadShelf.Visibility = DownloadShelf.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void DownloadItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is DownloadItem item && File.Exists(item.FilePath))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FilePath}\""); } catch { }
            }
        }

        // ================================================================
        // Find in page
        // ================================================================

        private void ShowFindBar_Click(object sender, RoutedEventArgs e)
        {
            OpenFindBar();
        }

        private void OpenFindBar()
        {
            FindBar.Visibility = Visibility.Visible;
            FindBox.Focus();
            FindBox.SelectAll();
        }

        private void FindClose_Click(object sender, RoutedEventArgs e)
        {
            FindBar.Visibility = Visibility.Collapsed;
            _ = CurrentBrowser?.CoreWebView2?.ExecuteScriptAsync("window.getSelection().removeAllRanges();");
        }

        private void FindBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) DoFind(Keyboard.Modifiers == ModifierKeys.Shift);
            else if (e.Key == Key.Escape) FindClose_Click(sender, e);
        }

        private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => DoFind(false);

        private void FindNext_Click(object sender, RoutedEventArgs e) => DoFind(false);
        private void FindPrev_Click(object sender, RoutedEventArgs e) => DoFind(true);

        private async void DoFind(bool backwards)
        {
            if (CurrentBrowser?.CoreWebView2 == null) return;
            var query = FindBox.Text.Replace("\\", "\\\\").Replace("\"", "\\\"");
            if (string.IsNullOrEmpty(query)) return;
            var back = backwards ? "true" : "false";
            await CurrentBrowser.CoreWebView2.ExecuteScriptAsync(
                $"window.find(\"{query}\", false, {back}, true, false, true, false)");
        }

        // ================================================================
        // Zoom
        // ================================================================
        // (Zoom is per-tab and no longer has a menu UI of its own - Ctrl +/-/0
        // still drive it via SetZoom, see HandleShortcut below.)

        private void SetZoom(double factor)
        {
            if (CurrentBrowser == null) return;
            factor = Math.Max(0.25, Math.Min(3.0, factor));
            CurrentBrowser.ZoomFactor = factor;
        }

        // ================================================================
        // Misc actions
        // ================================================================

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            CurrentBrowser?.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }

        private void ViewSource_Click(object sender, RoutedEventArgs e)
        {
            var url = CurrentBrowser?.CoreWebView2?.Source;
            if (!string.IsNullOrEmpty(url)) CurrentBrowser.CoreWebView2.Navigate("view-source:" + url);
        }

        private void DevTools_Click(object sender, RoutedEventArgs e)
        {
            CurrentBrowser?.CoreWebView2?.OpenDevToolsWindow();
        }

        // ================================================================
        // Settings page (lotus://settings/)
        // ================================================================

        // Recognizes lotus://settings (with or without a trailing slash) and the
        // lotus:settings shorthand, mirroring how lotus://newtab/ is matched below.
        private static bool IsLotusSettingsUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Equals("lotus:settings", StringComparison.OrdinalIgnoreCase) ||
                   url.Equals("lotus://settings", StringComparison.OrdinalIgnoreCase) ||
                   url.Equals("lotus://settings/", StringComparison.OrdinalIgnoreCase);
        }

        // The settings page's toggles and buttons talk back to the host over
        // window.chrome.webview.postMessage, as a small JSON envelope:
        // {"action":"setTrackerBlock","value":true}. See BuildSettingsPageHtml
        // for the page that sends these.
        private sealed class SettingsPageMessage
        {
            public string Action { get; set; }
            public bool Value { get; set; }
        }

        private void HandleSettingsPageMessage(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;

            SettingsPageMessage msg;
            try
            {
                msg = JsonSerializer.Deserialize<SettingsPageMessage>(rawJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return; }

            if (msg?.Action == null) return;

            switch (msg.Action)
            {
                case "setTrackerBlock": _trackerBlockEnabled = msg.Value; break;
                case "setHttpsOnly": _httpsOnlyEnabled = msg.Value; break;
                case "setPopupBlock": _popupBlockEnabled = msg.Value; break;
                case "setShowBookmarksBar": SetShowBookmarksBar(msg.Value); break;
                case "clearBrowsingData": _ = ClearBrowsingDataAsync(); break;
                case "setDefaultBrowser":
                    // Registration already ran at construction time, so Lotus is
                    // guaranteed to be in the list Windows shows here. Windows itself
                    // requires the user to make the final pick - no way around that
                    // by design, on any app.
                    DefaultBrowserRegistrar.OpenDefaultAppsSettings();
                    break;
            }
        }

        private async Task ClearBrowsingDataAsync()
        {
            if (CurrentBrowser?.CoreWebView2?.Profile == null) return;

            await CurrentBrowser.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllSite |
                CoreWebView2BrowsingDataKinds.CacheStorage |
                CoreWebView2BrowsingDataKinds.BrowsingHistory |
                CoreWebView2BrowsingDataKinds.Cookies);

            MessageBox.Show("Browsing data cleared.", "Lotus",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ================================================================
        // Keyboard shortcuts
        // ================================================================

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleShortcut(e.Key, Keyboard.Modifiers))
            {
                e.Handled = true;
            }
            await Task.CompletedTask;
        }

        private bool HandleShortcut(Key key, ModifierKeys mods)
        {
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;

            if (ctrl && shift && key == Key.T) { _ = ReopenClosedTab(); return true; }
            if (ctrl && shift && key == Key.I) { CurrentBrowser?.CoreWebView2?.OpenDevToolsWindow(); return true; }

            if (ctrl && key == Key.T) { _ = AddNewTab(null); return true; }
            if (ctrl && key == Key.W) { if (CurrentTab != null) CloseTab(CurrentTab); return true; }
            if (ctrl && key == Key.L) { AddressBar.Focus(); AddressBar.SelectAll(); return true; }
            if (ctrl && (key == Key.R || key == Key.F5)) { CurrentBrowser?.CoreWebView2?.Reload(); return true; }
            if (key == Key.F5) { CurrentBrowser?.CoreWebView2?.Reload(); return true; }
            if (ctrl && key == Key.D) { ToggleBookmarkCurrent(); return true; }
            if (ctrl && key == Key.H) { ToggleHistory_Click(null, null); return true; }
            if (ctrl && key == Key.J) { ToggleDownloads_Click(null, null); return true; }
            if (ctrl && key == Key.F) { OpenFindBar(); return true; }
            if (ctrl && key == Key.P) { CurrentBrowser?.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); return true; }
            if (ctrl && key == Key.U) { ViewSource_Click(null, null); return true; }

            if (ctrl && (key == Key.OemPlus || key == Key.Add)) { SetZoom((CurrentBrowser?.ZoomFactor ?? 1) + 0.1); return true; }
            if (ctrl && (key == Key.OemMinus || key == Key.Subtract)) { SetZoom((CurrentBrowser?.ZoomFactor ?? 1) - 0.1); return true; }
            if (ctrl && key == Key.D0) { SetZoom(1.0); return true; }

            if (ctrl && key == Key.Tab && !shift) { SwitchTabRelative(1); return true; }
            if (ctrl && key == Key.Tab && shift) { SwitchTabRelative(-1); return true; }

            if (ctrl && key >= Key.D1 && key <= Key.D9)
            {
                int index = key - Key.D1;
                SwitchToTabIndex(index);
                return true;
            }

            if (alt && key == Key.Left) { Back_Click(null, null); return true; }
            if (alt && key == Key.Right) { Forward_Click(null, null); return true; }

            if (key == Key.Escape)
            {
                if (FindBar.Visibility == Visibility.Visible) { FindClose_Click(null, null); return true; }
            }

            return false;
        }

        private void SwitchTabRelative(int direction)
        {
            if (BrowserTabs.Items.Count == 0) return;
            int index = BrowserTabs.SelectedIndex + direction;
            if (index < 0) index = BrowserTabs.Items.Count - 1;
            if (index >= BrowserTabs.Items.Count) index = 0;
            BrowserTabs.SelectedIndex = index;
        }

        private void SwitchToTabIndex(int index)
        {
            if (index >= 0 && index < BrowserTabs.Items.Count) BrowserTabs.SelectedIndex = index;
        }

        // ================================================================
        // UI sync
        // ================================================================

        private void UpdateAddressBar()
        {
            AddressBar.Text = CurrentBrowser?.CoreWebView2?.Source ?? string.Empty;
        }

        private void UpdateNavButtons()
        {
            BackButton.IsEnabled = CurrentBrowser?.CoreWebView2?.CanGoBack == true;
            ForwardButton.IsEnabled = CurrentBrowser?.CoreWebView2?.CanGoForward == true;
        }

        private void UpdateSecurityIcon(WebView2 browser)
        {
            var source = browser?.CoreWebView2?.Source ?? "";
            bool secure = source.StartsWith("https://") || string.IsNullOrEmpty(source) ||
                source.StartsWith("lotus://", StringComparison.OrdinalIgnoreCase);
            SecurityIcon.Data = (Geometry)FindResource(secure ? "IconLock" : "IconWarning");
            SecurityIcon.Stroke = secure
                ? (Brush)new BrushConverter().ConvertFrom("#1E8E3E")
                : (Brush)new BrushConverter().ConvertFrom("#D93025");
            SecurityIcon.ToolTip = secure ? "Connection is secure" : "Connection is not secure";
        }
    }

    // ====================================================================
    // Support models
    // ====================================================================

    public class TabHeaderInfo : INotifyPropertyChanged
    {
        private string _title = "New Tab";
        private ImageSource _favicon;
        private bool _isLoading;

        public string Title
        {
            get => _title;
            set { _title = value; OnChanged(nameof(Title)); }
        }

        public ImageSource Favicon
        {
            get => _favicon;
            set
            {
                _favicon = value;
                OnChanged(nameof(Favicon));
                OnChanged(nameof(ShowFavicon));
                OnChanged(nameof(ShowFallbackDot));
            }
        }

        // True while the tab's WebView2 is between NavigationStarting and
        // NavigationCompleted. Drives the Lotus spinner in the favicon slot.
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnChanged(nameof(IsLoading));
                OnChanged(nameof(ShowFavicon));
                OnChanged(nameof(ShowFallbackDot));
            }
        }

        // The real page favicon is only shown once the page is done loading
        // AND a favicon was actually delivered via FaviconChanged.
        public bool ShowFavicon => !IsLoading && Favicon != null;

        // The fallback globe is only shown when the page is done loading AND
        // we have no favicon to display for it.
        public bool ShowFallbackDot => !IsLoading && Favicon == null;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class BookmarkEntry
    {
        public string Title { get; set; }
        public string Url { get; set; }
    }

    public class HistoryEntry
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public DateTime VisitedAt { get; set; }
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnChanged(nameof(Progress)); }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnChanged(nameof(StatusText)); }
        }

        private bool _isInProgress;
        public bool IsInProgress
        {
            get => _isInProgress;
            set { _isInProgress = value; OnChanged(nameof(IsInProgress)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}