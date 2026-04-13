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
    public enum FetchError { None, NotLoggedIn, NetworkError, ServiceDown, ParseError }

    /// <summary>
    /// Fetches Claude.ai usage via WebView2 (real browser, bypasses Cloudflare).
    /// - First run / session expired: shows login window, user signs in, disposes WebView2
    /// - All fetches: hidden WebView2, same UserDataDir profile (cf_clearance persists), dispose after
    /// - Shared CoreWebView2Environment keeps browser process alive between fetches
    /// - Break detection: raw HTML saved to last_response.html on any failure
    /// </summary>
    internal static class BrowserService
    {
        private static readonly string AppDataDir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarWidget");
        private static readonly string FlagFile      = Path.Combine(AppDataDir, "wv2-logged-in.flag");
        private static readonly string UserDataDir   = Path.Combine(AppDataDir, "webview2-profile");
        private static readonly string DebugLog      = Path.Combine(AppDataDir, "debug.log");
        private static readonly string DebugHtmlFile = Path.Combine(AppDataDir, "last_response.html");

        // Shared environment — keeps browser process alive between fetches to save startup time.
        // Only the WebView2 control (renderer) is created/disposed per fetch.
        private static CoreWebView2Environment? _env;

        public static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchUsageAsync(CancellationToken ct = default)
        {
            return await Application.Current.Dispatcher.InvokeAsync(
                async () => await FetchInternal(ct)).Task.Unwrap();
        }

        public static void Dispose()
        {
            _env = null;
        }

        // ── Main fetch logic ──────────────────────────────────────────────────

        private static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchInternal(CancellationToken ct)
        {
            // Try fetching with existing profile (Cloudflare clearance + auth cookies)
            var result = await FetchViaWebView2(ct);

            if (result.Error == FetchError.NotLoggedIn)
            {
                // Session expired — show login window
                Log("Session expired or not logged in — launching login window");
                var authErr = await AuthenticateWithWebView2(ct);
                if (authErr != FetchError.None) return (null, null, authErr);

                // Retry once with fresh session
                result = await FetchViaWebView2(ct);
            }

            return result;
        }

        // ── WebView2 fetch (hidden, per-fetch) ────────────────────────────────

        private static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchViaWebView2(CancellationToken ct)
        {
            Window?   win = null;
            WebView2? wv  = null;
            try
            {
                // Invisible 1x1 window off-screen
                win = new Window
                {
                    Width = 1, Height = 1, Left = -32000, Top = -32000,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, Opacity = 0,
                    AllowsTransparency = true
                };
                wv = new WebView2 { Width = 1, Height = 1 };
                win.Content = wv;
                win.Show();

                var env = await GetEnvironmentAsync();
                await wv.EnsureCoreWebView2Async(env);

                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                wv.CoreWebView2.Settings.IsScriptEnabled               = true;  // needed for Cloudflare challenge
                wv.CoreWebView2.NewWindowRequested                    += (s, e) => e.Handled = true;

                Log("WebView2 fetch: navigating to /settings/usage");

                // Track all navigations — Cloudflare may cause a redirect chain
                var usagePageLoaded = new TaskCompletionSource<bool>();
                wv.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    var url = wv.CoreWebView2.Source ?? "";
                    Log($"WebView2 fetch: nav completed → {url} (success={e.IsSuccess})");

                    // Signal when we land on the actual usage page (not a challenge or login)
                    if (url.Contains("claude.ai/settings/usage") && e.IsSuccess)
                        usagePageLoaded.TrySetResult(true);
                    else if (url.Contains("/login") || url.Contains("/auth"))
                        usagePageLoaded.TrySetResult(false);  // redirected to login = not logged in
                };

                wv.CoreWebView2.Navigate("https://claude.ai/settings/usage");

                // Wait up to 20s — covers Cloudflare JS challenge + page load
                var completed = await Task.WhenAny(usagePageLoaded.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));

                var finalUrl = wv.CoreWebView2.Source ?? "";
                Log($"WebView2 fetch: final URL = {finalUrl}");

                // Check for login redirect
                if (finalUrl.Contains("/login") || finalUrl.Contains("/auth"))
                {
                    Log("WebView2 fetch: redirected to login — not logged in");
                    return (null, null, FetchError.NotLoggedIn);
                }

                // If we timed out and never hit usage page, check if we're on a Cloudflare page
                if (!usagePageLoaded.Task.IsCompleted || !usagePageLoaded.Task.Result)
                {
                    // Could be a Cloudflare challenge that never resolved, or network error
                    var titleJson = await wv.CoreWebView2.ExecuteScriptAsync("document.title");
                    var title = JsonConvert.DeserializeObject<string>(titleJson) ?? "";
                    Log($"WebView2 fetch: page title = '{title}'");

                    if (title.Contains("Just a moment") || title.Contains("Attention Required"))
                    {
                        Log("BREAK: Cloudflare challenge page — clearance cookie may have expired");
                        return (null, null, FetchError.NetworkError);
                    }
                }

                // Poll every 500ms (up to 20s) until React renders "% used" or "Resets" text
                string html = "";
                string previewText = "";
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    await Task.Delay(500, ct);
                    var textJson = await wv.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                    var innerText = JsonConvert.DeserializeObject<string>(textJson) ?? "";
                    if (attempt % 4 == 0)
                        Log($"WebView2 fetch: poll {attempt+1}, innerText length={innerText.Length}");

                    if (innerText.Contains("% used") || innerText.Contains("Resets") || innerText.Contains("reset"))
                    {
                        previewText = innerText.Length > 300 ? innerText.Substring(0, 300) : innerText;
                        Log($"WebView2 fetch: content found at poll {attempt+1}: {previewText}");
                        var htmlJson2 = await wv.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                        html = JsonConvert.DeserializeObject<string>(htmlJson2) ?? "";
                        break;
                    }
                }

                if (string.IsNullOrEmpty(html))
                {
                    Log("WebView2 fetch: timed out — usage content never appeared. Grabbing raw HTML for diagnostics.");
                    var htmlJson = await wv.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                    html = JsonConvert.DeserializeObject<string>(htmlJson) ?? "";
                }

                Log($"WebView2 fetch: html length = {html.Length}");
                TrySave(DebugHtmlFile, html);

                if (html.Length < 500)
                {
                    Log("BREAK: HTML too short — page likely didn't load");
                    return (null, null, FetchError.ServiceDown);
                }

                var (pct, reset) = ParseUsage(html);

                if (pct == null)
                {
                    Log($"BREAK: Could not parse usage.\n" +
                        $"  → Check {DebugHtmlFile} for the raw HTML\n" +
                        $"  → Check {DebugLog} for request details");
                    return (null, null, FetchError.ParseError);
                }

                Log($"WebView2 fetch: pct={pct}%, reset='{reset}'");
                return (pct.Value / 100.0, reset, FetchError.None);
            }
            catch (OperationCanceledException)
            {
                return (null, null, FetchError.NetworkError);
            }
            catch (Exception ex)
            {
                Log($"WebView2 fetch error: {ex.GetType().Name}: {ex.Message}");
                return (null, null, FetchError.NetworkError);
            }
            finally
            {
                try { wv?.Dispose(); }  catch { }
                try { win?.Close(); }   catch { }
            }
        }

        // ── WebView2 authentication (shows login window) ──────────────────────

        private static async Task<FetchError> AuthenticateWithWebView2(CancellationToken ct)
        {
            Log("WebView2: starting authentication");
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

                var env = await GetEnvironmentAsync();
                await wv.EnsureCoreWebView2Async(env);

                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                wv.CoreWebView2.NewWindowRequested                    += (s, e) => e.Handled = true;

                // Show login window — user signs in fully, then closes
                win.Opacity = 1; win.Width = 600; win.Height = 700;
                win.Left = (SystemParameters.WorkArea.Width  - 600) / 2;
                win.Top  = (SystemParameters.WorkArea.Height - 700) / 2;
                win.WindowStyle  = WindowStyle.SingleBorderWindow;
                win.ShowInTaskbar = true;
                win.Title = "Sign in to Claude, then close this window";
                wv.Width = 600; wv.Height = 700;

                wv.CoreWebView2.Navigate("https://claude.ai/login");

                // Intercept close: cancel it so WebView2 stays alive for cookie check
                var closingTcs = new TaskCompletionSource<bool>();
                bool allowClose = false;
                win.Closing += (s, e) =>
                {
                    if (!allowClose) { e.Cancel = true; closingTcs.TrySetResult(true); }
                };

                await Task.WhenAny(closingTcs.Task, Task.Delay(TimeSpan.FromMinutes(10), ct));

                Log("WebView2: close intercepted — checking session");

                // Verify they're actually logged in by checking current URL
                var currentUrl = wv.CoreWebView2.Source ?? "";
                Log($"WebView2: URL at close = {currentUrl}");

                // Check for auth cookies in profile
                var cookies = await wv.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
                Log($"WebView2: {cookies.Count} cookies in profile after login");

                bool hasSession = cookies.Count > 0 &&
                    System.Linq.Enumerable.Any(cookies, c =>
                        c.Name.Contains("session") || c.Name.Contains("auth") ||
                        c.Name.Contains("user") || c.Name.Contains("token") ||
                        c.Name.StartsWith("__Secure") || c.Name.StartsWith("cf_"));

                allowClose = true;
                try { win?.Close(); } catch { }

                if (cookies.Count == 0)
                {
                    Log("WebView2: no cookies — user may not have signed in");
                    return FetchError.NotLoggedIn;
                }

                File.WriteAllText(FlagFile, DateTime.UtcNow.ToString("O"));
                Log("WebView2: authentication complete");
                return FetchError.None;
            }
            catch (Exception ex)
            {
                Log($"WebView2: auth failed — {ex.GetType().Name}: {ex.Message}");
                return FetchError.NetworkError;
            }
            finally
            {
                try { wv?.Dispose(); }  catch { }
                try { win?.Close(); }   catch { }
                Log("WebView2: disposed after auth");
            }
        }

        // ── HTML parsing ──────────────────────────────────────────────────────

        private static (int? pct, string? reset) ParseUsage(string html)
        {
            int?    pct   = null;
            string? reset = null;

            // Strategy 1: __NEXT_DATA__ JSON blob (Next.js SSR — most reliable)
            var ndMatch = Regex.Match(html,
                @"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>",
                RegexOptions.IgnoreCase);

            if (ndMatch.Success)
            {
                var json = ndMatch.Groups[1].Value;
                Log($"__NEXT_DATA__: found ({json.Length} chars)");

                var pm = Regex.Match(json, @"""(?:percentage|pct|used_percentage|value)""\s*:\s*(\d{1,3})");
                if (pm.Success) pct = int.Parse(pm.Groups[1].Value);

                var rm = Regex.Match(json, @"""(?:reset_at|resetAt|reset_time|resets_at)""\s*:\s*""([^""]+)""");
                if (rm.Success) reset = rm.Groups[1].Value;
            }

            // Strategy 2: "X% used" anywhere in HTML
            if (pct == null)
            {
                var pm = Regex.Match(html, @"(\d{1,3})%\s+used", RegexOptions.IgnoreCase);
                if (pm.Success) pct = int.Parse(pm.Groups[1].Value);
            }

            // Strategy 3: reset time text patterns
            if (reset == null)
            {
                var rm = Regex.Match(html,
                    @"Resets?\s+((?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\w*\s+\d{1,2}:\d{2}\s*(?:AM|PM)?)",
                    RegexOptions.IgnoreCase);
                if (!rm.Success)
                    rm = Regex.Match(html,
                        @"Resets?\s+in\s+([\d]+\s+days?(?:\s+[\d]+\s+hr)?(?:\s+[\d]+\s+min)?|[\d]+\s+hr(?:s|ours?)?(?:\s+[\d]+\s+min)?|[\d]+\s+min(?:utes?)?)",
                        RegexOptions.IgnoreCase);
                if (rm.Success) reset = rm.Groups[1].Value.Trim();
            }

            if (pct == null)
                Log("ParseUsage: all strategies failed — page structure may have changed");

            return (pct, reset);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_env == null)
            {
                Directory.CreateDirectory(UserDataDir);
                _env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);
            }
            return _env;
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        private static void TrySave(string path, string content)
        {
            try { Directory.CreateDirectory(AppDataDir); File.WriteAllText(path, content); }
            catch { }
        }

        internal static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.AppendAllText(DebugLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
            catch { }
        }
    }
}
