using System;
using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace TaskbarWidget;

internal static class AutoStartHelper
{
    private const string RunKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TaskbarWidget";

    // ── Public API ─────────────────────────────────────────────────────────────

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
        catch { /* silently ignore — user can retry via context menu */ }
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

    /// <summary>
    /// On first launch, prompt for auto-start preference.
    /// </summary>
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
