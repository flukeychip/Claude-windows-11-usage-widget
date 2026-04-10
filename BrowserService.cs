using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TaskbarWidget;

/// <summary>
/// Fetches Claude.ai usage via a hidden WebView2 (Edge) window.
/// WebView2 is pre-installed on Windows 11 — no downloads, no Selenium,
/// no ChromeDriver/GeckoDriver. Uses the user's existing Edge session so
/// Cloudflare never challenges us.
/// </summary>
internal static class BrowserService
{
    // Persist the Edge profile so the user stays logged in between clicks
    private static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarWidget", "webview2-profile");

    private static bool _firstRun = !File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarWidget", "wv2-logged-in.flag"));

    public static async Task<(double? Usage, string? ResetTime)> FetchUsageAsync(CancellationToken ct = default)
    {
        // WebView2 must run on the UI thread
        return await Application.Current.Dispatcher.InvokeAsync(
            async () => await FetchOnUiThread(ct)).Task.Unwrap();
    }

    private static async Task<(double? Usage, string? ResetTime)> FetchOnUiThread(CancellationToken ct)
    {
        Window? hiddenWindow = null;
        WebView2? webView = null;

        try
        {
            // Create an off-screen invisible window to host WebView2
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

            // Show minimally — WebView2 needs a real HWND
            hiddenWindow.Show();

            // Initialize with persistent profile (keeps login cookies)
            Directory.CreateDirectory(UserDataDir);
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: UserDataDir);
            await webView.EnsureCoreWebView2Async(env);

            // Suppress dialogs, popups and new windows
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled  = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled             = false;
            webView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;

            if (_firstRun)
            {
                // Show login window, wait for user to sign in
                WriteDebugLog("First run — showing login window");
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

                // Poll until URL leaves /login
                var loginDeadline = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < loginDeadline && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                    var url = webView.CoreWebView2.Source ?? "";
                    WriteDebugLog($"Login poll: {url}");
                    if (!url.Contains("/login") && !url.Contains("/auth") && url.Contains("claude.ai"))
                        break;
                }

                var flagPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TaskbarWidget", "wv2-logged-in.flag");
                File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
                _firstRun = false;

                // Go invisible
                hiddenWindow.Opacity     = 0;
                hiddenWindow.Width       = 1;
                hiddenWindow.Height      = 1;
                hiddenWindow.Left        = -32000;
                hiddenWindow.Top         = -32000;
                hiddenWindow.WindowStyle = WindowStyle.None;
                webView.Width            = 1;
                webView.Height           = 1;
            }

            // Navigate to usage page, poll immediately after nav completes
            var navTcs = new TaskCompletionSource<bool>();
            webView.CoreWebView2.NavigationCompleted += (s, e) => navTcs.TrySetResult(true);
            webView.CoreWebView2.Navigate("https://claude.ai/settings/usage");
            await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(12), ct));
            WriteDebugLog($"On usage page. URL={webView.Source}");

            // Poll until React renders the "XX% used" text (up to 12 seconds)
            double? result    = null;
            string? resetTime = null;
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var raw = await webView.CoreWebView2.ExecuteScriptAsync(@"
                    (function() {
                        var pct = null, reset = null;
                        for (const el of document.querySelectorAll('p,span,div')) {
                            const t = el.textContent.trim();
                            if (!pct) {
                                const m = t.match(/(\d{1,3})%\s+used/i);
                                if (m && t.length < 50) pct = parseInt(m[1], 10);
                            }
                            if (!reset) {
                                const r = t.match(/Resets\s+in\s+([\d]+\s+hr\s+[\d]+\s+min|[\d]+\s+min|[\d]+\s+hr)/i);
                                if (r && t.length < 60) reset = r[1].trim();
                            }
                        }
                        if (pct === null) return null;
                        return JSON.stringify({ pct: pct, reset: reset });
                    })()
                ");

                WriteDebugLog($"JS result: {raw}");

                if (raw != "null" && raw != null)
                {
                    var json = raw.Trim('"').Replace("\\\"", "\"");
                    // parse simple JSON manually to avoid extra deps
                    var pctMatch   = System.Text.RegularExpressions.Regex.Match(json, @"""pct""\s*:\s*(\d+)");
                    var resetMatch = System.Text.RegularExpressions.Regex.Match(json, @"""reset""\s*:\s*""([^""]+)""");
                    if (pctMatch.Success &&
                        double.TryParse(pctMatch.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        result    = Math.Clamp(pct / 100.0, 0.0, 1.0);
                        resetTime = resetMatch.Success ? resetMatch.Groups[1].Value : null;
                        break;
                    }
                }

                await Task.Delay(500, ct);
            }

            WriteDebugLog(result.HasValue ? $"Usage: {result.Value:P0}, Reset: {resetTime}" : "Not found");
            return (result, resetTime);
        }
        catch (Exception ex)
        {
            WriteDebugLog($"FetchOnUiThread exception: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
        finally
        {
            try { webView?.Dispose(); }  catch { }
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
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{content}\n\n");
        }
        catch { }
    }
}
