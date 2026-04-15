using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TaskbarWidget
{
    public enum FetchError { None, NotLoggedIn, NetworkError, ServiceDown, ParseError }

    /// <summary>
    /// Spawns the widget exe in --fetch mode to fetch Claude.ai usage via WebView2.
    /// The subprocess owns all WebView2/Chromium memory and frees it on exit.
    /// Main widget process never loads WebView2 DLLs — stays at ~15-20 MB idle.
    /// </summary>
    internal static class BrowserService
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarWidget");
        private static readonly string DebugLog = Path.Combine(AppDataDir, "debug.log");

        public static async Task<(double? Usage, string? ResetTime, FetchError Error)> FetchUsageAsync(
            CancellationToken ct = default)
        {
            var exePath = Process.GetCurrentProcess().MainModule!.FileName;

            var psi = new ProcessStartInfo
            {
                FileName               = exePath,
                Arguments              = "--fetch",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            };

            Log("Subprocess fetch: starting");

            try
            {
                using var proc = Process.Start(psi)!;

                // Auth flow can take up to 10 min (user signing in)
                var readTask = proc.StandardOutput.ReadToEndAsync();
                var timeout  = Task.Delay(TimeSpan.FromMinutes(10), ct);
                var won      = await Task.WhenAny(readTask, timeout);

                if (won == timeout)
                {
                    Log("Subprocess fetch: timed out — killing");
                    try { proc.Kill(); } catch { }
                    return (null, null, FetchError.NetworkError);
                }

                proc.WaitForExit(5000);

                var json = (await readTask).Trim();
                Log($"Subprocess fetch: result = {json}");

                if (string.IsNullOrEmpty(json))
                    return (null, null, FetchError.NetworkError);

                var obj   = JObject.Parse(json);
                var error = (FetchError)(obj["error"]?.Value<int>() ?? 0);

                if (error != FetchError.None)
                    return (null, null, error);

                double? usage     = obj["usage"]?.Value<double>();
                string? resetTime = obj["resetTime"]?.Value<string>();

                return (usage, resetTime, FetchError.None);
            }
            catch (Exception ex)
            {
                Log($"Subprocess fetch error: {ex.GetType().Name}: {ex.Message}");
                return (null, null, FetchError.NetworkError);
            }
        }

        public static void Dispose() { }

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
