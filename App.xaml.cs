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
                        var (usage, resetTime, error) = await FetchSubprocess.FetchAsync(
                            System.Threading.CancellationToken.None);

                        stdout.WriteLine(JsonConvert.SerializeObject(new
                        {
                            usage,
                            resetTime,
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
