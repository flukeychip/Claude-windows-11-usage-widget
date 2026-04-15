using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TaskbarWidget
{
    internal static class UpdateService
    {
        private const string ApiUrl      = "https://api.github.com/repos/flukeychip/Claude-windows-11-usage-widget/releases/latest";
        private const string ReleasesUrl = "https://github.com/flukeychip/Claude-windows-11-usage-widget/releases/latest";

        /// <summary>
        /// Checks GitHub releases for a newer version.
        /// Returns (true, "v1.2.1") when an update is available.
        /// </summary>
        public static async Task<(bool Available, string? Tag)> CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "TaskbarWidget-AutoUpdate");
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var json = await http.GetStringAsync(ApiUrl);
                var obj  = JObject.Parse(json);

                var tag = obj["tag_name"]?.Value<string>() ?? "";
                if (string.IsNullOrEmpty(tag)) return (false, null);

                var latest      = ParseVersion(tag);
                var current     = Assembly.GetExecutingAssembly().GetName().Version!;
                var currentNorm = new Version(current.Major, current.Minor, Math.Max(0, current.Build));

                if (latest <= currentNorm) return (false, null);

                BrowserService.Log($"Update available: {tag} (current: {currentNorm})");
                return (true, tag);
            }
            catch (Exception ex)
            {
                BrowserService.Log($"Update check failed: {ex.Message}");
                return (false, null);
            }
        }

        /// <summary>
        /// Opens the GitHub releases page in the user's default browser.
        /// Safer than downloading and running an EXE — avoids antivirus blocks.
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
