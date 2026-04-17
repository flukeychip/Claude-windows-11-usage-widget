using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace TaskbarWidget
{
    internal static class UpdateService
    {
        private const string ApiUrl      = "https://api.github.com/repos/flukeychip/Claude-windows-11-usage-widget/releases/latest";
        private const string ReleasesUrl = "https://github.com/flukeychip/Claude-windows-11-usage-widget/releases/latest";

        /// <summary>
        /// Checks GitHub releases for a newer version.
        /// Returns (Available, Tag, InstallerUrl) — InstallerUrl is the .exe asset if present.
        /// </summary>
        public static async Task<(bool Available, string? Tag, string? InstallerUrl)> CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent",  "TaskbarWidget-AutoUpdate");
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var json = await http.GetStringAsync(ApiUrl);
                var obj  = JObject.Parse(json);

                var tag = obj["tag_name"]?.Value<string>() ?? "";
                if (string.IsNullOrEmpty(tag)) return (false, null, null);

                var latest      = ParseVersion(tag);
                var current     = Assembly.GetExecutingAssembly().GetName().Version!;
                var currentNorm = new Version(current.Major, current.Minor, Math.Max(0, current.Build));

                if (latest <= currentNorm) return (false, null, null);

                // Find the Setup .exe asset in the release
                string? installerUrl = null;
                if (obj["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        var name = asset["name"]?.Value<string>() ?? "";
                        if (name.EndsWith("_Setup.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            installerUrl = asset["browser_download_url"]?.Value<string>();
                            break;
                        }
                    }
                }

                BrowserService.Log($"Update available: {tag} (current: {currentNorm}), installer={installerUrl != null}");
                return (true, tag, installerUrl);
            }
            catch (Exception ex)
            {
                BrowserService.Log($"Update check failed: {ex.Message}");
                return (false, null, null);
            }
        }

        /// <summary>
        /// Prompts the user, downloads the installer to a temp file, runs it silently,
        /// and shuts down the current process. The installer relaunches the widget.
        /// Falls back to opening the releases page if anything fails.
        /// </summary>
        public static async Task DownloadAndInstallAsync(string installerUrl, string tag)
        {
            var confirm = MessageBox.Show(
                $"Version {tag} is ready to install.\n\n" +
                "The widget will close and restart automatically.",
                "Update Available — Claude Taskbar Widget",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                BrowserService.Log($"Downloading update {tag} from {installerUrl}");

                var tempPath = Path.Combine(Path.GetTempPath(), "ClaudeTaskbarWidget_Setup.exe");

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                http.DefaultRequestHeaders.Add("User-Agent", "TaskbarWidget-AutoUpdate");
                var bytes = await http.GetByteArrayAsync(installerUrl);
                File.WriteAllBytes(tempPath, bytes);

                BrowserService.Log($"Download complete ({bytes.Length / 1024} KB), launching installer");

                // /SILENT        — no wizard, brief progress window only
                // /CLOSEAPPLICATIONS — installer will close any running instance
                Process.Start(new ProcessStartInfo
                {
                    FileName        = tempPath,
                    Arguments       = "/SILENT /CLOSEAPPLICATIONS",
                    UseShellExecute = true
                });

                Application.Current?.Dispatcher.Invoke(() => Application.Current?.Shutdown());
            }
            catch (Exception ex)
            {
                BrowserService.Log($"Auto-update failed: {ex.Message}");
                MessageBox.Show(
                    "Download failed — opening the releases page so you can update manually.",
                    "Update Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                OpenReleasesPage();
            }
        }

        /// <summary>
        /// Opens the GitHub releases page in the user's default browser.
        /// Used as a fallback when auto-update is unavailable or fails.
        /// </summary>
        public static void OpenReleasesPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = ReleasesUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                BrowserService.Log($"Failed to open releases page: {ex.Message}");
            }
        }

        private static Version ParseVersion(string tag)
        {
            var s = tag.TrimStart('v', 'V');
            return Version.TryParse(s, out var v)
                ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
                : new Version(0, 0, 0);
        }
    }
}
