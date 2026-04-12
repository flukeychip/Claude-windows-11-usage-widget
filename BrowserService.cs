using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TaskbarWidget
{
    public enum FetchError { None, NotLoggedIn, NetworkError, ServiceDown }

    /// <summary>
    /// Fetches Claude.ai usage via a persistent hidden WebView2 instance.
    /// Kept alive between refreshes — subsequent calls are near-instant.
    /// </summary>
    internal static class BrowserService
    {
        private static readonly string UserDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarWidget", "webview2-profile");

        private static readonly string FlagPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarWidget", "wv2-logged-in.flag");

        private static bool _firstRun = !File.Exists(FlagPath);

        // Persistent WebView2 — survives between refreshes
        private static WebView2?  _webView;
        private static Window?    _hiddenWindow;
        private static bool       _ready = false;

        public static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchUsageAsync(CancellationToken ct = default)
        {
            return await Application.Current.Dispatcher.InvokeAsync(
                async () => await FetchOnUiThread(ct)).Task.Unwrap();
        }

        /// <summary>Call on app shutdown to cleanly release the WebView2.</summary>
        public static void Dispose()
        {
            try { _webView?.Dispose(); }    catch { }
            try { _hiddenWindow?.Close(); } catch { }
            _webView      = null;
            _hiddenWindow = null;
            _ready        = false;
        }

        // ── UI-thread work ────────────────────────────────────────────────────

        private static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchOnUiThread(CancellationToken ct)
        {
            try
            {
                // Init once — reuse on every subsequent call
                if (!_ready)
                {
                    var initError = await InitializeAsync(ct);
                    if (initError != FetchError.None)
                        return (null, null, initError);
                }

                // If already on login page, not logged in
                var currentUrl = _webView!.CoreWebView2.Source ?? "";
                if (currentUrl.Contains("/login") || currentUrl.Contains("/auth"))
                    return (null, null, FetchError.NotLoggedIn);

                // Navigate to usage page
                bool navOk    = false;
                bool navFail  = false;
                var navTcs    = new TaskCompletionSource<bool>();

                void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    navOk   = e.IsSuccess;
                    navFail = !e.IsSuccess;
                    navTcs.TrySetResult(e.IsSuccess);
                }

                _webView.CoreWebView2.NavigationCompleted += OnNavCompleted;
                _webView.CoreWebView2.Navigate("https://claude.ai/settings/usage");
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
                _webView.CoreWebView2.NavigationCompleted -= OnNavCompleted;

                // Check if we were redirected to login
                var finalUrl = _webView.CoreWebView2.Source ?? "";
                if (finalUrl.Contains("/login") || finalUrl.Contains("/auth"))
                    return (null, null, FetchError.NotLoggedIn);

                if (navFail || !navOk)
                    return (null, null, FetchError.ServiceDown);

                // Poll for React-rendered usage data
                double? result   = null;
                string? resetTime = null;
                var deadline = DateTime.UtcNow.AddSeconds(12);

                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    var raw = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                        (function() {
                            var pct = null, reset = null;
                            var all = Array.from(document.querySelectorAll('*'));

                            for (var i = 0; i < all.length && pct === null; i++) {
                                var el = all[i];
                                var t = el.textContent.trim();
                                if (el.children.length > 3 || !/^\d{1,3}%\s+used$/i.test(t)) continue;
                                var node = el;
                                for (var d = 0; d < 10; d++) {
                                    if (!node) break;
                                    if (/current\s+session/i.test(node.textContent)) { pct = parseInt(t.match(/(\d+)/)[1], 10); break; }
                                    node = node.parentElement;
                                }
                            }

                            if (pct === null) {
                                for (var i = 0; i < all.length; i++) {
                                    var t = all[i].textContent.trim();
                                    if (all[i].children.length <= 2 && /^\d{1,3}%\s+used$/i.test(t)) {
                                        pct = parseInt(t.match(/(\d+)/)[1], 10); break;
                                    }
                                }
                            }

                            for (var i = 0; i < all.length && reset === null; i++) {
                                var t = all[i].textContent.trim();
                                if (all[i].children.length > 2 || t.length > 80) continue;
                                var r = t.match(/Resets?\s+((?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\w*\s+\d+:\d+\s*(?:AM|PM)?)/i)
                                     || t.match(/Resets?\s+in\s+([\d]+\s+days?(?:\s+[\d]+\s+hr)?(?:\s+[\d]+\s+min)?|[\d]+\s+hr(?:s|ours?)?(?:\s+[\d]+\s+min)?|[\d]+\s+min(?:utes?)?)/i);
                                if (r) reset = r[1].trim();
                            }

                            if (pct === null) return null;
                            return JSON.stringify({ pct: pct, reset: reset });
                        })()
                    ");

                    if (raw != "null" && raw != null)
                    {
                        var json     = raw.Trim('"').Replace("\\\"", "\"");
                        var pctMatch   = System.Text.RegularExpressions.Regex.Match(json, @"""pct""\s*:\s*(\d+)");
                        var resetMatch = System.Text.RegularExpressions.Regex.Match(json, @"""reset""\s*:\s*""([^""]+)""");
                        if (pctMatch.Success &&
                            double.TryParse(pctMatch.Groups[1].Value,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            result    = Math.Min(Math.Max(pct / 100.0, 0.0), 1.0);
                            resetTime = resetMatch.Success ? resetMatch.Groups[1].Value : null;
                            return (result, resetTime, FetchError.None);
                        }
                    }

                    await Task.Delay(500, ct);
                }

                // Timed out parsing — page loaded but data missing
                return (null, null, FetchError.ServiceDown);
            }
            catch (Exception ex)
            {
                WriteDebugLog($"FetchOnUiThread exception: {ex.GetType().Name}: {ex.Message}");

                // Reset so next call re-initializes
                _ready = false;

                bool isNetwork = ex.Message.Contains("net::") ||
                                 ex.Message.Contains("ERR_NAME") ||
                                 ex.Message.Contains("ERR_INTERNET") ||
                                 ex.Message.Contains("ERR_CONNECTION") ||
                                 ex is System.Net.WebException;

                return (null, null, isNetwork ? FetchError.NetworkError : FetchError.ServiceDown);
            }
        }

        private static async Task<FetchError> InitializeAsync(CancellationToken ct)
        {
            try
            {
                _hiddenWindow = new Window
                {
                    Width         = 1,
                    Height        = 1,
                    Left          = -32000,
                    Top           = -32000,
                    WindowStyle   = WindowStyle.None,
                    ShowInTaskbar = false,
                    Opacity       = 0,
                };

                _webView = new WebView2 { Width = 1, Height = 1 };
                _hiddenWindow.Content = _webView;
                _hiddenWindow.Show();

                Directory.CreateDirectory(UserDataDir);
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                _webView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;

                if (_firstRun)
                {
                    _hiddenWindow.Opacity     = 1;
                    _hiddenWindow.Width       = 600;
                    _hiddenWindow.Height      = 700;
                    _hiddenWindow.Left        = (SystemParameters.WorkArea.Width  - 600) / 2;
                    _hiddenWindow.Top         = (SystemParameters.WorkArea.Height - 700) / 2;
                    _hiddenWindow.WindowStyle = WindowStyle.SingleBorderWindow;
                    _hiddenWindow.Title       = "Sign in to Claude, then close this window";
                    _webView.Width            = 600;
                    _webView.Height           = 700;

                    _webView.CoreWebView2.Navigate("https://claude.ai/login");

                    var loginDeadline = DateTime.UtcNow.AddMinutes(5);
                    while (DateTime.UtcNow < loginDeadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);
                        var url = _webView.CoreWebView2.Source ?? "";
                        if (!url.Contains("/login") && !url.Contains("/auth") && url.Contains("claude.ai"))
                            break;
                    }

                    File.WriteAllText(FlagPath, DateTime.UtcNow.ToString("O"));
                    _firstRun = false;

                    _hiddenWindow.Opacity     = 0;
                    _hiddenWindow.Width       = 1;
                    _hiddenWindow.Height      = 1;
                    _hiddenWindow.Left        = -32000;
                    _hiddenWindow.Top         = -32000;
                    _hiddenWindow.WindowStyle = WindowStyle.None;
                    _webView.Width            = 1;
                    _webView.Height           = 1;
                }

                _ready = true;
                return FetchError.None;
            }
            catch (Exception ex)
            {
                WriteDebugLog($"InitializeAsync exception: {ex.GetType().Name}: {ex.Message}");
                try { _webView?.Dispose(); }    catch { }
                try { _hiddenWindow?.Close(); } catch { }
                _webView = null; _hiddenWindow = null;
                return FetchError.NetworkError;
            }
        }

        private static void WriteDebugLog(string content)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TaskbarWidget", "debug.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}\n");
            }
            catch { }
        }
    }
}
