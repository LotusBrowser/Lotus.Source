using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TerminalOS
{
    // lotus://sysinfo/ - a front-end for Lotus SysInfo
    // (https://github.com/LotusBrowser/SysInfo).
    //
    // The page always uses the newest GitHub release: when it opens it resolves the
    // latest tag, downloads SysInfo.exe into %LOCALAPPDATA%\LotusBrowser\SysInfo\ if
    // the cached copy is missing or older, and then runs that copy with whatever
    // command-line options the user ticked. Output is streamed back into the page.
    //
    // Same bridge as lotus://settings: the page posts JSON to the host with
    // window.chrome.webview.postMessage, and the host talks back with
    // PostWebMessageAsString. Messages from the page carry only booleans and an
    // optional token - the command line itself is always built here from a fixed
    // whitelist, never from text supplied by the page.
    public partial class MainWindow
    {
        private const string SysInfoUrl = "lotus://sysinfo/";
        private const string SysInfoRepo = "LotusBrowser/SysInfo";
        private const string SysInfoAssetName = "SysInfo.exe";
        private const string SysInfoShareHost = "share.browselotus.net";

        private static readonly Regex SysInfoTagPattern =
            new Regex(@"^[vV]?\d+(\.\d+){0,3}(-[0-9A-Za-z.]+)?$", RegexOptions.Compiled);
        private static readonly Regex AnsiPattern =
            new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        // GitHub answers /releases/latest with a 302 to /releases/tag/<tag>; reading that
        // Location header tells us the newest tag without touching the rate-limited API.
        private static readonly HttpClient SysInfoNoRedirectHttp = CreateSysInfoHttpClient(
            new HttpClientHandler { AllowAutoRedirect = false }, TimeSpan.FromSeconds(20));
        private static readonly HttpClient SysInfoApiHttp = CreateSysInfoHttpClient(
            new HttpClientHandler(), TimeSpan.FromSeconds(20));
        // The download itself can take a while; the per-call CancellationToken is the limit.
        private static readonly HttpClient SysInfoDownloadHttp = CreateSysInfoHttpClient(
            new HttpClientHandler(), Timeout.InfiniteTimeSpan);

        private static HttpClient CreateSysInfoHttpClient(HttpMessageHandler handler, TimeSpan timeout)
        {
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LotusBrowser-SysInfo/1.0");
            return client;
        }

        private bool _sysInfoBusy;                // checking / downloading
        private Process _sysInfoProcess;         // non-null while SysInfo is running
        private bool _sysInfoCancelRequested;
        private string _sysInfoLastReport;
        private string _sysInfoLastShareUrl;

        private string SysInfoDir => Path.Combine(_dataDir, "SysInfo");
        private string SysInfoExePath => Path.Combine(SysInfoDir, SysInfoAssetName);
        private string SysInfoVersionPath => Path.Combine(SysInfoDir, "version.txt");

        // Recognizes lotus://sysinfo (with or without a trailing slash) and the
        // lotus:sysinfo shorthand, mirroring IsLotusSettingsUrl.
        private static bool IsLotusSysInfoUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Equals("lotus:sysinfo", StringComparison.OrdinalIgnoreCase) ||
                   url.Equals("lotus://sysinfo", StringComparison.OrdinalIgnoreCase) ||
                   url.Equals("lotus://sysinfo/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSysInfoSource(string source)
        {
            return Uri.TryCreate(source, UriKind.Absolute, out var u) &&
                   string.Equals(u.Scheme, "lotus", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(u.Host, "sysinfo", StringComparison.OrdinalIgnoreCase);
        }

        // ================================================================
        // Page -> host messages
        // ================================================================

        // Returns true when the message was a sysinfo* action (handled here), so the
        // caller can skip the settings-page handler.
        private bool TryHandleSysInfoMessage(CoreWebView2 core, string source, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch { return false; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("action", out var actionEl) ||
                    actionEl.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var action = actionEl.GetString();
                if (action == null || !action.StartsWith("sysinfo", StringComparison.Ordinal)) return false;

                // Only the SysInfo page itself may drive this, not other lotus:// pages.
                if (!IsSysInfoSource(source)) return true;

                switch (action)
                {
                    case "sysinfoEnsure":
                        _ = SysInfoEnsureAsync(core);
                        break;

                    case "sysinfoRun":
                        _ = SysInfoRunAsync(core,
                            help: SysInfoGetBool(root, "help"),
                            share: SysInfoGetBool(root, "share"),
                            elevate: SysInfoGetBool(root, "elevate"),
                            token: SysInfoGetString(root, "token"));
                        break;

                    case "sysinfoCancel":
                        _sysInfoCancelRequested = true;
                        try { _sysInfoProcess?.Kill(true); } catch { }
                        break;

                    case "sysinfoOpenFolder":
                        SysInfoOpenFolder();
                        break;

                    case "sysinfoOpenReport":
                        SysInfoOpenReport();
                        break;

                    case "sysinfoOpenShare":
                        if (_sysInfoLastShareUrl != null) _ = AddNewTab(_sysInfoLastShareUrl);
                        break;
                }
            }

            return true;
        }

        private static bool SysInfoGetBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

        private static string SysInfoGetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        // ================================================================
        // Host -> page messages
        // ================================================================

        // Posts a JSON message to the page. Messages are only delivered while the tab is
        // still showing lotus://sysinfo - window.chrome.webview exists on every page, so
        // we must not push output (local paths etc.) into a site the user navigated to.
        private void SysInfoPost(CoreWebView2 core, object payload)
        {
            var json = JsonSerializer.Serialize(payload);

            void Send()
            {
                try
                {
                    if (!IsSysInfoSource(core.Source)) return;
                    core.PostWebMessageAsString(json);
                }
                catch { /* tab closed or navigating - nothing to update */ }
            }

            if (Dispatcher.CheckAccess()) Send();
            else Dispatcher.BeginInvoke((Action)Send);
        }

        private void SysInfoStatus(CoreWebView2 core, string state, string message, string installed, string latest) =>
            SysInfoPost(core, new { type = "status", state, message, installed, latest });

        // ================================================================
        // Find / download the latest release
        // ================================================================

        private string ReadInstalledSysInfoVersion()
        {
            try
            {
                if (!File.Exists(SysInfoExePath) || !File.Exists(SysInfoVersionPath)) return null;
                var tag = File.ReadAllText(SysInfoVersionPath).Trim();
                return SysInfoTagPattern.IsMatch(tag) ? tag : null;
            }
            catch { return null; }
        }

        private async Task<string> GetLatestSysInfoTagAsync()
        {
            // 1) Follow the /releases/latest redirect (no API rate limit).
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{SysInfoRepo}/releases/latest");
                using var resp = await SysInfoNoRedirectHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                var location = resp.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    int idx = location.LastIndexOf("/tag/", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var tag = Uri.UnescapeDataString(location.Substring(idx + 5)).Trim('/');
                        if (SysInfoTagPattern.IsMatch(tag)) return tag;
                    }
                }
            }
            catch { /* fall through to the API */ }

            // 2) Fall back to the REST API.
            using var apiResp = await SysInfoApiHttp.GetAsync($"https://api.github.com/repos/{SysInfoRepo}/releases/latest");
            apiResp.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await apiResp.Content.ReadAsStringAsync());
            var apiTag = json.RootElement.GetProperty("tag_name").GetString();
            if (apiTag == null || !SysInfoTagPattern.IsMatch(apiTag))
                throw new InvalidDataException("GitHub returned an unexpected release tag.");
            return apiTag;
        }

        private async Task SysInfoEnsureAsync(CoreWebView2 core)
        {
            if (_sysInfoProcess != null)
            {
                SysInfoPost(core, new { type = "runState", running = true, cancellable = false });
                return;
            }
            if (_sysInfoBusy)
            {
                SysInfoStatus(core, "checking", "Already checking for updates…", ReadInstalledSysInfoVersion(), null);
                return;
            }

            _sysInfoBusy = true;
            string installed = ReadInstalledSysInfoVersion();
            bool haveCached = installed != null;

            try
            {
                SysInfoStatus(core, "checking", "Checking for the latest version…", installed, null);

                string latest;
                try
                {
                    latest = await GetLatestSysInfoTagAsync();
                }
                catch (Exception ex)
                {
                    if (haveCached)
                        SysInfoStatus(core, "ready", "Couldn't check for updates, using the downloaded copy.", installed, null);
                    else
                        SysInfoStatus(core, "error", "Couldn't reach GitHub to find SysInfo: " + ex.Message, null, null);
                    return;
                }

                if (haveCached && string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
                {
                    SysInfoStatus(core, "ready", "You have the latest version.", installed, latest);
                    return;
                }

                SysInfoStatus(core, "downloading", $"Downloading SysInfo {latest}…", installed, latest);
                try
                {
                    await DownloadSysInfoAsync(core, latest);
                    SysInfoStatus(core, "ready",
                        haveCached ? $"Updated to {latest}." : $"Downloaded SysInfo {latest}.", latest, latest);
                }
                catch (Exception ex)
                {
                    if (haveCached)
                        SysInfoStatus(core, "ready", "Update failed (" + ex.Message + "), using the older downloaded copy.", installed, latest);
                    else
                        SysInfoStatus(core, "error", "Download failed: " + ex.Message, null, latest);
                }
            }
            finally
            {
                _sysInfoBusy = false;
            }
        }

        private async Task DownloadSysInfoAsync(CoreWebView2 core, string tag)
        {
            Directory.CreateDirectory(SysInfoDir);

            var url = $"https://github.com/{SysInfoRepo}/releases/download/{Uri.EscapeDataString(tag)}/{SysInfoAssetName}";
            var tmp = SysInfoExePath + ".download";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var resp = await SysInfoDownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();

                long total = resp.Content.Headers.ContentLength ?? -1;
                long received = 0;
                var clock = Stopwatch.StartNew();
                long lastPost = -1000;

                await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                        received += n;
                        if (clock.ElapsedMilliseconds - lastPost >= 100)
                        {
                            lastPost = clock.ElapsedMilliseconds;
                            SysInfoPost(core, new { type = "progress", received, total });
                        }
                    }
                }

                SysInfoPost(core, new { type = "progress", received, total });

                // Basic sanity check: a real Windows executable starts with "MZ".
                var info = new FileInfo(tmp);
                if (info.Length < 100 * 1024 || !SysInfoHasMzHeader(tmp))
                    throw new InvalidDataException("the downloaded file isn't a valid Windows executable");

                File.Move(tmp, SysInfoExePath, true);
                File.WriteAllText(SysInfoVersionPath, tag);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static bool SysInfoHasMzHeader(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }

        // ================================================================
        // Run SysInfo
        // ================================================================

        // The only place a command line is produced. Help wins over everything else.
        private static string BuildSysInfoArgs(bool help, bool share) =>
            help ? "-h" : share ? "--share-online" : "";

        private async Task SysInfoRunAsync(CoreWebView2 core, bool help, bool share, bool elevate, string token)
        {
            if (_sysInfoProcess != null || _sysInfoBusy)
            {
                SysInfoPost(core, new { type = "done", exitCode = -1, error = "SysInfo is busy. Try again in a moment.", hasReport = false, hasShare = false, shareUrl = (string)null });
                return;
            }
            if (!File.Exists(SysInfoExePath))
            {
                SysInfoPost(core, new { type = "done", exitCode = -1, error = "SysInfo hasn't been downloaded yet.", hasReport = false, hasShare = false, shareUrl = (string)null });
                return;
            }

            if (help) { share = false; elevate = false; }
            if (!share || elevate) token = null; // the token only applies to uploads, and can't be passed to an elevated process

            string args = BuildSysInfoArgs(help, share);
            var output = new StringBuilder();
            string error = null;
            int exitCode = -1;

            _sysInfoCancelRequested = false;
            _sysInfoLastReport = null;
            _sysInfoLastShareUrl = null;
            SysInfoPost(core, new { type = "runState", running = true, cancellable = !elevate });

            try
            {
                exitCode = elevate
                    ? await RunSysInfoElevatedAsync(core, args, output)
                    : await RunSysInfoAsync(core, args, token, output);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                _sysInfoProcess = null;
            }

            if (_sysInfoCancelRequested) error = "Cancelled.";

            var text = output.ToString();
            _sysInfoLastReport = FindSysInfoReport(text);
            _sysInfoLastShareUrl = FindSysInfoShareUrl(text);

            SysInfoPost(core, new
            {
                type = "done",
                exitCode,
                error,
                hasReport = _sysInfoLastReport != null,
                hasShare = _sysInfoLastShareUrl != null,
                shareUrl = _sysInfoLastShareUrl
            });
        }

        private static string CleanSysInfoLine(string line) => AnsiPattern.Replace(line, "");

        private async Task<int> RunSysInfoAsync(CoreWebView2 core, string args, string token, StringBuilder output)
        {
            var psi = new ProcessStartInfo
            {
                FileName = SysInfoExePath,
                Arguments = args,
                WorkingDirectory = SysInfoDir, // reports are saved next to the exe
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(token)) psi.Environment["SYSINFO_TOKEN"] = token;

            void OnLine(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                var line = CleanSysInfoLine(e.Data) + "\n";
                lock (output) output.Append(line);
                SysInfoPost(core, new { type = "output", text = line });
            }

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += OnLine;
            p.ErrorDataReceived += OnLine;

            p.Start();
            _sysInfoProcess = p;
            p.StandardInput.Close(); // never leave it waiting on a keypress
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                var msg = "Timed out after 3 minutes and was stopped.\n";
                lock (output) output.Append(msg);
                SysInfoPost(core, new { type = "output", text = msg });
            }

            // The parameterless overload also waits for the redirected streams to drain.
            await Task.Run(() => p.WaitForExit());
            return p.ExitCode;
        }

        // Elevated runs go through "cmd /c ... > log" because output can't be redirected
        // from a process launched with the "runas" verb. The log is shown once it exits.
        private async Task<int> RunSysInfoElevatedAsync(CoreWebView2 core, string args, StringBuilder output)
        {
            var log = Path.Combine(SysInfoDir, "last-run.log");
            try { if (File.Exists(log)) File.Delete(log); } catch { }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{SysInfoExePath}\" {args} > \"{log}\" 2>&1\"",
                WorkingDirectory = SysInfoDir,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("Administrator permission was declined.");
            }

            if (p == null) throw new InvalidOperationException("Couldn't start SysInfo.");

            using (p)
            {
                _sysInfoProcess = p;
                SysInfoPost(core, new { type = "output", text = "Running as administrator - output will appear when it finishes…\n" });

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // An elevated process can't be killed from here; just stop waiting.
                    throw new TimeoutException("SysInfo is still running after 3 minutes; stopped waiting for it.");
                }

                string text = "";
                try
                {
                    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    text = CleanSysInfoLine(reader.ReadToEnd());
                }
                catch { }

                if (text.Length > 0)
                {
                    if (!text.EndsWith("\n")) text += "\n";
                    lock (output) output.Append(text);
                    SysInfoPost(core, new { type = "output", text });
                }

                return p.ExitCode;
            }
        }

        // ================================================================
        // Results
        // ================================================================

        // SysInfo prints "Report saved to: <path>". Only trust a path that really is a
        // .md file inside our own SysInfo folder.
        private string FindSysInfoReport(string text)
        {
            var m = Regex.Match(text, @"Report saved to:\s*(.+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            try
            {
                var full = Path.GetFullPath(m.Groups[1].Value.Trim());
                var root = Path.GetFullPath(SysInfoDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetExtension(full), ".md", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(full))
                {
                    return full;
                }
            }
            catch { }
            return null;
        }

        private static string FindSysInfoShareUrl(string text)
        {
            var m = Regex.Match(text, @"https://" + Regex.Escape(SysInfoShareHost) + @"/\S+");
            if (!m.Success) return null;

            var candidate = m.Value.TrimEnd('.', ',', ')', ';', '"', '\'');
            return Uri.TryCreate(candidate, UriKind.Absolute, out var u) &&
                   u.Scheme == Uri.UriSchemeHttps &&
                   string.Equals(u.Host, SysInfoShareHost, StringComparison.OrdinalIgnoreCase)
                ? u.AbsoluteUri
                : null;
        }

        private void SysInfoOpenFolder()
        {
            try
            {
                Directory.CreateDirectory(SysInfoDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SysInfoDir}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void SysInfoOpenReport()
        {
            var path = _sysInfoLastReport;
            if (path == null || !File.Exists(path)) return;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // No app registered for .md - show it in Explorer instead.
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
                catch { }
            }
        }

        // ================================================================
        // The page
        // ================================================================

        private string BuildSysInfoPageHtml()
        {
            return SysInfoPageTemplate
                .Replace("%%THEME%%", ThemeManager.BuildCssVariables())
                .Replace("%%FAVICON%%", LotusFaviconDataUri())
                .Replace("%%LOGO%%", LotusSvg(40));
        }

        // Plain (non-interpolated) raw string, so CSS/JS braces need no escaping.
        // Visual style matches lotus://settings.
        private const string SysInfoPageTemplate = """
<!DOCTYPE html><html><head><meta charset='utf-8'><title>SysInfo</title>
<link rel='icon' type='image/svg+xml' href="data:image/svg+xml,%%FAVICON%%">
<style id='lotus-theme'>%%THEME%%</style>
<style>
body { background:var(--surface-secondary); font-family:'Segoe UI',sans-serif; display:flex; flex-direction:column;
       align-items:center; padding:70px 20px 60px; margin:0; color:var(--text-primary); }
.logo { display:flex; align-items:center; gap:14px; margin-bottom:36px; }
.logo span { font-size:32px; font-weight:600; color:var(--accent-dark); letter-spacing:.5px; }
.page { width:100%; max-width:640px; }
.card { background:var(--hover-soft); border-radius:14px; padding:20px 22px; margin-bottom:20px;
        box-shadow:0 1px 4px var(--shadow); }
.card h2 { margin:0 0 14px; font-size:14px; font-weight:600; color:var(--accent-dark);
           text-transform:uppercase; letter-spacing:.5px; }
.muted { font-size:12.5px; color:var(--text-muted); margin:2px 0 12px; }
.status { font-size:14px; margin:0 0 4px; }
.status.error { color:var(--error); }
.versions { font-size:12.5px; color:var(--text-muted); margin-bottom:14px; }
.bar { height:8px; background:var(--hover-pink); border-radius:4px; overflow:hidden; margin:10px 0 6px; }
.fill { height:100%; width:0; background:var(--accent); transition:width .1s linear; }
.fill.indeterminate { width:40%; animation:slide 1.1s ease-in-out infinite alternate; }
@keyframes slide { from { margin-left:0; } to { margin-left:60%; } }
.opt { display:flex; gap:12px; align-items:flex-start; padding:10px 0; cursor:pointer; }
.opt.disabled { opacity:.45; cursor:default; }
.opt input { accent-color:var(--accent); width:18px; height:18px; margin:2px 0 0; cursor:inherit; flex:none; }
.opt .t { font-size:14px; }
.opt code, .cmd code { font-family:Consolas,'Cascadia Mono',monospace; font-size:12.5px; background:var(--surface-secondary);
                       border-radius:6px; padding:1px 6px; color:var(--accent-dark); }
.opt small { display:block; font-size:12.5px; color:var(--text-muted); margin-top:3px; }
details { margin:6px 0 4px; }
summary { font-size:13px; color:var(--accent-dark); cursor:pointer; padding:6px 0; }
.field { display:flex; flex-direction:column; gap:4px; margin:6px 0 6px 0; font-size:13px; }
.field input { height:34px; border-radius:17px; border:1px solid var(--input-border); padding:0 14px; font-size:13px;
               background:var(--surface-softer); outline:none; font-family:inherit; }
.field input:disabled { opacity:.5; }
.cmd { display:flex; align-items:center; gap:10px; background:var(--surface-softer); border:1px solid var(--input-border);
       border-radius:10px; padding:10px 12px; margin:14px 0 8px; }
.cmd code { flex:1; background:none; padding:0; font-size:13.5px; color:var(--text-primary); overflow-x:auto; white-space:nowrap; }
.actions { display:flex; flex-wrap:wrap; gap:10px; margin-top:14px; }
button { font-family:inherit; font-size:13px; border:none; border-radius:18px; padding:9px 18px; cursor:pointer; }
button:disabled { opacity:.45; cursor:default; }
.accent-btn { background:var(--accent); color:var(--on-accent); }
.accent-btn:hover:not(:disabled) { background:var(--accent-hover); }
.ghost-btn { background:var(--hover-pink); color:var(--accent-dark); }
.ghost-btn:hover:not(:disabled) { background:var(--ghost-hover); }
pre { margin:0; padding:12px 14px; background:var(--surface-softer); border:1px solid var(--input-border); border-radius:10px;
      font-family:Consolas,'Cascadia Mono',monospace; font-size:12.5px; line-height:1.5;
      max-height:340px; overflow:auto; white-space:pre-wrap; word-break:break-word; min-height:60px; }
pre:empty::before { content:'Output will appear here.'; color:var(--text-muted); }
[hidden] { display:none !important; }
</style></head><body>
<div class='logo'>%%LOGO%%<span>SysInfo</span></div>
<div class='page'>

  <div class='card'>
    <h2>Lotus SysInfo</h2>
    <p class='status' id='statusText'>Checking for the latest version…</p>
    <div class='versions' id='versions'></div>
    <div class='bar' id='bar' hidden><div class='fill' id='fill'></div></div>
    <p class='muted' id='progressText' hidden></p>
    <p class='muted'>Collects your hardware and OS specs into a shareable Markdown report. The newest release is downloaded from
       GitHub automatically.</p>
    <button class='ghost-btn' id='checkBtn' onclick='ensure()'>Check for updates</button>
  </div>

  <div class='card'>
    <h2>Options</h2>

    <label class='opt' id='rowShare'>
      <input type='checkbox' id='optShare' onchange='updateUi()'>
      <div><span class='t'>Share online</span> <code>--share-online</code>
        <small>Also upload the report to share.browselotus.net and get a link anyone can open.</small></div>
    </label>

    <label class='opt' id='rowHelp'>
      <input type='checkbox' id='optHelp' onchange='updateUi()'>
      <div><span class='t'>Show help</span> <code>-h</code>
        <small>Print SysInfo's usage text and exit, without collecting anything.</small></div>
    </label>

    <label class='opt' id='rowAdmin'>
      <input type='checkbox' id='optAdmin' onchange='updateUi()'>
      <div><span class='t'>Run as administrator</span>
        <small>Gives fuller TPM and Secure Boot details. Windows will ask for permission, and the output appears once it finishes.</small></div>
    </label>

    <details>
      <summary>Advanced</summary>
      <label class='field'>
        <span>Upload token <code>SYSINFO_TOKEN</code> (optional)</span>
        <input type='password' id='token' autocomplete='off' placeholder='Sent as a Bearer token with the upload' oninput='updateUi()'>
      </label>
      <p class='muted'>Only used together with Share online. Passed to SysInfo as an environment variable and never saved.</p>
    </details>

    <div class='cmd'><code id='cmdPreview'>SysInfo.exe</code>
      <button class='ghost-btn' onclick='copyText($("cmdPreview").textContent, this)'>Copy</button></div>
    <p class='muted' id='cmdNote'></p>
    <p class='muted' id='shareNote' hidden>Sharing uploads the report (hardware specs; computer name, username, domain, profile path and MAC addresses are filtered out).
       Anyone who has the link can view it.</p>

    <div class='actions'>
      <button class='accent-btn' id='runBtn' onclick='run()' disabled>Run SysInfo</button>
      <button class='ghost-btn' id='cancelBtn' onclick='post("sysinfoCancel")' hidden>Cancel</button>
      <button class='ghost-btn' onclick='post("sysinfoOpenFolder")'>Open reports folder</button>
    </div>
  </div>

  <div class='card'>
    <h2>Output</h2>
    <pre id='out'></pre>
    <div class='actions'>
      <button class='ghost-btn' id='openReportBtn' onclick='post("sysinfoOpenReport")' hidden>Open report</button>
      <button class='ghost-btn' id='openShareBtn' onclick='post("sysinfoOpenShare")' hidden>Open share link</button>
      <button class='ghost-btn' id='copyShareBtn' hidden>Copy share link</button>
      <button class='ghost-btn' onclick='copyText($("out").textContent, this)'>Copy output</button>
    </div>
  </div>
</div>

<script>
const $ = id => document.getElementById(id);
const ui = { ready:false, busy:true, running:false, cancellable:false };
let shareUrl = null;

function post(action, extra) {
  window.chrome.webview.postMessage(JSON.stringify(Object.assign({ action: action }, extra || {})));
}

function ensure() { ui.busy = true; setStatus('checking', 'Checking for the latest version…'); updateUi(); post('sysinfoEnsure'); }

function currentArgs() {
  if ($('optHelp').checked) return ['-h'];
  if ($('optShare').checked) return ['--share-online'];
  return [];
}

function updateUi() {
  const help = $('optHelp').checked, share = $('optShare').checked, admin = $('optAdmin').checked;
  const hasToken = $('token').value.length > 0;

  $('optShare').disabled = help;
  $('rowShare').classList.toggle('disabled', help);
  $('optAdmin').disabled = help;
  $('rowAdmin').classList.toggle('disabled', help);
  $('token').disabled = !share || help || admin;

  $('cmdPreview').textContent = ['SysInfo.exe'].concat(currentArgs()).join(' ');
  const notes = [];
  if (admin && !help) notes.push('Runs elevated (UAC prompt).');
  if (share && !help && hasToken && !admin) notes.push('SYSINFO_TOKEN will be set for this run.');
  if (share && !help && hasToken && admin) notes.push('The upload token is ignored when running as administrator.');
  $('cmdNote').textContent = notes.join(' ');
  $('cmdNote').hidden = notes.length === 0;
  $('shareNote').hidden = !(share && !help);

  const locked = ui.busy || ui.running;
  $('runBtn').disabled = !ui.ready || locked;
  $('runBtn').textContent = ui.running ? 'Running…' : 'Run SysInfo';
  $('checkBtn').disabled = locked;
  $('cancelBtn').hidden = !ui.running;
  $('cancelBtn').disabled = !ui.cancellable;
}

function setStatus(state, message, installed, latest) {
  const el = $('statusText');
  el.textContent = message || '';
  el.className = 'status' + (state === 'error' ? ' error' : '');
  const parts = [];
  if (installed) parts.push('Installed: ' + installed);
  if (latest) parts.push('Latest: ' + latest);
  $('versions').textContent = parts.join('  ·  ');
  const downloading = state === 'downloading';
  $('bar').hidden = !downloading;
  $('progressText').hidden = !downloading;
  if (downloading) { $('fill').style.width = '0'; $('fill').classList.add('indeterminate'); $('progressText').textContent = ''; }
}

function mb(n) { return (n / 1048576).toFixed(1) + ' MB'; }

function run() {
  const help = $('optHelp').checked;
  $('out').textContent = '';
  shareUrl = null;
  ['openReportBtn', 'openShareBtn', 'copyShareBtn'].forEach(id => { $(id).hidden = true; });
  ui.running = true; ui.cancellable = !($('optAdmin').checked && !help);
  updateUi();
  post('sysinfoRun', {
    help: help,
    share: !help && $('optShare').checked,
    elevate: !help && $('optAdmin').checked,
    token: $('token').value
  });
}

function append(text) {
  const out = $('out');
  out.textContent += text;
  out.scrollTop = out.scrollHeight;
}

async function copyText(text, btn) {
  const original = btn.textContent;
  try {
    await navigator.clipboard.writeText(text);
    btn.textContent = 'Copied';
  } catch (e) {
    btn.textContent = 'Copy failed';
  }
  setTimeout(() => { btn.textContent = original; }, 1500);
}

$('copyShareBtn').onclick = function () { if (shareUrl) copyText(shareUrl, this); };

window.chrome.webview.addEventListener('message', ev => {
  let m;
  try { m = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data; } catch (e) { return; }
  if (!m || !m.type) return;

  switch (m.type) {
    case 'status':
      ui.busy = (m.state === 'checking' || m.state === 'downloading');
      ui.ready = (m.state === 'ready') || (ui.ready && ui.busy);
      if (m.state === 'error') ui.ready = false;
      setStatus(m.state, m.message, m.installed, m.latest);
      updateUi();
      break;

    case 'progress':
      if (m.total > 0) {
        $('fill').classList.remove('indeterminate');
        $('fill').style.width = Math.min(100, m.received * 100 / m.total) + '%';
        $('progressText').textContent = mb(m.received) + ' of ' + mb(m.total);
      } else {
        $('progressText').textContent = mb(m.received);
      }
      break;

    case 'runState':
      ui.running = !!m.running; ui.cancellable = !!m.cancellable;
      if (ui.running) { ui.busy = false; ui.ready = true; }
      updateUi();
      break;

    case 'output':
      append(m.text);
      break;

    case 'done':
      ui.running = false; ui.cancellable = false;
      if (m.error) append('\n' + m.error + '\n');
      else append('\n[Finished, exit code ' + m.exitCode + ']\n');
      $('openReportBtn').hidden = !m.hasReport;
      shareUrl = m.hasShare ? m.shareUrl : null;
      $('openShareBtn').hidden = !m.hasShare;
      $('copyShareBtn').hidden = !m.hasShare;
      updateUi();
      break;
  }
});

updateUi();
post('sysinfoEnsure');
</script>
</body></html>
""";
    }
}
