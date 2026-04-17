using System;
using System.IO;
using System.Threading;
using System.Windows;
using Newtonsoft.Json;

namespace TaskbarWidget
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private bool _mutexOwned = false;
        private TaskbarWidgetHost? _host;

        protected override void OnStartup(StartupEventArgs e)
        {
            // ── Unhandled exception handlers ──────────────────────────────────
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException               += OnDispatcherUnhandledException;

            // ── Subprocess fetch mode ─────────────────────────────────────────
            // Launched by the main widget process to fetch usage via WebView2.
            // Writes one JSON line to stdout and exits. No UI, no mutex.
            if (e.Args.Length >= 1 && e.Args[0] == "--fetch")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // Queue after the message loop starts (Dispatcher is ready in OnStartup)
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    // WinExe apps have no Console.Out — write directly to the raw stdout handle
                    using var stdout = new System.IO.StreamWriter(
                        Console.OpenStandardOutput(),
                        System.Text.Encoding.UTF8) { AutoFlush = true };

                    try
                    {
                        var (usage, resetTime, isWeekly, error) = await FetchSubprocess.FetchAsync(
                            System.Threading.CancellationToken.None);

                        stdout.WriteLine(JsonConvert.SerializeObject(new
                        {
                            usage,
                            resetTime,
                            isWeekly,
                            error = (int)error
                        }));
                    }
                    catch (Exception ex)
                    {
                        BrowserService.Log($"Subprocess unhandled: {ex.Message}");
                        stdout.WriteLine(JsonConvert.SerializeObject(new
                        {
                            usage     = (double?)null,
                            resetTime = (string?)null,
                            isWeekly  = false,
                            error     = (int)FetchError.NetworkError
                        }));
                    }
                    finally
                    {
                        Shutdown();
                    }
                }));

                return;
            }

            // ── Normal widget mode ────────────────────────────────────────────
            base.OnStartup(e);

            try
            {
                _mutex = new Mutex(true, "TaskbarWidget_b2f4e8a1_SingleInstance", out bool createdNew);
                _mutexOwned = createdNew;

                if (!createdNew)
                {
                    Shutdown();
                    return;
                }

                _host = new TaskbarWidgetHost();
                _host.Initialize();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"TaskbarWidget failed to start:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex      = e.ExceptionObject as Exception;
            var logPath = GetLogPath();
            LogCrash(logPath, ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "Unknown error");
            ShowCrashDialog(logPath);
        }

        private void OnDispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var logPath = GetLogPath();
            LogCrash(logPath, e.Exception.ToString());
            ShowCrashDialog(logPath);
            e.Handled = true; // keep process alive so the dialog is visible
        }

        private static string GetLogPath()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "TaskbarWidget", "debug.log");

        private static void LogCrash(string logPath, string detail)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH\n{detail}\n");
            }
            catch { }
        }

        private static void ShowCrashDialog(string logPath)
        {
            var result = MessageBox.Show(
                "Claude Taskbar Widget ran into a problem and needs to close.\n\n" +
                "A crash log has been saved. Click OK to open the log folder.",
                "Claude Taskbar Widget — Unexpected Error",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Error);

            if (result == MessageBoxResult.OK)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = Path.GetDirectoryName(logPath)!,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            if (_mutexOwned)
            {
                _mutex?.ReleaseMutex();
                _mutexOwned = false;
            }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
