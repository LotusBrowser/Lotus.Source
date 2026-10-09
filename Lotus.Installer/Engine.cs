using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Lotus.Installer
{
    internal static class Engine
    {
        const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // ---------------------------------------------------------------- info

        public static string AppVersion
        {
            get
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(Payload.SelfPath).ProductVersion ?? "1.0.0";
                    int i = v.IndexOf('+');
                    return i > 0 ? v.Substring(0, i) : v;
                }
                catch { return "1.0.0"; }
            }
        }

        public static string InstalledVersion()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Product.UninstallKey))
                return (k == null ? null : k.GetValue("DisplayVersion") as string) ?? "";
        }

        public static string GetInstalledLocation()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Product.UninstallKey))
            {
                var p = k == null ? null : k.GetValue("InstallLocation") as string;
                return !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p.TrimEnd('\\') : null;
            }
        }

        static string GetLegacyInnoLocation()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Product.LegacyInnoKey))
            {
                var p = k == null ? null : k.GetValue("InstallLocation") as string;
                return !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p.TrimEnd('\\') : null;
            }
        }

        // arg > existing new-style install > old Inno install > default
        public static string ResolveDefaultInstallDir(string argDir)
        {
            if (!string.IsNullOrWhiteSpace(argDir)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(argDir)).TrimEnd('\\');
            return GetInstalledLocation() ?? GetLegacyInnoLocation() ?? Product.DefaultInstallDir;
        }

        public static long PayloadUncompressedBytes(string payloadOverride)
        {
            try
            {
                using (var s = Payload.Open(payloadOverride))
                {
                    if (s == null) return 0;
                    using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                        return z.Entries.Sum(e => e.Length);
                }
            }
            catch { return 0; }
        }

        public static bool IsWebView2Installed()
        {
            string sub = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2Guid;
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                if (HasPv(RegistryHive.LocalMachine, view, sub)) return true;
            return HasPv(RegistryHive.CurrentUser, RegistryView.Default, sub);
        }

        static bool HasPv(RegistryHive hive, RegistryView view, string sub)
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, view))
                using (var k = b.OpenSubKey(sub))
                {
                    var pv = k == null ? null : k.GetValue("pv") as string;
                    return !string.IsNullOrEmpty(pv) && pv != "0.0.0.0";
                }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ processes

        static List<Process> FindRunning(string dir)
        {
            var res = new List<Process>();
            string prefix = dir.TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName("Lotus"))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { res.Add(p); continue; }
                }
                catch { }
                p.Dispose();
            }
            return res;
        }

        public static bool IsLotusRunning(string dir)
        {
            var l = FindRunning(dir);
            bool any = l.Count > 0;
            foreach (var p in l) p.Dispose();
            return any;
        }

        public static void CloseRunning(string dir)
        {
            foreach (var p in FindRunning(dir))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch (Exception ex) { Log.Write("CloseRunning: " + ex.Message); }
                finally { p.Dispose(); }
            }
        }

        // -------------------------------------------------------------- install

        public static InstallResult Install(InstallOptions o, IProgress<ProgressInfo> progress)
        {
            string dir = Path.GetFullPath(o.InstallDir).TrimEnd('\\');
            string prefix = dir + "\\";
            var result = new InstallResult { InstallDir = dir, ExePath = Path.Combine(dir, Product.ExeName) };
            bool dirExisted = Directory.Exists(dir);
            bool needWebView = !IsWebView2Installed();
            double extractShare = needWebView ? 0.60 : 0.90;
            var written = new List<string>();

            Log.Write("Install -> " + dir);
            try
            {
                Report(progress, 0, "Preparing", null);
                CloseRunning(dir);

                using (var payload = Payload.Open(o.PayloadPath))
                {
                    if (payload == null) throw new FileNotFoundException("The installer's payload is missing. Download Setup again.");
                    using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                    {
                        var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                        long total = Math.Max(1, entries.Sum(e => e.Length));
                        CheckDisk(dir, total);
                        Directory.CreateDirectory(dir);

                        var oldManifest = ReadManifest(dir);
                        var newRel = new List<string>();
                        var sw = Stopwatch.StartNew();
                        long done = 0, lastReport = -100;

                        foreach (var e in entries)
                        {
                            string rel = e.FullName.Replace('/', '\\');
                            string dest = Path.GetFullPath(Path.Combine(dir, rel));
                            if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Unsafe path in payload: " + e.FullName);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));

                            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                            using (var dst = OpenForWrite(dest))
                            {
                                written.Add(dest);
                                using (var src = e.Open())
                                {
                                    var buf = new byte[81920]; int n;
                                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        dst.Write(buf, 0, n);
                                        done += n;
                                        if (sw.ElapsedMilliseconds - lastReport >= 40)
                                        {
                                            lastReport = sw.ElapsedMilliseconds;
                                            Report(progress, extractShare * Math.Min(1.0, (double)done / total), "Copying files", rel);
                                        }
                                    }
                                }
                            }
                            try { File.SetLastWriteTime(dest, e.LastWriteTime.DateTime); } catch { }
                            newRel.Add(rel);
                        }

                        // Remove files an older version installed that this one doesn't ship.
                        if (oldManifest != null)
                        {
                            var keep = new HashSet<string>(newRel, StringComparer.OrdinalIgnoreCase) { Product.UninstallerName };
                            foreach (var old in oldManifest.Where(r => !keep.Contains(r)))
                            {
                                string p = SafeCombine(dir, old);
                                if (p != null) TryDelete(p);
                            }
                            PruneEmptyDirs(dir, false);
                        }

                        Report(progress, extractShare + 0.02, "Finishing up", "Creating uninstaller");
                        string uninstaller = Path.Combine(dir, Product.UninstallerName);
                        CopyStub(uninstaller);
                        written.Add(uninstaller);
                        newRel.Add(Product.UninstallerName);
                        File.WriteAllLines(Path.Combine(dir, Product.ManifestName), newRel, new UTF8Encoding(false));

                        Report(progress, extractShare + 0.04, "Finishing up", "Registering Lotus");
                        WriteUninstallEntry(dir, result.ExePath, uninstaller, total);
                        RegisterBrowser(result.ExePath);

                        Report(progress, extractShare + 0.06, "Finishing up", "Creating shortcuts");
                        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product.Name + ".lnk");
                        ShellLink.Create(startMenu, result.ExePath, "Lotus web browser");
                        if (o.DesktopShortcut)
                            ShellLink.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product.Name + ".lnk"), result.ExePath, "Lotus web browser");

                        MigrateLegacyInno(dir);
                    }
                }

                if (needWebView)
                {
                    bool ok = false;
                    try { ok = InstallWebView2(progress, 0.60, 0.95); }
                    catch (Exception ex) { Log.Write("WebView2: " + ex); }
                    result.WebView2Missing = !ok;
                }

                Report(progress, 1.0, "Done", null);
                return result;
            }
            catch
            {
                if (!dirExisted)
                {
                    foreach (var f in written) TryDelete(f);
                    TryDelete(Path.Combine(dir, Product.ManifestName));
                    PruneEmptyDirs(dir, true);
                }
                throw;
            }
        }

        static void Report(IProgress<ProgressInfo> p, double f, string status, string detail)
        {
            if (p != null) p.Report(new ProgressInfo { Fraction = f, Status = status, Detail = detail });
        }

        static void CheckDisk(string dir, long needed)
        {
            try
            {
                var root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return;
                var di = new DriveInfo(root);
                if (di.AvailableFreeSpace < needed + 50L * 1024 * 1024)
                    throw new IOException("There isn't enough free space on " + root + ". Lotus needs about " + (needed / 1048576 + 50) + " MB.");
            }
            catch (IOException) { throw; }
            catch { }
        }

        static FileStream OpenForWrite(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920); }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 6)
                {
                    Thread.Sleep(300);
                }
            }
        }

        static void CopyStub(string dest)
        {
            string self = Payload.SelfPath;
            long len = Payload.StubLength(self);
            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
            using (var src = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = OpenForWrite(dest))
            {
                var buf = new byte[81920]; long left = len;
                while (left > 0)
                {
                    int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    dst.Write(buf, 0, n); left -= n;
                }
            }
        }

        static List<string> ReadManifest(string dir)
        {
            try
            {
                string p = Path.Combine(dir, Product.ManifestName);
                return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Trim().Length > 0).ToList() : null;
            }
            catch { return null; }
        }

        static string SafeCombine(string dir, string rel)
        {
            try
            {
                string p = Path.GetFullPath(Path.Combine(dir, rel));
                return p.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? p : null;
            }
            catch { return null; }
        }

        static bool TryDelete(string path, int attempts = 1)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }
                catch { if (i + 1 < attempts) Thread.Sleep(300); }
            }
            return false;
        }

        static void PruneEmptyDirs(string root, bool deleteRootIfEmpty)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(s => s.Length))
                {
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
                }
                if (deleteRootIfEmpty && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
            catch { }
        }

        // ------------------------------------------------------------- registry

        static void WriteUninstallEntry(string dir, string exe, string uninstaller, long bytes)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Product.UninstallKey))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", AppVersion);
                k.SetValue("Publisher", Product.Publisher);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        // Same keys DefaultBrowserRegistrar.EnsureRegistered() writes at runtime, so
        // Lotus shows up in Settings > Default apps immediately after install.
        static void RegisterBrowser(string exe)
        {
            string cmd = "\"" + exe + "\" \"%1\"";
            string name = Product.BrowserRegName, prog = Product.BrowserProgId;

            using (var client = Registry.CurrentUser.CreateSubKey(@"Software\Clients\StartMenuInternet\" + name))
            {
                client.SetValue("", name);
                client.SetValue("LocalizedString", name);
                using (var k = client.CreateSubKey("DefaultIcon")) k.SetValue("", exe + ",0");
                using (var k = client.CreateSubKey(@"shell\open\command")) k.SetValue("", cmd);
                using (var caps = client.CreateSubKey("Capabilities"))
                {
                    caps.SetValue("ApplicationName", name);
                    caps.SetValue("ApplicationIcon", exe + ",0");
                    caps.SetValue("ApplicationDescription", "Lotus web browser");
                    using (var u = caps.CreateSubKey("URLAssociations")) { u.SetValue("http", prog); u.SetValue("https", prog); }
                    using (var f = caps.CreateSubKey("FileAssociations")) { f.SetValue(".htm", prog); f.SetValue(".html", prog); }
                    using (var s = caps.CreateSubKey("StartMenu")) s.SetValue("StartMenuInternet", name);
                }
            }
            using (var r = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                r.SetValue(name, @"Software\Clients\StartMenuInternet\" + name + @"\Capabilities");
            using (var p = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + prog))
            {
                p.SetValue("", "Lotus HTML Document");
                using (var k = p.CreateSubKey("DefaultIcon")) k.SetValue("", exe + ",0");
                using (var k = p.CreateSubKey(@"shell\open\command")) k.SetValue("", cmd);
            }
        }

        // The Inno uninstaller never removed these, so they used to be left behind.
        static void UnregisterBrowser()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Clients\StartMenuInternet\" + Product.BrowserRegName, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Product.BrowserProgId, false); } catch { }
            try
            {
                using (var r = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                    if (r != null) r.DeleteValue(Product.BrowserRegName, false);
            }
            catch { }
        }

        // Upgrading from the Inno Setup build: drop its Add/Remove entry, its
        // unins000.* files and its Start Menu folder so there's one clean install.
        // (We do NOT run Inno's uninstaller - it would delete user data.)
        static void MigrateLegacyInno(string newDir)
        {
            try
            {
                string old = GetLegacyInnoLocation();
                Registry.CurrentUser.DeleteSubKeyTree(Product.LegacyInnoKey, false);
                if (old != null && Directory.Exists(old))
                    foreach (var f in Directory.GetFiles(old, "unins*.*")) TryDelete(f);

                string group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product.Name);
                if (Directory.Exists(group) && Directory.GetFileSystemEntries(group).All(e => e.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
                    Directory.Delete(group, true);
            }
            catch (Exception ex) { Log.Write("MigrateLegacyInno: " + ex.Message); }
        }

        // ------------------------------------------------------------- WebView2

        static bool InstallWebView2(IProgress<ProgressInfo> progress, double lo, double hi)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            string tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var wc = new WebClient())
                {
                    wc.DownloadProgressChanged += (s, a) =>
                        Report(progress, lo + (hi - lo) * 0.7 * a.ProgressPercentage / 100.0, "Downloading WebView2 Runtime", a.ProgressPercentage + "%");
                    wc.DownloadFileTaskAsync(new Uri(WebView2BootstrapperUrl), tmp).GetAwaiter().GetResult();
                }
                Report(progress, lo + (hi - lo) * 0.75, "Installing WebView2 Runtime", "This can take a minute");
                var psi = new ProcessStartInfo(tmp, "/silent /install") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(5 * 60 * 1000);
                return IsWebView2Installed();
            }
            finally { TryDelete(tmp); }
        }

        // ------------------------------------------------------------ uninstall

        public static void Uninstall(string dir, bool deleteData, IProgress<ProgressInfo> progress)
        {
            dir = Path.GetFullPath(dir).TrimEnd('\\');
            Log.Write("Uninstall <- " + dir + " deleteData=" + deleteData);
            Report(progress, 0, "Preparing", null);
            CloseRunning(dir);

            var manifest = ReadManifest(dir);
            var files = new List<string>();
            if (manifest != null)
                foreach (var r in manifest) { var p = SafeCombine(dir, r); if (p != null) files.Add(p); }
            else
            {
                files.Add(Path.Combine(dir, Product.ExeName));
                files.Add(Path.Combine(dir, Product.UninstallerName));
            }

            // Delete the real exe first so a failure to remove it is noticed early.
            for (int i = 0; i < files.Count; i++)
            {
                Report(progress, 0.80 * i / Math.Max(1, files.Count), "Removing files", Path.GetFileName(files[i]));
                TryDelete(files[i], 8);
            }
            TryDelete(Path.Combine(dir, Product.ManifestName));
            PruneEmptyDirs(dir, true);

            Report(progress, 0.85, "Removing shortcuts", null);
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product.Name + ".lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product.Name + ".lnk"));

            Report(progress, 0.90, "Cleaning up registry", null);
            UnregisterBrowser();
            try { Registry.CurrentUser.DeleteSubKeyTree(Product.UninstallKey, false); } catch { }

            if (deleteData)
            {
                Report(progress, 0.95, "Deleting browsing data", null);
                foreach (var d in new[] { Product.UserDataDir, Product.LegacyRoamingDir })
                {
                    for (int i = 0; i < 4; i++)
                    {
                        try { if (Directory.Exists(d)) Directory.Delete(d, true); break; }
                        catch { Thread.Sleep(400); }   // WebView2 helper processes may still be exiting
                    }
                }
            }
            Report(progress, 1.0, "Done", null);
        }

        // The uninstaller runs from a temp copy of itself; this removes that copy after exit.
        public static void ScheduleSelfDelete(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + path + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        // ---------------------------------------------------------------- shell

        public static void Launch(string exe)
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
        }

        public static void OpenDefaultApps()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Product.BrowserRegName) { UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.DefaultPrograms") { UseShellExecute = true }); }
        }
    }
}
