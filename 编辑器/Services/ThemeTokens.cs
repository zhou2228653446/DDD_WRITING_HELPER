using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace 编辑器.Services
{
    /// <summary>
    /// 把 <see cref="ThemePreset"/> 翻译成一组设计 token，并写入
    /// <see cref="Application.Current"/> 的资源字典顶层。
    ///
    /// 为什么写"顶层"：Application.Resources 的查找顺序是 顶层 → 合并字典（后合并优先），
    /// 因此顶层写入可以覆盖 Themes/Tokens.xaml 里的默认值，且所有
    /// DynamicResource 引用会实时刷新 —— 一处生效，全局联动（含浮动的 AI 面板窗口）。
    ///
    /// 质感策略（纸 / 毛玻璃）：
    ///   面板与编辑区的底色不做成"半透明去透底下"，而是做成
    ///   <see cref="Textured"/> —— 一个把「主题纯色 + 程序生成的纸纹」直接合成进去的
    ///   DrawingBrush（128px 平铺）。
    ///   原因：靠多层 alpha 叠加来"透出"底纹，实测透出率被压到 1% 以下，
    ///   会被 8bit 色深直接量化掉（采样标准差精确为 0），等于没做；
    ///   合成进画刷后强度完全可控，且各区域都能得到一致的纸张质地。
    ///
    /// 配色原则：不用纯白大面积底色、不用纯黑正文、强调色降饱和 —— 消除炫光与刺眼。
    /// </summary>
    public static class ThemeTokens
    {
        /// <summary>当前生效的预设，供控件在代码里取色（如背景图半透明遮罩）。</summary>
        public static ThemePreset? Current { get; private set; }

        // tile 取 256：平铺重复周期越大，大面积上越不容易看出规则图案
        //（128px 时纸纹会出现明显的等距横纹，像屏幕脏了）
        private const int Tile = 256;

        // 纸纹只生成一次；Freeze 后可跨主题共享且省内存
        private static BitmapSource? _paperBitmap;

        /// <summary>纸纹底图（惰性生成一次，固定随机种子 → 主题切换时纹理不会跳动）。</summary>
        private static BitmapSource PaperBitmap => _paperBitmap ??= BuildPaperBitmap();

        public static void Apply(ThemePreset preset)
        {
            if (preset == null) return;
            Current = preset;

            var app = Application.Current;
            if (app == null) return;   // 设计时/单元测试环境

            bool dark = preset.IsDark;

            // ---- 背景层 ----
            // 强度（第二个参数）是量出来的观感比例：正文区略重（纸面），
            // 顶部/状态栏更轻（避免文字被噪点干扰）。
            WriteBrush(app, "Brush.Surface",   Textured(preset.WindowBg,   0.16));
            WriteBrush(app, "Brush.Panel",     Textured(preset.PanelBg,    0.15));
            WriteBrush(app, "Brush.Editor",    Textured(preset.EditorBg,   0.17));
            WriteBrush(app, "Brush.Menu",      Textured(preset.MenuBg,     0.14));
            WriteBrush(app, "Brush.StatusBar", Textured(preset.StatusBarBg, 0.12));
            WriteBrush(app, "Brush.HeaderBg",  Textured(preset.MenuBg,     0.12));   // 分区标题条
            WriteBrush(app, "Brush.SubtleBg",  Textured(preset.MenuBg,     0.10));   // chip / 只读框
            WriteBrush(app, "Brush.InputBg",   Textured(preset.PanelBg,    0.08));

            // ---- 柔光层：左上→右下 的极淡渐变，模拟纸张受光不均 / 玻璃反光 ----
            WriteBrush(app, "Brush.Sheen", BuildSheen(dark));

            // ---- 线 ----
            Write(app, "Brush.Border",     preset.BorderColor);
            Write(app, "Brush.Divider",    preset.BorderColor);
            Write(app, "Brush.Splitter",   preset.SplitterBg);

            // ---- 文字 ----
            Write(app, "Brush.Text",         preset.TextColor);
            Write(app, "Brush.TextMuted",    preset.TextMuted);
            Write(app, "Brush.TextOnAccent", dark ? "#F2EFE9" : "#FBFAF6");

            // ---- 强调 ----
            // hover / pressed 只压暗 4% / 9%：原先是 10% / 22%，按下去像"闪一下"，太跳。
            Write(app, "Brush.Accent",        preset.Accent);
            Write(app, "Brush.AccentHover",   Darken(preset.Accent, 0.94));
            Write(app, "Brush.AccentPressed", Darken(preset.Accent, 0.87));

            // ---- 状态 ----
            // 悬浮/选中态一律用"极低 alpha 的暖色"叠加，而不是灰块或高饱和色块。
            Write(app, "Brush.Hover",    dark ? Alpha("#FFFFFF", 0x12) : Alpha("#6B5539", 0x12));
            Write(app, "Brush.Selected", Alpha(preset.Accent!, (byte)(dark ? 0x2A : 0x20)));
            Write(app, "Brush.Danger",      preset.Danger);
            Write(app, "Brush.DangerHover", Darken(preset.Danger, 0.93));
            Write(app, "Brush.DangerPressed", Darken(preset.Danger, 0.86));
            Write(app, "Brush.Success",     dark ? "#5EA97E" : "#5F8F62");

            // ---- 覆写 HandyControl 皮肤键 ----
            // HandyControl 的控件样式（TabControl 内容区 / Expander / ComboBox / ScrollViewer…）
            // 引用的是它自己那套键，不覆写的话这些控件会一直停在浅色皮肤，
            // 夜间模式下就会出现"面板变深、内容区仍惨白"的割裂。
            WriteBrush(app, "RegionBrush",          Textured(preset.EditorBg, 0.17));  // 内容区/输入区底色
            WriteBrush(app, "SecondaryRegionBrush", Textured(preset.PanelBg,  0.15));  // 次级区域（卡片、列表）
            WriteBrush(app, "ThirdlyRegionBrush",   Textured(preset.MenuBg,   0.12));  // 三级区域（标题条）
            WriteBrush(app, "BackgroundBrush",      Textured(preset.WindowBg, 0.16));
            WriteBrush(app, "DefaultBrush",         Textured(preset.MenuBg,   0.12));
            Write(app, "BorderBrush",          preset.BorderColor);
            Write(app, "PrimaryBorderBrush",   preset.BorderColor);
            Write(app, "SecondaryBorderBrush", preset.BorderColor);
            Write(app, "PrimaryTextBrush",     preset.TextColor);
            Write(app, "SecondaryTextBrush",   preset.TextMuted);
            Write(app, "ThirdlyTextBrush",     preset.TextMuted);
            Write(app, "TitleBrush",           preset.TextColor);
            Write(app, "ReverseTextBrush",     dark ? "#22252A" : "#FBFAF6");
            Write(app, "PrimaryBrush",         preset.Accent);
            Write(app, "AccentBrush",          preset.Accent);
            Write(app, "DarkPrimaryBrush",     Darken(preset.Accent, 0.92));
            Write(app, "DarkAccentBrush",      Darken(preset.Accent, 0.92));
        }

        // ==================== 质感层构造 ====================

        /// <summary>
        /// 生成"主题纯色 + 纸纹"合成画刷（128px 平铺）。
        /// <paramref name="textureOpacity"/> 是纸纹的观感强度（0~1），
        /// 0.10~0.22 是"看得出质地但不脏"的区间。
        /// </summary>
        private static Brush Textured(string hex, double textureOpacity)
        {
            var baseBrush = new SolidColorBrush(Parse(hex));
            baseBrush.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                baseBrush, null, new RectangleGeometry(new Rect(0, 0, Tile, Tile))));

            if (textureOpacity > 0)
            {
                var texture = new DrawingGroup { Opacity = textureOpacity };
                texture.Children.Add(new ImageDrawing(PaperBitmap, new Rect(0, 0, Tile, Tile)));
                group.Children.Add(texture);
            }
            group.Freeze();

            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, Tile, Tile),
                Stretch = Stretch.Fill
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 生成纸纹 tile：极淡的黑白双色颗粒
        /// （浅色主题下显暗点、深色主题下显亮点，天然自适应）。
        /// alpha 上限约 0x2E（≈18%），平均 9% 左右；最终强度由 Textured 的
        /// textureOpacity 再缩放一次。
        ///
        /// 刻意不画"纸纤维"横线：长线条平铺后会形成规则条纹，在编辑区那种
        /// 大面积上极其显眼，观感是"屏幕脏了"而不是"纸"。
        /// </summary>
        private static BitmapSource BuildPaperBitmap()
        {
            const int S = Tile;
            var rnd = new Random(20260925);          // 固定种子
            var px = new byte[S * S * 4];             // BGRA

            // 颗粒
            for (int i = 0; i < S * S; i++)
            {
                int o = i * 4;
                byte v = (byte)(rnd.Next(2) == 0 ? 255 : 0);
                px[o] = v; px[o + 1] = v; px[o + 2] = v;
                px[o + 3] = (byte)rnd.Next(0, 0x2E);
            }

            var bmp = BitmapSource.Create(S, S, 96, 96, PixelFormats.Bgra32, null, px, S * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// 左上→右下 的柔光渐变：模拟纸张受光不均 / 玻璃表面反光。
        /// 深色主题用更弱的光（0x10），否则会发灰。
        /// </summary>
        private static LinearGradientBrush BuildSheen(bool dark)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(dark ? 0x10 : 0x18), 255, 255, 255), 0.0));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x00, 255, 255, 255), 0.62));
            brush.Freeze();
            return brush;
        }

        // ==================== 写入工具 ====================

        private static void Write(Application app, string key, string hex)
        {
            var brush = new SolidColorBrush(Parse(hex));
            brush.Freeze();                       // 冻结后可跨线程共享，且省内存
            app.Resources[key] = brush;
        }

        private static void WriteBrush(Application app, string key, Brush brush)
        {
            app.Resources[key] = brush;
        }

        private static Color Parse(string hex) =>
            (Color)ColorConverter.ConvertFromString(hex)!;

        /// <summary>按比例压暗（用于派生 hover / pressed 态）。</summary>
        private static string Darken(string hex, double factor)
        {
            var c = Parse(hex);
            return $"#{(byte)(c.R * factor):X2}{(byte)(c.G * factor):X2}{(byte)(c.B * factor):X2}";
        }

        /// <summary>加透明度，得到半透明遮罩色。</summary>
        private static string Alpha(string hex, byte a)
        {
            var c = Parse(hex);
            return $"#{a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }
}
