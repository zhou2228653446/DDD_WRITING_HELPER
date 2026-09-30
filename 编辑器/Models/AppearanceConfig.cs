using System.Windows.Media;
using System.Text.Json.Serialization;

namespace 编辑器
{
    public class ThemePreset
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";

        public string WindowBg { get; set; } = "#F5F5F5";
        public string PanelBg { get; set; } = "#FFFFFF";
        public string EditorBg { get; set; } = "#FFFFFF";
        public string MenuBg { get; set; } = "#F0F0F0";
        public string StatusBarBg { get; set; } = "#F0F0F0";
        public string TextColor { get; set; } = "#000000";
        public string BorderColor { get; set; } = "#DDDDDD";
        public string SplitterBg { get; set; } = "#DDDDDD";

        /// <summary>强调色：主操作按钮、选中指示条、链接。</summary>
        public string Accent { get; set; } = "#2F6FEB";

        /// <summary>次要文字色：分区标题、指标标签、图标。</summary>
        public string TextMuted { get; set; } = "#6B7280";

        /// <summary>危险色：停止生成等破坏性操作。</summary>
        public string Danger { get; set; } = "#E5484D";

        /// <summary>是否深色主题：决定悬浮态的叠加方向（深色叠白、浅色叠黑）。</summary>
        public bool IsDark { get; set; }

        [JsonIgnore] public Brush WindowBgBrush => new SolidColorBrush(ParseColor(WindowBg));
        [JsonIgnore] public Brush PanelBgBrush => new SolidColorBrush(ParseColor(PanelBg));
        [JsonIgnore] public Brush EditorBgBrush => new SolidColorBrush(ParseColor(EditorBg));
        [JsonIgnore] public Brush MenuBgBrush => new SolidColorBrush(ParseColor(MenuBg));
        [JsonIgnore] public Brush StatusBarBgBrush => new SolidColorBrush(ParseColor(StatusBarBg));
        [JsonIgnore] public Brush TextColorBrush => new SolidColorBrush(ParseColor(TextColor));
        [JsonIgnore] public Brush BorderColorBrush => new SolidColorBrush(ParseColor(BorderColor));
        [JsonIgnore] public Brush SplitterBgBrush => new SolidColorBrush(ParseColor(SplitterBg));
        [JsonIgnore] public Brush AccentColorBrush => new SolidColorBrush(ParseColor(Accent));

        private static Color ParseColor(string hex) =>
            (Color)ColorConverter.ConvertFromString(hex)!;

        public Brush GetSemiTransparentPanelBg(double opacity = 0.88)
        {
            var c = ParseColor(PanelBg);
            return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));
        }
    }

    public class AppearanceConfig
    {
        public string PresetName { get; set; } = "warm-paper";
        public string? BackgroundImagePath { get; set; }
    }
}
