using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

    internal class SavedCookie
    {
        public string Name    { get; set; } = "";
        public string Value   { get; set; } = "";
        public string Domain  { get; set; } = "";
        public string Path    { get; set; } = "/";
    }

    /// <summary>
    /// Fetches Claude.ai usage.
    /// - First run / expired cookies: WebView2 login → extracts + saves cookies → disposes WebView2
    /// - All subsequent fetches: HttpClient with saved cookies (~15MB idle vs ~68MB)
    /// - Break detection: logs detailed diagnostics + raw HTML to debug file
    /// </summary>
    internal static class BrowserService
    {
        private static readonly string AppDataDir     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarWidget");
        private static readonly string CookieFile     = Path.Combine(AppDataDir, "cookies.json");
        private static readonly string FlagFile       = Path.Combine(AppDataDir, "wv2-logged-in.flag");
        private static readonly string UserDataDir    = Path.Combine(AppDataDir, "webview2-profile");
        private static readonly string DebugLog       = Path.Combine(AppDataDir, "debug.log");
        private static readonly string DebugHtmlFile  = Path.Combine(AppDataDir, "last_response.html");

        // Singleton HttpClient — reused across all fetches (lightweight, ~1MB)
        private static HttpClient? _http;

        public static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchUsageAsync(CancellationToken ct = default)
        {
            return await Application.Current.Dispatcher.InvokeAsync(
                async () => await FetchInternal(ct)).Task.Unwrap();
        }

        public static void Dispose()
        {
            _http?.Dispose();
            _http = null;
        }

        // ── Main fetch logic ──────────────────────────────────────────────────

        private static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchInternal(CancellationToken ct)
        {
            // Have cookies on disk → try HttpClient first
            if (File.Exists(CookieFile))
            {
                var (usage, reset, error) = await FetchViaHttpClient(ct);

                if (error == FetchError.None)
                    return (usage, reset, FetchError.None);

                if (error == FetchError.NetworkError)
                    return (null, null, FetchError.NetworkError);

                // Cookies expired or HTML structure changed → re-auth
                Log($"BREAK: HttpClient returned {error} — clearing cookies, re-authenticating");
                TryDelete(CookieFile);
                _http?.Dispose();
                _http = null;
            }

            // No cookies or just cleared → always force login window (delete stale flag)
            TryDelete(FlagFile);
            var authErr = await AuthenticateWithWebView2(ct);
            if (authErr != FetchError.None) return (null, null, authErr);

            // One fetch with fresh cookies
            return await FetchViaHttpClient(ct);
        }

        // ── HttpClient fetch ──────────────────────────────────────────────────

        private static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchViaHttpClient(CancellationToken ct)
        {
            try
            {
                var cookies = LoadCookies();
                if (cookies == null || cookies.Count == 0)
                {
                    Log("HttpClient: no cookies on disk");
                    return (null, null, FetchError.NotLoggedIn);
                }

                _http ??= CreateHttpClient();

                // Rebuild Cookie header from saved cookies
                var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                _http.DefaultRequestHeaders.Remove("Cookie");
                _http.DefaultRequestHeaders.Add("Cookie", cookieHeader);

                Log($"HttpClient: GET /settings/usage ({cookies.Count} cookies)");

                var response = await _http.GetAsync("https://claude.ai/settings/usage", ct);

                Log($"HttpClient: {(int)response.StatusCode} — final URL: {response.RequestMessage?.RequestUri}");

                // Redirect or 401 = session expired
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
                if (finalUrl.Contains("/login") || finalUrl.Contains("/auth") ||
                    response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Log("BREAK: Session expired (redirected to login or 401/403)");
                    return (null, null, FetchError.NotLoggedIn);
                }

                if (!response.IsSuccessStatusCode)
                {
                    Log($"BREAK: HTTP {(int)response.StatusCode}");
                    return (null, null, FetchError.ServiceDown);
                }

                var html = await response.Content.ReadAsStringAsync();
                Log($"HttpClient: response body length={html.Length}");

                // Always save last response for debugging
                TrySave(DebugHtmlFile, html);

                var (pct, reset) = ParseUsage(html);

                if (pct == null)
                {
                    Log($"BREAK: Could not parse usage percentage from response.\n" +
                        $"  → Check {DebugHtmlFile} for the raw HTML\n" +
                        $"  → Check {DebugLog} for request details\n" +
                        $"  → Likely cause: claude.ai changed their page structure");
                    return (null, null, FetchError.ParseError);
                }

                Log($"HttpClient: parsed pct={pct}%, reset='{reset}'");
                return (pct.Value / 100.0, reset, FetchError.None);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
            {
                Log($"HttpClient: network error — {ex.GetType().Name}: {ex.Message}");
                return (null, null, FetchError.NetworkError);
            }
            catch (Exception ex)
            {
                Log($"HttpClient: unexpected error — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return (null, null, FetchError.ServiceDown);
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

                // Look for numeric percentage near usage-related keys
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

        // ── WebView2 authentication ───────────────────────────────────────────

        private static async Task<FetchError> AuthenticateWithWebView2(CancellationToken ct)
        {
            Log("WebView2: starting authentication");
            Window?   win  = null;
            WebView2? wv   = null;

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
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);
                await wv.EnsureCoreWebView2Async(env);

                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                wv.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;

                bool needsLogin = !File.Exists(FlagFile);

                if (needsLogin)
                {
                    // Show visible login window
                    win.Opacity = 1; win.Width = 600; win.Height = 700;
                    win.Left = (SystemParameters.WorkArea.Width - 600) / 2;
                    win.Top  = (SystemParameters.WorkArea.Height - 700) / 2;
                    win.WindowStyle = WindowStyle.SingleBorderWindow;
                    win.Title = "Sign in to Claude, then close this window";
                    wv.Width = 600; wv.Height = 700;

                    wv.CoreWebView2.Navigate("https://claude.ai/login");

                    var deadline = DateTime.UtcNow.AddMinutes(5);
                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);
                        var url = wv.CoreWebView2.Source ?? "";
                        Log($"WebView2: login poll — {url}");
                        if (url.Contains("claude.ai") && !url.Contains("/login") && !url.Contains("/auth"))
                            break;
                    }

                    File.WriteAllText(FlagFile, DateTime.UtcNow.ToString("O"));
                    win.Opacity = 0; win.Width = 1; win.Height = 1;
                    win.Left = -32000; win.Top = -32000;
                    win.WindowStyle = WindowStyle.None;
                    wv.Width = 1; wv.Height = 1;
                }

                // Navigate to usage page so all auth cookies are set
                var navTcs = new TaskCompletionSource<bool>();
                wv.CoreWebView2.NavigationCompleted += (s, e) => navTcs.TrySetResult(e.IsSuccess);
                wv.CoreWebView2.Navigate("https://claude.ai/settings/usage");
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(12), ct));

                // Extract all claude.ai cookies
                var wvCookies = await wv.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
                var saved = wvCookies.Select(c => new SavedCookie
                {
                    Name   = c.Name,
                    Value  = c.Value,
                    Domain = c.Domain,
                    Path   = c.Path
                }).ToList();

                Log($"WebView2: extracted {saved.Count} cookies from claude.ai");
                SaveCookies(saved);

                return FetchError.None;
            }
            catch (Exception ex)
            {
                Log($"WebView2: auth failed — {ex.GetType().Name}: {ex.Message}");
                return FetchError.NetworkError;
            }
            finally
            {
                // Always dispose WebView2 after auth — not needed anymore
                try { wv?.Dispose(); }  catch { }
                try { win?.Close(); }   catch { }
                Log("WebView2: disposed after auth");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect  = true,
                UseCookies         = false,   // we set Cookie header manually
                MaxAutomaticRedirections = 5,
            };

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            return client;
        }

        private static List<SavedCookie>? LoadCookies()
        {
            try
            {
                if (!File.Exists(CookieFile)) return null;
                var json = File.ReadAllText(CookieFile);
                return JsonConvert.DeserializeObject<List<SavedCookie>>(json);
            }
            catch { return null; }
        }

        private static void SaveCookies(List<SavedCookie> cookies)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(CookieFile, JsonConvert.SerializeObject(cookies, Formatting.Indented));
            }
            catch { }
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
