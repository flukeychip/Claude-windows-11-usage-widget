using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace TaskbarWidget
{
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
