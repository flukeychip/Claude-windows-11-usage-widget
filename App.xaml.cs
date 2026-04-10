using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace TaskbarWidget;

public partial class App : Application
{
    private static Mutex? _mutex;
    private bool _mutexOwned = false;
    private TaskbarWidgetHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            LogDebug("OnStartup: starting");

            // Prevent multiple instances
            _mutex = new Mutex(true, "TaskbarWidget_b2f4e8a1_SingleInstance", out bool createdNew);
            _mutexOwned = createdNew;

            LogDebug($"OnStartup: createdNew={createdNew}");

            if (!createdNew)
            {
                LogDebug("OnStartup: another instance exists, shutting down");
                Shutdown();
                return;
            }

            LogDebug("OnStartup: creating TaskbarWidgetHost");
            _host = new TaskbarWidgetHost();

            LogDebug("OnStartup: calling _host.Initialize()");
            _host.Initialize();

            LogDebug("OnStartup: complete");
        }
        catch (Exception ex)
        {
            LogDebug($"OnStartup exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            System.Windows.MessageBox.Show($"TaskbarWidget failed to start:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static void LogDebug(string msg)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TaskbarWidget", "debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
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
