using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace TaskbarWidget
{
    /// <summary>
    /// Runs ONLY in --fetch subprocess mode. Never loaded/called from the main widget process.
    /// Contains all WebView2 logic. When this process exits, the OS frees ALL Chromium memory.
    /// </summary>
    internal static class FetchSubprocess
    {
        private static readonly string AppDataDir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarWidget");
        private static readonly string FlagFile      = Path.Combine(AppDataDir, "wv2-logged-in.flag");
        private static readonly string UserDataDir   = Path.Combine(AppDataDir, "webview2-profile");
        private static readonly string DebugLog      = Path.Combine(AppDataDir, "debug.log");
        private static readonly string DebugHtmlFile = Path.Combine(AppDataDir, "last_response.html");

        public static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchAsync(CancellationToken ct)
        {
            var result = await FetchViaWebView2(ct);

            if (result.Error == FetchError.NotLoggedIn)
            {
                Log("Session expired — launching login window");
                var authErr = await AuthenticateWithWebView2(ct);
                if (authErr != FetchError.None) return (null, null, authErr);
                result = await FetchViaWebView2(ct);
            }

            return result;
        }

        // ── Fetch ─────────────────────────────────────────────────────────────

        private static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchViaWebView2(CancellationToken ct)
        {
            Window?   win = null;
            WebView2? wv  = null;
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? navHandler    = null;
            EventHandler<CoreWebView2NewWindowRequestedEventArgs>?  newWinHandler = null;
            try
            {
                win = new Window
                {
                    Width = 1, Height = 1, Left = -32000, Top = -32000,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, Opacity = 0,
                    AllowsTransparency = true
                };
                wv = new WebView2 { Width = 1, Height = 1 };
                win.Content = wv;
                win.Show();

                Directory.CreateDirectory(UserDataDir);

                CoreWebView2Environment env;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);
                }
                catch (Exception ex) when (
                    ex is WebView2RuntimeNotFoundException ||
                    ex.Message.Contains("WebView2") ||
                    ex.Message.Contains("Edge") ||
                    (ex is System.Runtime.InteropServices.COMException com &&
                     (uint)com.HResult == 0x80070002))
                {
                    Log($"WebView2 runtime not found: {ex.Message}");
                    return (null, null, FetchError.WebView2Missing);
                }

                await wv.EnsureCoreWebView2Async(env);

                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                wv.CoreWebView2.Settings.IsScriptEnabled               = true;

                newWinHandler = (s, e) => e.Handled = true;
                wv.CoreWebView2.NewWindowRequested += newWinHandler;

                var usagePageLoaded = new TaskCompletionSource<bool>();
                navHandler = (s, e) =>
                {
                    var url = (s as CoreWebView2)?.Source ?? "";
                    Log($"nav → {url} (success={e.IsSuccess})");
                    if (url.Contains("claude.ai/settings/usage") && e.IsSuccess)
                        usagePageLoaded.TrySetResult(true);
                    else if (url.Contains("/login") || url.Contains("/auth"))
                        usagePageLoaded.TrySetResult(false);
                };
                wv.CoreWebView2.NavigationCompleted += navHandler;

                Log("Navigating to /settings/usage");
                wv.CoreWebView2.Navigate("https://claude.ai/settings/usage");

                await Task.WhenAny(usagePageLoaded.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));

                var finalUrl = wv.CoreWebView2.Source ?? "";

                if (finalUrl.Contains("/login") || finalUrl.Contains("/auth"))
                {
                    Log("Redirected to login — not logged in");
                    return (null, null, FetchError.NotLoggedIn);
                }

                if (!usagePageLoaded.Task.IsCompleted || !usagePageLoaded.Task.Result)
                {
                    var titleJson = await wv.CoreWebView2.ExecuteScriptAsync("document.title");
                    var title = JsonConvert.DeserializeObject<string>(titleJson) ?? "";
                    if (title.Contains("Just a moment") || title.Contains("Attention Required"))
                    {
                        Log("Cloudflare challenge — clearance may have expired");
                        return (null, null, FetchError.NetworkError);
                    }
                }

                // Poll for rendered content — React app may take a moment to hydrate
                string pageText = "";
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(500, ct);
                    var textJson = await wv.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                    var text = JsonConvert.DeserializeObject<string>(textJson) ?? "";
                    if (i % 4 == 0) Log($"Poll {i + 1}, text length={text.Length}");

                    // Accept the page once we see any usage-related content
                    if (ContainsUsageContent(text))
                    {
                        Log($"Content found at poll {i + 1}");
                        pageText = text;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(pageText))
                {
                    Log("Timed out waiting for usage content");
                    TrySave(DebugHtmlFile, "timed out");
                    return (null, null, FetchError.ParseError);
                }

                TrySave(DebugHtmlFile, pageText);

                if (pageText.Length < 50)
                    return (null, null, FetchError.ServiceDown);

                var (pct, reset) = ParseUsage(pageText);
                if (pct == null)
                {
                    Log($"Could not parse usage percentage from text (length={pageText.Length})");
                    return (null, null, FetchError.ParseError);
                }

                Log($"Result: {pct}%, reset='{reset}'");
                return (pct.Value / 100.0, reset, FetchError.None);
            }
            catch (OperationCanceledException) { return (null, null, FetchError.NetworkError); }
            catch (Exception ex)
            {
                Log($"Fetch error: {ex.GetType().Name}: {ex.Message}");
                return (null, null, FetchError.NetworkError);
            }
            finally
            {
                try
                {
                    if (wv?.CoreWebView2 != null)
                    {
                        if (navHandler    != null) wv.CoreWebView2.NavigationCompleted -= navHandler;
                        if (newWinHandler != null) wv.CoreWebView2.NewWindowRequested  -= newWinHandler;
                    }
                }
                catch { }
                try { wv?.Dispose(); } catch { }
                try { win?.Close();  } catch { }
            }
        }

        // ── Auth ──────────────────────────────────────────────────────────────

        private static async Task<FetchError> AuthenticateWithWebView2(CancellationToken ct)
        {
            Log("Starting authentication");
            Window?   win = null;
            WebView2? wv  = null;
            try
            {
                win = new Window
                {
                    Width = 1, Height = 1, Left = -32000, Top = -32000,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, Opacity = 0
                };
                wv = new WebView2 { Width = 1, Height = 1 };
                win.Content = wv;
                win.Show();

                Directory.CreateDirectory(UserDataDir);

                CoreWebView2Environment env;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);
                }
                catch (Exception ex) when (
                    ex is WebView2RuntimeNotFoundException ||
                    ex.Message.Contains("WebView2") ||
                    ex.Message.Contains("Edge") ||
                    (ex is System.Runtime.InteropServices.COMException com &&
                     (uint)com.HResult == 0x80070002))
                {
                    Log($"WebView2 runtime not found during auth: {ex.Message}");
                    return FetchError.WebView2Missing;
                }

                await wv.EnsureCoreWebView2Async(env);

                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                wv.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;

                win.Opacity = 1; win.Width = 600; win.Height = 700;
                win.Left = (SystemParameters.WorkArea.Width  - 600) / 2;
                win.Top  = (SystemParameters.WorkArea.Height - 700) / 2;
                win.WindowStyle   = WindowStyle.SingleBorderWindow;
                win.ShowInTaskbar = true;
                win.Topmost       = true;
                win.Title         = "Sign in to Claude — close this window when done";
                wv.Width = 600; wv.Height = 700;

                wv.CoreWebView2.Navigate("https://claude.ai/login");

                var closingTcs = new TaskCompletionSource<bool>();
                bool allowClose = false;
                win.Closing += (s, e) =>
                {
                    if (!allowClose) { e.Cancel = true; closingTcs.TrySetResult(true); }
                };

                await Task.WhenAny(closingTcs.Task, Task.Delay(TimeSpan.FromMinutes(10), ct));

                var cookies = await wv.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
                Log($"{cookies.Count} cookies after login");

                allowClose = true;
                try { win?.Close(); } catch { }

                if (cookies.Count == 0)
                {
                    Log("No cookies — user may not have signed in");
                    return FetchError.NotLoggedIn;
                }

                File.WriteAllText(FlagFile, DateTime.UtcNow.ToString("O"));
                Log("Authentication complete");
                return FetchError.None;
            }
            catch (Exception ex)
            {
                Log($"Auth error: {ex.GetType().Name}: {ex.Message}");
                return FetchError.NetworkError;
            }
            finally
            {
                try { wv?.Dispose(); } catch { }
                try { win?.Close();  } catch { }
            }
        }

        // ── Parse ─────────────────────────────────────────────────────────────

        // Returns true if the page text looks like it has usage data worth parsing.
        // Accepts English "% used", "Resets in", or any percentage followed by common
        // usage-context words. This is intentionally broad to survive minor UI changes.
        private static bool ContainsUsageContent(string text)
        {
            var lower = text.ToLowerInvariant();
            if (lower.Contains("% used"))    return true;
            if (lower.Contains("resets in")) return true;
            if (lower.Contains("resets") && Regex.IsMatch(text, @"\d{1,3}%")) return true;
            return false;
        }

        private static (int? pct, string? reset) ParseUsage(string text)
        {
            int?    pct   = null;
            string? reset = null;

            // Primary: "55% used"
            var pm = Regex.Match(text, @"(\d{1,3})%\s+used", RegexOptions.IgnoreCase);
            if (pm.Success)
            {
                pct = int.Parse(pm.Groups[1].Value);
            }
            else
            {
                // Fallback: any percentage on the usage settings page is the usage percentage.
                // We're already on /settings/usage so false positives are unlikely.
                var fallback = Regex.Match(text, @"\b(\d{1,3})%");
                if (fallback.Success)
                {
                    var val = int.Parse(fallback.Groups[1].Value);
                    if (val <= 100)
                    {
                        pct = val;
                        Log($"Used fallback percentage parse: {val}%");
                    }
                }
            }

            // Reset time: "Resets in X hr Y min" (relative)
            var rm = Regex.Match(text,
                @"Resets in\s+(\d+\s+hr(?:s)?\s+\d+\s+min|\d+\s+hr(?:s)?|\d+\s+min(?:utes?)?|\d+\s+day(?:s)?(?:\s+\d+\s+hr)?)",
                RegexOptions.IgnoreCase);

            // Reset time: "Resets Mon 2:59 PM" (absolute)
            if (!rm.Success)
                rm = Regex.Match(text,
                    @"Resets\s+((?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec|Mon|Tue|Wed|Thu|Fri|Sat|Sun)\w*\s+[\d:]+\s*(?:AM|PM)?)",
                    RegexOptions.IgnoreCase);

            if (rm.Success) reset = rm.Groups[1].Value.Trim();

            return (pct, reset);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void TrySave(string path, string content)
        {
            try { Directory.CreateDirectory(AppDataDir); File.WriteAllText(path, content); }
            catch { }
        }

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                PruneLog(DebugLog, maxBytes: 256 * 1024, keepBytes: 64 * 1024);
                File.AppendAllText(DebugLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [subprocess] {msg}\n");
            }
            catch { }
        }

        private static void PruneLog(string path, int maxBytes, int keepBytes)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= maxBytes) return;

                using var fs     = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                long      offset = Math.Max(0, fs.Length - keepBytes);
                fs.Seek(offset, SeekOrigin.Begin);

                int b;
                while (offset < fs.Length - 1 && (b = fs.ReadByte()) != -1 && b != '\n') offset++;

                int tail = (int)(fs.Length - fs.Position);
                var buf  = new byte[tail];
                fs.Read(buf, 0, tail);

                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(buf, 0, tail);
                fs.SetLength(tail);
            }
            catch { }
        }
    }
}
