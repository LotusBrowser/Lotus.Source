using System;
using System.IO;
using System.Text;

namespace Lotus.Installer
{
    internal static class Product
    {
        public const string Name = "Lotus";
        public const string Publisher = "Allen Lambert Jr";
        public const string ExeName = "Lotus.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string ManifestName = "install-manifest.txt";

        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lotus";
        // Key written by the previous Inno Setup installer (AppId + "_is1").
        public const string LegacyInnoKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B6C1B9D1-4B7E-4B0E-9B8E-8C7D4E9B6F31}_is1";

        // Must match DefaultBrowserRegistrar.cs in the Lotus app.
        public const string BrowserRegName = "Lotus";
        public const string BrowserProgId = "LotusHTML";

        public static string DefaultInstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lotus");

        // Where Lotus really stores bookmarks, history and the WebView2 profile
        // (MainWindow.xaml.cs -> InitializeAsync). The old Inno script cleaned
        // %APPDATA%\Lotus, which Lotus never uses.
        public static string UserDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotusBrowser");
        public static string LegacyRoamingDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lotus");
    }

    internal static class Log
    {
        static readonly string PathName = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LotusSetup.log");
        public static string FilePath => PathName;
        public static void Write(string message)
        {
            try { File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    internal sealed class CliArgs
    {
        public bool Uninstall, Silent, DeleteData, NoDesktop;
        public string Dir, Payload, FromDir;

        public static CliArgs Parse(string[] args)
        {
            var r = new CliArgs();
            foreach (var raw in args)
            {
                var s = raw.TrimStart('/', '-');
                string key = s, val = null;
                int eq = s.IndexOf('=');
                if (eq >= 0) { key = s.Substring(0, eq); val = s.Substring(eq + 1).Trim('"'); }
                switch (key.ToLowerInvariant())
                {
                    case "uninstall": r.Uninstall = true; break;
                    case "silent": case "s": case "quiet": case "verysilent": r.Silent = true; break;
                    case "dir": case "d": r.Dir = val; break;
                    case "payload": r.Payload = val; break;
                    case "from": r.FromDir = val; break;
                    case "deletedata": r.DeleteData = true; break;
                    case "nodesktop": r.NoDesktop = true; break;
                }
            }
            return r;
        }
    }

    internal sealed class InstallOptions
    {
        public string InstallDir;
        public string PayloadPath;
        public bool DesktopShortcut = true;
        public bool LaunchAfter = true;
        public bool SetDefault = true;
    }

    internal sealed class InstallResult
    {
        public string InstallDir, ExePath;
        public bool WebView2Missing;
    }

    internal sealed class ProgressInfo
    {
        public double Fraction;
        public string Status, Detail;
    }
}
