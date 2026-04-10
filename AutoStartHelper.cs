using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TaskbarWidget
{
    internal static class AutoStartHelper
    {
        private const string RunKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "TaskbarWidget";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(AppName) is not null;
            }
            catch { return false; }
        }

        public static void Enable()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule!.FileName;
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.SetValue(AppName, $"\"{exe}\"");
            }
            catch { }
        }

        public static void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch { }
        }

        public static void PromptIfNeeded(WidgetConfig config)
        {
            if (!config.AutoStartPromptShown)
            {
                var result = MessageBox.Show(
                    "Would you like TaskbarWidget to start automatically when you log in to Windows?",
                    "TaskbarWidget — First Run",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);

                if (result == MessageBoxResult.Yes)
                    Enable();

                config.AutoStartPromptShown = true;
                ConfigStore.Save(config);
            }
        }
    }
}
