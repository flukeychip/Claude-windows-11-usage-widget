using System;
using System.IO;
using System.Text.Json;

namespace TaskbarWidget;

public sealed class WidgetConfig
{
    /// <summary>Last saved widget X position (physical px, taskbar-client-relative).</summary>
    public int? PositionX { get; set; }

    /// <summary>Last saved widget Y position (physical px, taskbar-client-relative).</summary>
    public int? PositionY { get; set; }

    /// <summary>True once the user has been asked about auto-start (even if they said no).</summary>
    public bool AutoStartPromptShown { get; set; }

    /// <summary>Current token usage as a percentage (0-100). User enters manually.</summary>
    public double? UsagePercentage { get; set; }

    /// <summary>
    /// Widget position as a fraction (0.0–1.0) of available taskbar drag space.
    /// DPI-independent — correct across resolution/scale changes.
    /// </summary>
    public double? PositionFraction { get; set; }
}

internal static class ConfigStore
{
    private static readonly string Dir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "TaskbarWidget");

    private static readonly string File = Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions WriteOpts =
        new() { WriteIndented = true };

    public static WidgetConfig Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                var json = System.IO.File.ReadAllText(File);
                return JsonSerializer.Deserialize<WidgetConfig>(json) ?? new WidgetConfig();
            }
        }
        catch { /* corrupt or unreadable — fall through to defaults */ }

        return new WidgetConfig();
    }

    public static void Save(WidgetConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(config, WriteOpts);
            System.IO.File.WriteAllText(File, json);
        }
        catch { /* ignore write failures — position just won't persist */ }
    }
}
