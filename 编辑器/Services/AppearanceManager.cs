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

        /// <summary>
        /// 内置配色。只描述颜色，质感由 <see cref="BuiltInMaterials"/> 决定，两者自由组合。
        /// </summary>
        public static readonly List<ColorScheme> ColorSchemes = new()
        {
            // 配色三条原则：
            //   1) 不用纯白（#FFF）做大面积底色 —— 换成带一点暖黄的象牙白，消除炫光；
            //   2) 不用纯黑做正文 —— 用暖调深灰，长时间阅读不刺眼；
            //   3) 强调色降饱和降亮度，做成"雾面"而非"荧光"。
            new ColorScheme
            {
                Name = "default-white",
                DisplayName = "温润纸白",
                Description = "象牙纸底，暖调低饱和，长时间阅读不累眼",
                WindowBg = "#EBE6DC", PanelBg = "#F8F5EE", EditorBg = "#FDFBF6",
                MenuBg = "#F4F0E7", StatusBarBg = "#E7E1D6",
                TextColor = "#33302A", BorderColor = "#DCD3C6", SplitterBg = "#D8CFC1",
                Accent = "#4A729C", TextMuted = "#7C7468", Danger = "#C2605C", IsDark = false
            },
            new ColorScheme
            {
                Name = "night-mode",
                DisplayName = "墨色玻璃",
                Description = "中性暖黑底，低对比护眼，夜间写作",
                WindowBg = "#1A1C20", PanelBg = "#22252A", EditorBg = "#282C31",
                MenuBg = "#2A2E34", StatusBarBg = "#17191D",
                TextColor = "#CFCAC2", BorderColor = "#3F444D", SplitterBg = "#3F444D",
                Accent = "#7BA3CC", TextMuted = "#8B9098", Danger = "#D2706B", IsDark = true
            },
            new ColorScheme
            {
                Name = "green-theme",
                DisplayName = "雾绿纸张",
                Description = "灰绿纸调，柔和自然",
                WindowBg = "#E4E8DF", PanelBg = "#EFF3EA", EditorBg = "#FAFBF6",
                MenuBg = "#E1E7DB", StatusBarBg = "#D9E0D2",
                TextColor = "#333A31", BorderColor = "#C5D1BB", SplitterBg = "#C5D1BB",
                Accent = "#5B7F5E", TextMuted = "#6E7A68", Danger = "#BE625C", IsDark = false
            },
            new ColorScheme
            {
                Name = "yellow-theme",
                DisplayName = "暖砂纸卷",
                Description = "砂纸暖调，温润偏黄",
                WindowBg = "#EFE8D7", PanelBg = "#F6F1E5", EditorBg = "#FCFAF3",
                MenuBg = "#ECE3CF", StatusBarBg = "#E1D6BE",
                TextColor = "#3A322A", BorderColor = "#D5C8AD", SplitterBg = "#D5C8AD",
                Accent = "#A0742F", TextMuted = "#857460", Danger = "#BE605A", IsDark = false
            },

            // ---- 以下四套是拆分材质后新增的：既然颜色和质感能自由组，配色就该给足选择 ----
            new ColorScheme
            {
                Name = "ocean-blue",
                DisplayName = "深海蓝",
                Description = "冷调深蓝，沉静专注，适合长时间独处写作",
                WindowBg = "#1B2430", PanelBg = "#222C3A", EditorBg = "#2A3543",
                MenuBg = "#1F2836", StatusBarBg = "#1A222C",
                TextColor = "#D6DEE8", BorderColor = "#35414F", SplitterBg = "#35414F",
                Accent = "#4A90C2", TextMuted = "#8A97A6", Danger = "#D2706B", IsDark = true
            },
            new ColorScheme
            {
                Name = "mist-gray",
                DisplayName = "晨雾灰",
                Description = "中性灰白，不带色偏，最不容易审美疲劳",
                WindowBg = "#E8E9EB", PanelBg = "#F4F5F6", EditorBg = "#FBFBFC",
                MenuBg = "#EFF0F2", StatusBarBg = "#E2E3E6",
                TextColor = "#2E3136", BorderColor = "#D2D5DA", SplitterBg = "#D2D5DA",
                Accent = "#5C6B7A", TextMuted = "#757A82", Danger = "#C2605C", IsDark = false
            },
            new ColorScheme
            {
                Name = "terracotta",
                DisplayName = "赤陶",
                Description = "陶土暖橙，厚重有温度，写人情世故",
                WindowBg = "#EDE2D8", PanelBg = "#F7F0E8", EditorBg = "#FDF8F2",
                MenuBg = "#F2E9DF", StatusBarBg = "#E7DBCF",
                TextColor = "#3D2F26", BorderColor = "#D6C4B2", SplitterBg = "#D6C4B2",
                Accent = "#B5654A", TextMuted = "#8A7263", Danger = "#BE605A", IsDark = false
            },
            new ColorScheme
            {
                Name = "ink-pine",
                DisplayName = "松墨",
                Description = "墨绿近黑，沉得住气，适合严肃题材",
                WindowBg = "#1E2422", PanelBg = "#262D2A", EditorBg = "#2C3431",
                MenuBg = "#222926", StatusBarBg = "#1B211F",
                TextColor = "#D3DAD5", BorderColor = "#3A4440", SplitterBg = "#3A4440",
                Accent = "#6E9B7A", TextMuted = "#8A948E", Danger = "#CC726B", IsDark = true
            },
        };

        /// <summary>
        /// 内置材质。只描述质感，与配色无关。
        ///
        /// ★ 数值是**故意拉开的**，不是随手填的。材质之间如果只差一点点，用户切换时
        /// 会觉得"好像没换"——那就等于白做了。所以三个维度都给了大跨度：
        ///   颗粒 0 → 0.62（纸纹细、绒面粗块、玻璃类几乎没有）
        ///   柔光 0 → 0.62（绒面全哑光、玻璃类高反光）
        ///   透光 0 → 0.45（纸不透、玻璃很透）
        /// 再加上"边缘光"这个强特征：玻璃边缘的折射亮线，磨砂面没有——
        /// 它比柔光更抓眼，是"玻璃感"最强的识别信号。
        /// </summary>
        public static readonly List<Material> BuiltInMaterials = new()
        {
            new Material
            {
                Name = "paper", DisplayName = "纸纹", Kind = MaterialKind.Paper,
                Description = "细颗粒纸面 + 极淡暖光。最像纸的一种，也是默认",
                Texture = 0.30, Sheen = 0.12, Translucency = 0.0,
                Stretched = false, HighGloss = false, EdgeLight = 0.0
            },
            new Material
            {
                Name = "velvet", DisplayName = "绒面", Kind = MaterialKind.Velvet,
                Description = "粗块颗粒 + 几乎零反光。布面精装的手感，完全不反光",
                Texture = 0.44, Sheen = 0.02, Translucency = 0.0,
                Stretched = false, HighGloss = false, EdgeLight = 0.0
            },
            new Material
            {
                Name = "frosted", DisplayName = "毛玻璃", Kind = MaterialKind.Frosted,
                Description = "细磨砂 + 柔和漫射光 + 中等透光。没有边缘亮线",
                Texture = 0.22, Sheen = 0.38, Translucency = 0.22,
                Stretched = true, HighGloss = false, EdgeLight = 0.0
            },
            new Material
            {
                Name = "glass", DisplayName = "清玻璃", Kind = MaterialKind.Glass,
                Description = "几乎无颗粒 + 强反光 + 高透光 + 边缘折射亮线",
                Texture = 0.04, Sheen = 0.52, Translucency = 0.45,
                Stretched = true, HighGloss = false, EdgeLight = 0.35
            },
            new Material
            {
                Name = "liquid", DisplayName = "液态玻璃", Kind = MaterialKind.Liquid,
                Description = "斜向流动高光带 + 强边缘光 + 高透光。光泽最强的一种",
                Texture = 0.02, Sheen = 0.62, Translucency = 0.40,
                Stretched = true, HighGloss = true, EdgeLight = 0.60
            },
            new Material
            {
                Name = "solid", DisplayName = "纯色", Kind = MaterialKind.Solid,
                Description = "完全无质感，没有颗粒也没有光。最省性能也最干净",
                Texture = 0.0, Sheen = 0.0, Translucency = 0.0,
                Stretched = false, HighGloss = false, EdgeLight = 0.0
            },
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
                    var cfg = JsonSerializer.Deserialize<AppearanceConfig>(json, _jsonOptions);
                    return cfg ?? new AppearanceConfig();
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

        public static ColorScheme? GetColorScheme(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return ColorSchemes.Find(p => p.Name == name);
        }

        public static Material? GetMaterial(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return BuiltInMaterials.Find(m => m.Name == name);
        }

        /// <summary>取材质，找不到就退回纸纹（不会因为配置写错就整个外观崩掉）。</summary>
        public static Material ResolveMaterial(string? name) =>
            GetMaterial(name) ?? BuiltInMaterials[0];
    }
}
