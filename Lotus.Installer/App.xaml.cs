using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Lotus.Installer
{
    public partial class App : Application
    {
        CliArgs _args;
        bool _isTempUninstaller;
        Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _args = CliArgs.Parse(e.Args);
            Log.Write("---- start: " + string.Join(" ", e.Args));
            try { Start(); }
            catch (Exception ex)
            {
                Log.Write(ex.ToString());
                if (!_args.Silent)
                    MessageBox.Show("Setup hit a problem:\n\n" + ex.Message + "\n\nLog: " + Log.FilePath, "Lotus Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_isTempUninstaller) Engine.ScheduleSelfDelete(Payload.SelfPath);
            if (_mutex != null) _mutex.Dispose();
            base.OnExit(e);
        }

        void Start()
        {
            bool createdNew;
            _mutex = new Mutex(true, "Lotus-Setup-Mutex", out createdNew);
            if (!createdNew && !_args.Silent) { Shutdown(0); return; }

            string self = Payload.SelfPath;
            string selfDir = Path.GetDirectoryName(self);
            bool hasPayload;
            using (var p = Payload.Open(_args.Payload)) hasPayload = p != null;

            bool inInstallDir = File.Exists(Path.Combine(selfDir, Product.ExeName));
            bool uninstall = _args.Uninstall || (!hasPayload && inInstallDir);

            if (uninstall) { StartUninstall(self, selfDir, inInstallDir); return; }

            if (!hasPayload)
            {
                MessageBox.Show("This setup file is incomplete. Please download it again.", "Lotus Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }

            if (_args.Silent) { RunSilentInstall(); return; }
            new InstallerWindow(SetupMode.Install, _args, null).Show();
        }

        async void RunSilentInstall()
        {
            int code = 0;
            try
            {
                var o = new InstallOptions
                {
                    InstallDir = Engine.ResolveDefaultInstallDir(_args.Dir),
                    PayloadPath = _args.Payload,
                    DesktopShortcut = !_args.NoDesktop,
                    LaunchAfter = false,
                    SetDefault = false
                };
                await Task.Run(() => Engine.Install(o, null));
            }
            catch (Exception ex) { Log.Write(ex.ToString()); code = 1; }
            Shutdown(code);
        }

        void StartUninstall(string self, string selfDir, bool inInstallDir)
        {
            string dir = _args.FromDir;
            if (string.IsNullOrEmpty(dir))
            {
                dir = inInstallDir ? selfDir : Engine.GetInstalledLocation();
                if (dir == null)
                {
                    if (!_args.Silent) MessageBox.Show("Lotus doesn't appear to be installed.", "Lotus Setup", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }
                if (inInstallDir)
                {
                    // Uninstall.exe can't delete itself while running: re-launch from %TEMP% and exit.
                    string tmp = Path.Combine(Path.GetTempPath(), "LotusUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(self, tmp, true);
                    var sb = new StringBuilder("/uninstall /from=\"" + selfDir + "\"");
                    if (_args.Silent) sb.Append(" /silent");
                    if (_args.DeleteData) sb.Append(" /deletedata");
                    Process.Start(new ProcessStartInfo(tmp, sb.ToString()) { UseShellExecute = false });
                    Shutdown(0);
                    return;
                }
            }
            else _isTempUninstaller = true;

            if (_args.Silent) { RunSilentUninstall(dir); return; }
            new InstallerWindow(SetupMode.Uninstall, _args, dir).Show();
        }

        async void RunSilentUninstall(string dir)
        {
            int code = 0;
            try { await Task.Run(() => Engine.Uninstall(dir, _args.DeleteData, null)); }
            catch (Exception ex) { Log.Write(ex.ToString()); code = 1; }
            Shutdown(code);
        }
    }
}
