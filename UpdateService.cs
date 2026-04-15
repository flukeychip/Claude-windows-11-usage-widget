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
        private const string ApiUrl = "https://api.github.com/repos/flukeychip/Claude-windows-11-usage-widget/releases/latest";

        /// <summary>
        /// Checks GitHub releases for a newer version.
        /// Returns (true, "v1.2.1", downloadUrl) when an update is available.
        /// </summary>
        public static async Task<(bool Available, string? Tag, string? DownloadUrl)> CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "TaskbarWidget-AutoUpdate");
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var json = await http.GetStringAsync(ApiUrl);
                var obj  = JObject.Parse(json);

                var tag = obj["tag_name"]?.Value<string>() ?? "";
                if (string.IsNullOrEmpty(tag)) return (false, null, null);

                // Compare major.minor.build — ignore revision
                var latest  = ParseVersion(tag);
                var current = Assembly.GetExecutingAssembly().GetName().Version!;
                var currentNorm = new Version(current.Major, current.Minor, Math.Max(0, current.Build));

                if (latest <= currentNorm) return (false, null, null);

                // Find the .exe asset
                string? downloadUrl = null;
                if (obj["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        var name = asset["name"]?.Value<string>() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset["browser_download_url"]?.Value<string>();
                            break;
                        }
                    }
                }

                BrowserService.Log($"Update available: {tag} (current: {currentNorm})");
                return (true, tag, downloadUrl);
            }
            catch (Exception ex)
            {
                BrowserService.Log($"Update check failed: {ex.Message}");
                return (false, null, null);
            }
        }

        /// <summary>
        /// Downloads the installer to %TEMP% and launches it after the widget exits.
        /// </summary>
        public static async Task DownloadAndInstallAsync(string downloadUrl)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "ClaudeTaskbarWidget_Setup.exe");

            BrowserService.Log($"Downloading update: {downloadUrl}");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "TaskbarWidget-AutoUpdate");
            var data = await http.GetByteArrayAsync(downloadUrl);
            File.WriteAllBytes(tempPath, data);

            BrowserService.Log("Download complete — launching installer");

            // PowerShell waits 2s for the widget to exit, then runs the installer silently
            var escaped = tempPath.Replace("'", "''");
            var script  = $"Start-Sleep -Seconds 2; Start-Process -FilePath '{escaped}' -ArgumentList '/VERYSILENT','/NORESTART'";

            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            Application.Current.Shutdown();
        }

        private static Version ParseVersion(string tag)
        {
            // Strips leading "v" or "V", then parses e.g. "1.2.3" → Version(1,2,3)
            var s = tag.TrimStart('v', 'V');
            return Version.TryParse(s, out var v)
                ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
                : new Version(0, 0, 0);
        }
    }
}
