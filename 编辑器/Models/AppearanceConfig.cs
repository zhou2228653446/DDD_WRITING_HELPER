using System.Windows.Media;
using System.Text.Json.Serialization;

namespace 编辑器
{
    /// <summary>
    /// 配色方案 —— **只管颜色，不管质感**。
    ///
    /// 原先叫 ThemePreset，颜色和材质是绑死的：选「温润纸白」就只能配纸纹，
    /// 想要毛玻璃就得有人再抄一套「毛玻璃版纸白」。4 套配色 × 6 种材质全写死就是
    /// 24 个预设，改一个颜色要改 6 处。
    /// 所以拆开：这里只描述颜色，质感交给 <see cref="Material"/>，两者自由组合。
    /// </summary>
    public class ColorScheme
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

        /// <summary>是否深色主题：决定柔光叠加方向（深色叠白、浅色叠黑）与派生色。</summary>
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

    /// <summary>材质的渲染方式。决定画刷怎么合成，与用什么颜色无关。</summary>
    public enum MaterialKind
    {
        /// <summary>纸纹：颗粒平铺，暖调哑光。最"像纸"的一种。</summary>
        Paper,

        /// <summary>绒面：颗粒更重、几乎不反光，手感偏软。</summary>
        Velvet,

        /// <summary>毛玻璃：细颗粒 + 大面积柔光 + 半透光。</summary>
        Frosted,

        /// <summary>清玻璃：反光更强、透光更多，几乎没有颗粒。</summary>
        Glass,

        /// <summary>液态玻璃：斜向高光带 + 边缘光晕，透光且有"厚度感"。</summary>
        Liquid,

        /// <summary>纯色：完全无质感。最省性能，也最干净。</summary>
        Solid,
    }

    /// <summary>
    /// 材质 —— **只管质感，不管颜色**。
    ///
    /// 关于"真毛玻璃"：WPF 没有实时背景模糊的能力。真正的亚克力要靠 DWM 的
    /// SetWindowCompositionAttribute，代价是开启 AllowsTransparency——
    /// 那会让整个窗口走分层渲染路径，长时间码字时性能损失肉眼可见。
    /// 所以这里做的是**视觉模拟**：靠颗粒 + 大面积柔光渐变 + 半透光还原观感；
    /// 配合背景图使用时透出来的就是真实画面，效果最接近真玻璃。
    /// </summary>
    public class Material
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";

        public MaterialKind Kind { get; set; } = MaterialKind.Paper;

        /// <summary>颗粒强度（0~0.35）。超过 0.3 在大面积上会显脏。</summary>
        public double Texture { get; set; }

        /// <summary>柔光强度（0~0.5）。模拟受光不均 / 玻璃反光。</summary>
        public double Sheen { get; set; }

        /// <summary>透光率（0~0.5）。面板透出底层（背景图 / 底色）的程度。</summary>
        public double Translucency { get; set; }

        /// <summary>
        /// 是否用"相对拉伸"而不是平铺。
        /// 玻璃类材质的大面积渐变一旦平铺就会变成重复的斜条纹，所以必须拉伸；
        /// 代价是颗粒会被一起拉大——所以玻璃类材质的颗粒都很弱，本来也不靠它。
        /// </summary>
        public bool Stretched { get; set; }

        /// <summary>是否画液态玻璃那种斜向高光带。</summary>
        public bool HighGloss { get; set; }

        /// <summary>
        /// 边缘光强度（0~1）。
        /// 玻璃边缘会把光折射出一道亮线，磨砂面没有——这是"玻璃感"最强的识别特征，
        /// 比柔光更抓眼。清玻璃给一点、液态玻璃给满，毛玻璃给 0。
        /// </summary>
        public double EdgeLight { get; set; }
    }

    public class AppearanceConfig
    {
        /// <summary>配色名（ColorScheme.Name）。字段名沿用旧的 PresetName，老配置照旧能读。</summary>
        public string PresetName { get; set; } = "default-white";

        /// <summary>材质名（Material.Name）。老配置没有这个字段，默认纸纹。</summary>
        public string MaterialName { get; set; } = "paper";

        /// <summary>
        /// 材质强度倍率（0~2）。1 = 材质设计者调好的默认观感，
        /// 0 = 完全关掉质感（等同纯色），调大则颗粒与反光都更明显。
        /// </summary>
        public double MaterialIntensity { get; set; } = 1.0;

        public string? BackgroundImagePath { get; set; }
    }
}
