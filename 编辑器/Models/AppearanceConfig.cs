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

        [JsonIgnore] public Brush WindowBgBrush => new SolidColorBrush(ParseColor(WindowBg));
        [JsonIgnore] public Brush PanelBgBrush => new SolidColorBrush(ParseColor(PanelBg));
        [JsonIgnore] public Brush EditorBgBrush => new SolidColorBrush(ParseColor(EditorBg));
        [JsonIgnore] public Brush MenuBgBrush => new SolidColorBrush(ParseColor(MenuBg));
        [JsonIgnore] public Brush StatusBarBgBrush => new SolidColorBrush(ParseColor(StatusBarBg));
        [JsonIgnore] public Brush TextColorBrush => new SolidColorBrush(ParseColor(TextColor));
        [JsonIgnore] public Brush BorderColorBrush => new SolidColorBrush(ParseColor(BorderColor));
        [JsonIgnore] public Brush SplitterBgBrush => new SolidColorBrush(ParseColor(SplitterBg));

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
