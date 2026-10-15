using System;
using Microsoft.Win32;

namespace Lotus.Browser
{
    // Registers Lotus as a Windows browser candidate so it shows up in
    // Settings > Apps > Default apps, and lets the app query whether it is
    // currently the chosen default for http/https.
    //
    // Windows (Vista+) does not let an application flip itself to be the
    // default handler in code - the user has to confirm the choice in the
    // Settings UI. What we CAN do without any admin rights, entirely under
    // HKEY_CURRENT_USER, is:
    //   1. Register a "client" entry describing the app (name, icon, open
    //      command) under Software\Clients\StartMenuInternet.
    //   2. Advertise which protocols/extensions it can handle via a
    //      Capabilities key.
    //   3. List it in Software\RegisteredApplications so it appears as a
    //      choice in the Default Apps picker.
    //   4. Register a ProgId under Software\Classes with the actual
    //      shell\open\command line used to launch it.
    // Once that's done, we open ms-settings:defaultapps and the user picks
    // Lotus from the list themselves.
    public static class DefaultBrowserRegistrar
    {
        private const string AppRegName = "Lotus";
        private const string ProgId = "LotusHTML";

        private static string ExePath => System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        // Writes every registry key needed for Lotus to be offered as a
        // default-browser candidate. Safe to call every startup - it's cheap
        // and self-healing if the user moved/reinstalled the app (exe path
        // can change between builds).
        public static void EnsureRegistered()
        {
            try
            {
                string exe = ExePath;
                string openCommand = $"\"{exe}\" \"%1\"";

                // ---- Software\Clients\StartMenuInternet\Lotus ----
                using (var client = Registry.CurrentUser.CreateSubKey($@"Software\Clients\StartMenuInternet\{AppRegName}"))
                {
                    client.SetValue("", AppRegName);
                    client.SetValue("LocalizedString", AppRegName);

                    using (var icon = client.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", $"{exe},0");

                    using (var openCmd = client.CreateSubKey(@"shell\open\command"))
                        openCmd.SetValue("", openCommand);

                    using (var caps = client.CreateSubKey("Capabilities"))
                    {
                        caps.SetValue("ApplicationName", AppRegName);
                        caps.SetValue("ApplicationIcon", $"{exe},0");
                        caps.SetValue("ApplicationDescription", "Lotus web browser");

                        using (var urlAssoc = caps.CreateSubKey("URLAssociations"))
                        {
                            urlAssoc.SetValue("http", ProgId);
                            urlAssoc.SetValue("https", ProgId);
                        }

                        using (var fileAssoc = caps.CreateSubKey("FileAssociations"))
                        {
                            fileAssoc.SetValue(".htm", ProgId);
                            fileAssoc.SetValue(".html", ProgId);
                        }

                        using (var startMenu = caps.CreateSubKey("StartMenu"))
                            startMenu.SetValue("StartMenuInternet", AppRegName);
                    }
                }

                // ---- Software\RegisteredApplications ----
                using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                    registered.SetValue(AppRegName, $@"Software\Clients\StartMenuInternet\{AppRegName}\Capabilities");

                // ---- Software\Classes\LotusHTML (the ProgId actually invoked on open) ----
                using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
                {
                    progId.SetValue("", "Lotus HTML Document");

                    using (var icon = progId.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", $"{exe},0");

                    using (var openCmd = progId.CreateSubKey(@"shell\open\command"))
                        openCmd.SetValue("", openCommand);
                }
            }
            catch
            {
                // Registration is best-effort - if the user's environment blocks
                // HKCU writes for some reason, just skip it silently rather than
                // crashing the browser over a settings feature.
            }
        }

        // Reads what Windows currently has recorded as the user's chosen
        // handler for the http protocol, and compares it to our ProgId.
        public static bool IsCurrentDefault()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                var progId = key?.GetValue("ProgId") as string;
                return string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Opens the Windows "Default apps" settings page so the user can pick
        // Lotus. Registration must have already run at least once (we call
        // EnsureRegistered on every startup) so Lotus actually shows up in
        // the list.
        public static void OpenDefaultAppsSettings()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:defaultapps",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Fallback for the rare box where the ms-settings URI scheme itself
                // isn't resolvable (e.g. very locked-down Windows builds).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "/name Microsoft.DefaultPrograms",
                    UseShellExecute = true
                });
            }
        }
    }
}
