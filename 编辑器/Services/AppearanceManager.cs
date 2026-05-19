using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace 编辑器.Services
{
    public class AppearanceManager
    {
        private readonly string _filePath;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static readonly List<ThemePreset> BuiltInPresets = new()
        {
            new ThemePreset
            {
                Name = "warm-paper",
                DisplayName = "暖色纸页",
                Description = "暖黄基调，模拟纸张阅读体验",
                WindowBg = "#F5E6C8", PanelBg = "#EDD9B5", EditorBg = "#FFF8EF",
                MenuBg = "#E8D5B0", StatusBarBg = "#E8D5B0",
                TextColor = "#3A2A1A", BorderColor = "#D4C4A0", SplitterBg = "#D0BC98"
            },
            new ThemePreset
            {
                Name = "night-bw",
                DisplayName = "夜间黑白",
                Description = "深色界面，适合夜间写作",
                WindowBg = "#1A1A2E", PanelBg = "#16213E", EditorBg = "#FAFAFA",
                MenuBg = "#0F3460", StatusBarBg = "#0F3460",
                TextColor = "#E0E0E0", BorderColor = "#2A2A4A", SplitterBg = "#253050"
            },
            new ThemePreset
            {
                Name = "green-eye",
                DisplayName = "绿色护眼",
                Description = "柔和绿色调，缓解视觉疲劳",
                WindowBg = "#C7EDCC", PanelBg = "#B8D9BE", EditorBg = "#F5FFF5",
                MenuBg = "#A8D0B0", StatusBarBg = "#A8D0B0",
                TextColor = "#2D4A2D", BorderColor = "#9CC4A4", SplitterBg = "#90BA98"
            }
        };

        public AppearanceManager(string configDir)
        {
            _filePath = Path.Combine(configDir, "appearance.json");
        }

        public AppearanceConfig Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<AppearanceConfig>(json, _jsonOptions) ?? new AppearanceConfig();
                }
            }
            catch { }
            return new AppearanceConfig();
        }

        public void Save(AppearanceConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(config, _jsonOptions));
            }
            catch { }
        }

        public static ThemePreset? GetPreset(string name) =>
            BuiltInPresets.Find(p => p.Name == name);
    }
}
