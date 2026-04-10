using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TaskbarWidget
{
    internal static class BrowserService
    {
        private static readonly string UserDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarWidget", "webview2-profile");

        private static bool _firstRun = !File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarWidget", "wv2-logged-in.flag"));

        public static async Task<(double? Usage, string? ResetTime)> FetchUsageAsync(CancellationToken ct = default)
        {
            return await Application.Current.Dispatcher.InvokeAsync(
                async () => await FetchOnUiThread(ct)).Task.Unwrap();
        }

        private static async Task<(double? Usage, string? ResetTime)> FetchOnUiThread(CancellationToken ct)
        {
            Window? hiddenWindow = null;
            WebView2? webView = null;

            try
            {
                hiddenWindow = new Window
                {
                    Width         = 1,
                    Height        = 1,
                    Left          = -32000,
                    Top           = -32000,
                    WindowStyle   = WindowStyle.None,
                    ShowInTaskbar = false,
                    Opacity       = 0,
                };

                webView = new WebView2 { Width = 1, Height = 1 };
                hiddenWindow.Content = webView;
                hiddenWindow.Show();

                Directory.CreateDirectory(UserDataDir);
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                webView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;

                if (_firstRun)
                {
                    hiddenWindow.Opacity     = 1;
                    hiddenWindow.Width       = 600;
                    hiddenWindow.Height      = 700;
                    hiddenWindow.Left        = (SystemParameters.WorkArea.Width  - 600) / 2;
                    hiddenWindow.Top         = (SystemParameters.WorkArea.Height - 700) / 2;
                    hiddenWindow.WindowStyle = WindowStyle.SingleBorderWindow;
                    hiddenWindow.Title       = "Sign in to Claude, then close this window";
                    webView.Width            = 600;
                    webView.Height           = 700;

                    webView.CoreWebView2.Navigate("https://claude.ai/login");

                    var loginDeadline = DateTime.UtcNow.AddMinutes(5);
                    while (DateTime.UtcNow < loginDeadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);
                        var url = webView.CoreWebView2.Source ?? "";
                        if (!url.Contains("/login") && !url.Contains("/auth") && url.Contains("claude.ai"))
                            break;
                    }

                    var flagPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "TaskbarWidget", "wv2-logged-in.flag");
                    File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
                    _firstRun = false;

                    hiddenWindow.Opacity     = 0;
                    hiddenWindow.Width       = 1;
                    hiddenWindow.Height      = 1;
                    hiddenWindow.Left        = -32000;
                    hiddenWindow.Top         = -32000;
                    hiddenWindow.WindowStyle = WindowStyle.None;
                    webView.Width            = 1;
                    webView.Height           = 1;
                }

                var navTcs = new TaskCompletionSource<bool>();
                webView.CoreWebView2.NavigationCompleted += (s, e) => navTcs.TrySetResult(true);
                webView.CoreWebView2.Navigate("https://claude.ai/settings/usage");
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(12), ct));

                double? result    = null;
                string? resetTime = null;
                var deadline = DateTime.UtcNow.AddSeconds(12);

                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    var raw = await webView.CoreWebView2.ExecuteScriptAsync(@"
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
                        var json = raw.Trim('"').Replace("\\\"", "\"");
                        var pctMatch   = System.Text.RegularExpressions.Regex.Match(json, @"""pct""\s*:\s*(\d+)");
                        var resetMatch = System.Text.RegularExpressions.Regex.Match(json, @"""reset""\s*:\s*""([^""]+)""");
                        if (pctMatch.Success &&
                            double.TryParse(pctMatch.Groups[1].Value,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            result    = Math.Min(Math.Max(pct / 100.0, 0.0), 1.0);
                            resetTime = resetMatch.Success ? resetMatch.Groups[1].Value : null;
                            break;
                        }
                    }

                    await Task.Delay(500, ct);
                }

                return (result, resetTime);
            }
            catch (Exception ex)
            {
                WriteDebugLog($"FetchOnUiThread exception: {ex.GetType().Name}: {ex.Message}");
                return (null, null);
            }
            finally
            {
                try { webView?.Dispose(); }   catch { }
                try { hiddenWindow?.Close(); } catch { }
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
