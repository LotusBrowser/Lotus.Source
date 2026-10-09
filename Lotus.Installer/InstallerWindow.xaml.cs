using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Shapes = System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace Lotus.Installer
{
    internal enum SetupMode { Install, Uninstall }

    public partial class InstallerWindow : Window
    {
        readonly SetupMode _mode;
        readonly CliArgs _args;
        readonly string _uninstallDir;
        readonly Shapes.Path[] _petals;

        FrameworkElement _page, _returnPage;
        Action _resume;
        bool _busy, _lastSuccess;
        int _petalsOn = 8;
        InstallOptions _opts;
        InstallResult _result;
        long _payloadBytes;

        internal InstallerWindow(SetupMode mode, CliArgs args, string uninstallDir)
        {
            InitializeComponent();
            _mode = mode;
            _args = args;
            _uninstallDir = uninstallDir;
            _petals = new[] { Petal0, Petal1, Petal2, Petal3, Petal4, Petal5, Petal6, Petal7 };

            if (mode == SetupMode.Install)
            {
                VersionText.Text = "Version " + Engine.AppVersion;
                PathBox.Text = Engine.ResolveDefaultInstallDir(args.Dir);
                DesktopSwitch.IsChecked = !args.NoDesktop;
                WebViewNotice.Visibility = Engine.IsWebView2Installed() ? Visibility.Collapsed : Visibility.Visible;
                _payloadBytes = Engine.PayloadUncompressedBytes(args.Payload);
                if (_payloadBytes > 0)
                    SpaceText.Text = "Needs about " + (_payloadBytes / 1048576 + 1) + " MB of disk space.";
                ShowPage(PageWelcome);
            }
            else
            {
                var v = Engine.InstalledVersion();
                VersionText.Text = v.Length > 0 ? "Version " + v : "";
                ShowPage(PageUninstall);
            }
        }

        // ------------------------------------------------------------ chrome

        void Root_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); } catch { }
        }

        void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !_busy) Close();
            else if (e.Key == Key.Enter && !_busy)
            {
                // Enter triggers the page's primary action (unless a button has focus and handles it itself).
                if (_page == PageWelcome) Install_Click(this, null);
                else if (_page == PageUninstall) Uninstall_Click(this, null);
                else if (_page == PageDone) Finish_Click(this, null);
            }
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_busy) { e.Cancel = true; return; }
            Application.Current.Shutdown(_lastSuccess || _page != PageDone ? 0 : 1);
        }

        void ShowPage(FrameworkElement next)
        {
            if (_page == next) return;
            if (_page != null) _page.Visibility = Visibility.Collapsed;
            _page = next;
            next.Opacity = 0;
            next.Visibility = Visibility.Visible;
            if (SystemParameters.ClientAreaAnimation)
                next.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            else { next.BeginAnimation(OpacityProperty, null); next.Opacity = 1; }

            CloseButton.IsEnabled = !_busy;
        }

        // ------------------------------------------------------------ petals

        void SetPetals(int on, bool animate)
        {
            on = Math.Max(0, Math.Min(_petals.Length, on));
            bool anim = animate && SystemParameters.ClientAreaAnimation;
            for (int i = 0; i < _petals.Length; i++)
            {
                bool should = i < on, was = i < _petalsOn;
                if (should == was) continue;
                ApplyPetal(_petals[i], should, anim);
            }
            _petalsOn = on;
        }

        static void ApplyPetal(Shapes.Path p, bool on, bool animate)
        {
            var st = (System.Windows.Media.ScaleTransform)p.RenderTransform;
            double o = on ? 1.0 : 0.12, s = on ? 1.0 : 0.86;
            if (!animate)
            {
                p.BeginAnimation(OpacityProperty, null);
                st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                p.Opacity = o; st.ScaleX = s; st.ScaleY = s;
                return;
            }
            var dur = TimeSpan.FromMilliseconds(520);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            p.BeginAnimation(OpacityProperty, new DoubleAnimation(o, dur) { EasingFunction = ease });
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(s, dur) { EasingFunction = ease });
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(s, dur) { EasingFunction = ease });
        }

        // ---------------------------------------------------------- progress

        void OnProgress(ProgressInfo p)
        {
            ProgressStatus.Text = p.Status ?? "";
            ProgressDetail.Text = p.Detail ?? "";
            double f = Math.Max(0, Math.Min(1, p.Fraction));
            ProgressPercent.Text = (int)Math.Round(f * 100) + "%";

            double w = BarTrack.ActualWidth * f;
            if (SystemParameters.ClientAreaAnimation)
                BarFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(180)));
            else { BarFill.BeginAnimation(WidthProperty, null); BarFill.Width = w; }

            int n = (int)Math.Floor(f * _petals.Length);
            SetPetals(_mode == SetupMode.Install ? n : _petals.Length - n, true);
        }

        void StartProgress(string title)
        {
            _busy = true;
            ProgressTitle.Text = title;
            ProgressStatus.Text = "Preparing";
            ProgressDetail.Text = "";
            ProgressPercent.Text = "0%";
            BarFill.BeginAnimation(WidthProperty, null);
            BarFill.Width = 0;
            ShowPage(PageProgress);
            // Install: the lotus closes up, then blooms with progress. Uninstall: it fades petal by petal.
            if (_mode == SetupMode.Install) SetPetals(0, true);
        }

        // ----------------------------------------------------------- install

        bool TryGetInstallDir(out string dir, out string error)
        {
            dir = null; error = null;
            string text = (PathBox.Text ?? "").Trim().Trim('"');
            if (text.Length == 0) { error = "Choose a folder to install Lotus into."; return false; }
            text = Environment.ExpandEnvironmentVariables(text);
            if (!Path.IsPathRooted(text)) { error = "Enter a full path, like C:\\Users\\you\\AppData\\Local\\Programs\\Lotus."; return false; }
            try { dir = Path.GetFullPath(text).TrimEnd('\\'); }
            catch (Exception) { error = "That doesn't look like a valid folder path."; return false; }
            string root = (Path.GetPathRoot(dir) ?? "").TrimEnd('\\');
            if (string.Equals(root, dir, StringComparison.OrdinalIgnoreCase)) { error = "Pick a folder, not a whole drive."; return false; }
            return true;
        }

        void PathBox_TextChanged(object sender, TextChangedEventArgs e) { PathError.Visibility = Visibility.Collapsed; }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "Choose where to install Lotus";
                dlg.ShowNewFolderButton = true;
                string cur;
                if (Directory.Exists(PathBox.Text)) dlg.SelectedPath = PathBox.Text;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    cur = dlg.SelectedPath.TrimEnd('\\');
                    PathBox.Text = string.Equals(Path.GetFileName(cur), Product.Name, StringComparison.OrdinalIgnoreCase)
                        ? cur : Path.Combine(cur, Product.Name);
                }
            }
        }

        void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _page != PageWelcome) return;
            string dir, err;
            if (!TryGetInstallDir(out dir, out err))
            {
                PathError.Text = err;
                PathError.Visibility = Visibility.Visible;
                return;
            }
            _opts = new InstallOptions
            {
                InstallDir = dir,
                PayloadPath = _args.Payload,
                DesktopShortcut = DesktopSwitch.IsChecked == true,
                SetDefault = DefaultSwitch.IsChecked == true,
                LaunchAfter = LaunchSwitch.IsChecked == true
            };
            if (Engine.IsLotusRunning(dir))
            {
                _returnPage = PageWelcome;
                _resume = BeginInstall;
                ShowPage(PageClose);
                return;
            }
            BeginInstall();
        }

        async void BeginInstall()
        {
            StartProgress("Installing Lotus");
            var progress = new Progress<ProgressInfo>(OnProgress);
            try
            {
                _result = await Task.Run(() => Engine.Install(_opts, progress));
                _busy = false; _lastSuccess = true;
                SetPetals(8, true);
                ShowInstallDone();
            }
            catch (Exception ex)
            {
                _busy = false; _lastSuccess = false;
                Log.Write(ex.ToString());
                SetPetals(8, true);
                ShowError("Setup couldn't finish", ex);
            }
        }

        void ShowInstallDone()
        {
            DoneTitle.Text = "Lotus is ready";
            DoneBody.Text = "Lotus was installed to " + _result.InstallDir + ".";
            ErrorBox.Visibility = Visibility.Collapsed;
            var notes = new System.Text.StringBuilder();
            if (_opts.SetDefault) notes.Append("When you click Finish, Windows Settings opens so you can choose Lotus as your default browser.");
            if (_result.WebView2Missing)
            {
                if (notes.Length > 0) notes.Append("\n\n");
                notes.Append("Lotus needs the Microsoft Edge WebView2 Runtime, and setup couldn't install it automatically. Install it, then start Lotus.");
                WebViewLinkButton.Visibility = Visibility.Visible;
            }
            else WebViewLinkButton.Visibility = Visibility.Collapsed;
            DoneNote.Text = notes.ToString();
            DoneNote.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            FinishButton.Content = "Finish";
            CloseButton.IsEnabled = true;
            ShowPage(PageDone);
        }

        void ShowError(string title, Exception ex)
        {
            string msg = ex is UnauthorizedAccessException
                ? "Setup doesn't have permission to write to that folder. Choose a folder inside your user profile instead."
                : ex.Message;
            DoneTitle.Text = title;
            DoneBody.Text = "Nothing is wrong with your PC. Here's what Setup ran into:";
            ErrorBox.Text = msg + "\n\nLog file: " + Log.FilePath;
            ErrorBox.Visibility = Visibility.Visible;
            DoneNote.Visibility = Visibility.Collapsed;
            WebViewLinkButton.Visibility = Visibility.Collapsed;
            FinishButton.Content = "Close";
            CloseButton.IsEnabled = true;
            ShowPage(PageDone);
        }

        // --------------------------------------------------------- uninstall

        void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _page != PageUninstall) return;
            if (Engine.IsLotusRunning(_uninstallDir))
            {
                _returnPage = PageUninstall;
                _resume = BeginUninstall;
                ShowPage(PageClose);
                return;
            }
            BeginUninstall();
        }

        async void BeginUninstall()
        {
            StartProgress("Removing Lotus");
            bool deleteData = DeleteDataSwitch.IsChecked == true;
            var progress = new Progress<ProgressInfo>(OnProgress);
            try
            {
                await Task.Run(() => Engine.Uninstall(_uninstallDir, deleteData, progress));
                _busy = false; _lastSuccess = true;
                SetPetals(0, true);
                DoneTitle.Text = "Lotus has been removed";
                DoneBody.Text = deleteData
                    ? "Lotus and your browsing data were removed from this PC."
                    : "Lotus was removed. Your bookmarks and history are still in AppData\\Local\\LotusBrowser if you reinstall.";
                ErrorBox.Visibility = Visibility.Collapsed;
                DoneNote.Visibility = Visibility.Collapsed;
                WebViewLinkButton.Visibility = Visibility.Collapsed;
                FinishButton.Content = "Close";
                CloseButton.IsEnabled = true;
                ShowPage(PageDone);
            }
            catch (Exception ex)
            {
                _busy = false; _lastSuccess = false;
                Log.Write(ex.ToString());
                ShowError("Uninstall couldn't finish", ex);
            }
        }

        // ------------------------------------------------- shared page actions

        void CloseCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_returnPage != null) ShowPage(_returnPage);
        }

        async void CloseContinue_Click(object sender, RoutedEventArgs e)
        {
            CloseContinueButton.IsEnabled = false;
            string dir = _mode == SetupMode.Install ? _opts.InstallDir : _uninstallDir;
            await Task.Run(() => Engine.CloseRunning(dir));
            CloseContinueButton.IsEnabled = true;
            var go = _resume;
            if (go != null) go();
        }

        async void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_lastSuccess && _mode == SetupMode.Install)
            {
                try
                {
                    if (_opts.LaunchAfter) Engine.Launch(_result.ExePath);
                    if (_opts.SetDefault)
                    {
                        if (_opts.LaunchAfter) await Task.Delay(700);
                        Engine.OpenDefaultApps();
                    }
                }
                catch (Exception ex) { Log.Write("Finish: " + ex.Message); }
            }
            Close();
        }

        void WebViewLink_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); }
            catch { }
        }
    }
}
