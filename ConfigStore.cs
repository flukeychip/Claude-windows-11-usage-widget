using System;
using System.IO;
using Newtonsoft.Json;

namespace TaskbarWidget
{
    public sealed class WidgetConfig
    {
        public int?    PositionX             { get; set; }
        public int?    PositionY             { get; set; }
        public bool    AutoStartPromptShown  { get; set; }
        public double? UsagePercentage       { get; set; }
        public double? PositionFraction      { get; set; }
    }

    internal static class ConfigStore
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "TaskbarWidget");

        private static readonly string FilePath = Path.Combine(Dir, "config.json");

        public static WidgetConfig Load()
        {
            if (!File.Exists(FilePath)) return new WidgetConfig();
            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<WidgetConfig>(json) ?? new WidgetConfig();
            }
            catch (Exception ex)
            {
                // Corrupted config — delete it so the next save writes clean JSON
                try
                {
                    BrowserService.Log($"Config corrupted, resetting to defaults: {ex.Message}");
                    File.Delete(FilePath);
                }
                catch { }
                return new WidgetConfig();
            }
        }

        public static void Save(WidgetConfig config)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
