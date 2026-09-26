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
            // 配色三条原则：
            //   1) 不用纯白（#FFF）做大面积底色 —— 换成带一点暖黄的象牙白，消除炫光；
            //   2) 不用纯黑做正文 —— 用暖调深灰，长时间阅读不刺眼；
            //   3) 强调色降饱和降亮度，做成"雾面"而非"荧光"。
            // 底色本身是纸纹层的背景，面板会以 94% 左右的透明度叠在纸纹上（见 ThemeTokens）。
            new ThemePreset
            {
                Name = "default-white",
                DisplayName = "温润纸白",
                Description = "象牙纸底，暖调低饱和，长时间阅读不累眼",
                WindowBg = "#EBE6DC", PanelBg = "#F8F5EE", EditorBg = "#FDFBF6",
                MenuBg = "#F4F0E7", StatusBarBg = "#E7E1D6",
                TextColor = "#33302A", BorderColor = "#DCD3C6", SplitterBg = "#D8CFC1",
                Accent = "#4A729C", TextMuted = "#7C7468", Danger = "#C2605C", IsDark = false
            },
            new ThemePreset
            {
                Name = "night-mode",
                DisplayName = "墨色玻璃",
                Description = "中性暖黑底，低对比护眼，夜间写作",
                WindowBg = "#1A1C20", PanelBg = "#22252A", EditorBg = "#282C31",
                MenuBg = "#2A2E34", StatusBarBg = "#17191D",
                TextColor = "#CFCAC2", BorderColor = "#3F444D", SplitterBg = "#3F444D",
                Accent = "#7BA3CC", TextMuted = "#8B9098", Danger = "#D2706B", IsDark = true
            },
            new ThemePreset
            {
                Name = "green-theme",
                DisplayName = "雾绿纸张",
                Description = "灰绿纸调，柔和自然",
                WindowBg = "#E4E8DF", PanelBg = "#EFF3EA", EditorBg = "#FAFBF6",
                MenuBg = "#E1E7DB", StatusBarBg = "#D9E0D2",
                TextColor = "#333A31", BorderColor = "#C5D1BB", SplitterBg = "#C5D1BB",
                Accent = "#5B7F5E", TextMuted = "#6E7A68", Danger = "#BE625C", IsDark = false
            },
            new ThemePreset
            {
                Name = "yellow-theme",
                DisplayName = "暖砂纸卷",
                Description = "砂纸暖调，温润偏黄",
                WindowBg = "#EFE8D7", PanelBg = "#F6F1E5", EditorBg = "#FCFAF3",
                MenuBg = "#ECE3CF", StatusBarBg = "#E1D6BE",
                TextColor = "#3A322A", BorderColor = "#D5C8AD", SplitterBg = "#D5C8AD",
                Accent = "#A0742F", TextMuted = "#857460", Danger = "#BE605A", IsDark = false
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
