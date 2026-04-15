using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TaskbarWidget
{
    public enum FetchError
    {
        None,
        NotLoggedIn,
        NetworkError,
        ServiceDown,
        ParseError,
        WebView2Missing,  // WebView2 runtime not installed on this machine
        Blocked           // Subprocess was killed (antivirus / security software)
    }

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

        // 3 minutes: enough for a user to sign in, short enough to feel responsive
        private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromMinutes(3);

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

            Process? proc = null;
            try
            {
                try
                {
                    proc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    // Can't spawn the subprocess at all — likely blocked by security software
                    Log($"Subprocess fetch: failed to start — {ex.Message}");
                    return (null, null, FetchError.Blocked);
                }

                if (proc == null)
                {
                    Log("Subprocess fetch: Process.Start returned null");
                    return (null, null, FetchError.Blocked);
                }

                var readTask = proc.StandardOutput.ReadToEndAsync();
                var timeout  = Task.Delay(SubprocessTimeout, ct);
                var won      = await Task.WhenAny(readTask, timeout);

                if (won == timeout)
                {
                    Log("Subprocess fetch: timed out — killing");
                    try { proc.Kill(); } catch { }
                    return (null, null, FetchError.NetworkError);
                }

                proc.WaitForExit(5000);

                var json     = (await readTask).Trim();
                var exitCode = proc.ExitCode;
                Log($"Subprocess fetch: exit={exitCode} result={json}");

                // Empty output with non-zero exit usually means AV killed the process
                if (string.IsNullOrEmpty(json))
                {
                    var err = exitCode == 0 ? FetchError.NetworkError : FetchError.Blocked;
                    Log($"Subprocess fetch: empty output, exit={exitCode} → {err}");
                    return (null, null, err);
                }

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
            finally
            {
                try { proc?.Dispose(); } catch { }
            }
        }

        public static void Dispose() { }

        internal static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                PruneLog(DebugLog, maxBytes: 256 * 1024, keepBytes: 64 * 1024);
                File.AppendAllText(DebugLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
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
