using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace 编辑器.Services
{
    /// <summary>
    /// 把「配色 <see cref="ColorScheme"/> + 材质 <see cref="Material"/>」翻译成一组设计 token，
    /// 并写入 <see cref="Application.Current"/> 的资源字典顶层。
    ///
    /// 为什么写"顶层"：Application.Resources 的查找顺序是 顶层 → 合并字典（后合并优先），
    /// 因此顶层写入可以覆盖 Themes/Tokens.xaml 里的默认值，且所有
    /// DynamicResource 引用会实时刷新 —— 一处生效，全局联动（含浮动的 AI 面板窗口）。
    ///
    /// ── 质感是怎么合成的 ──
    /// 不做"半透明去透底下"那一套：实测透出率被压到 1% 以下，会被 8bit 色深
    /// 直接量化掉（采样标准差精确为 0），等于没做。
    /// 正确做法是把「底色 + 颗粒 + 柔光 + 高光」全部合成进**同一个画刷**：
    ///     <see cref="Surface"/> 用 DrawingGroup 分层叠，最后包成一个 DrawingBrush。
    /// 强度完全可控，各区域得到的质地也一致。
    ///
    /// 平铺 vs 拉伸：
    ///   纸纹/绒面 → 平铺 256px（颗粒清晰，大面积看不出重复）
    ///   玻璃类 → 相对拉伸（大面积渐变一旦平铺就变成重复斜条纹，很难看）
    ///
    /// 配色原则：不用纯白大面积底色、不用纯黑正文、强调色降饱和 —— 消除炫光与刺眼。
    /// </summary>
    public static class ThemeTokens
    {
        /// <summary>当前生效的配色，供控件在代码里取色（如背景图半透明遮罩）。</summary>
        public static ColorScheme? Current { get; private set; }

        /// <summary>当前生效的材质。</summary>
        public static Material? CurrentMaterial { get; private set; }

        /// <summary>当前配色是否深色。BuildGlossBand 要靠它分深浅色定高光峰值——
        /// 做成字段而不是逐层传参，是为了不让 8 个 Surface 调用点都被第三个布尔淹没。</summary>
        private static bool _isDark;

        // tile 取 256：平铺重复周期越大，大面积上越不容易看出规则图案
        //（128px 时纸纹会出现明显的等距横纹，像屏幕脏了）
        private const int Tile = 256;

        // 颗粒只生成一次；Freeze 后可跨主题共享且省内存。
        // 三种形态是材质差异的主要载体：细（纸）/ 粗块（绒）/ 细尘（玻璃）
        private static BitmapSource? _grainBitmap;
        private static BitmapSource? _velvetBitmap;
        private static BitmapSource? _frostBitmap;

        /// <summary>细颗粒（纸纹用），alpha 上限 38%。</summary>
        private static BitmapSource GrainBitmap => _grainBitmap ??= BuildGrainBitmap(0x61, 1);

        /// <summary>
        /// 粗块颗粒（绒面用）：2×2 成块。
        /// 绒布的质感来自"纤维团"而不是"细砂"，成块才有布的手感。
        /// 块大到 3px 就变成电视雪花——第一版就是这么翻车的，块和强度都要克制。
        /// </summary>
        private static BitmapSource VelvetBitmap => _velvetBitmap ??= BuildGrainBitmap(0x54, 2);

        /// <summary>细尘（玻璃类用），alpha 更低——磨砂面的微尘，不该抢镜。</summary>
        private static BitmapSource FrostBitmap => _frostBitmap ??= BuildGrainBitmap(0x40, 1);

        /// <summary>按材质挑颗粒形态。绒面的纤维团是它最强的识别特征，不能和纸纹共用一张。</summary>
        private static BitmapSource GrainFor(Material m) => m.Kind switch
        {
            MaterialKind.Velvet => VelvetBitmap,
            MaterialKind.Frosted or MaterialKind.Glass or MaterialKind.Liquid => FrostBitmap,
            _ => GrainBitmap,
        };

        public static void Apply(ColorScheme? scheme, Material? material, double intensity = 1.0)
        {
            if (scheme == null) return;
            material ??= AppearanceManager.ResolveMaterial(null);

            Current = scheme;
            CurrentMaterial = material;

            var app = Application.Current;
            if (app == null) return;   // 设计时/单元测试环境

            bool dark = scheme.IsDark;
            _isDark = dark;
            double k = Math.Clamp(intensity, 0.0, 2.0);

            // ---- 背景层 ----
            // role 系数是量出来的观感比例：正文区略重（纸面感），
            // 顶部菜单 / 状态栏更轻（避免噪点干扰文字）。
            WriteBrush(app, "Brush.Surface",   Surface(scheme.WindowBg,    material, k, 1.00));
            WriteBrush(app, "Brush.Panel",     Surface(scheme.PanelBg,     material, k, 0.95));
            WriteBrush(app, "Brush.Editor",    Surface(scheme.EditorBg,    material, k, 1.08, 0.3));
            WriteBrush(app, "Brush.Menu",      Surface(scheme.MenuBg,      material, k, 0.85));
            WriteBrush(app, "Brush.StatusBar", Surface(scheme.StatusBarBg, material, k, 0.80));
            WriteBrush(app, "Brush.HeaderBg",  Surface(scheme.MenuBg,      material, k, 0.78));   // 分区标题条
            WriteBrush(app, "Brush.SubtleBg",  Surface(scheme.MenuBg,      material, k, 0.70));   // chip / 只读框
            WriteBrush(app, "Brush.InputBg",   Surface(scheme.PanelBg,     material, k, 0.60));

            // ---- 柔光层：左上→右下 的渐变，模拟纸张受光不均 / 玻璃表面反光 ----
            WriteBrush(app, "Brush.Sheen", BuildSheen(material, k));

            // ---- 线 ----
            Write(app, "Brush.Border",   scheme.BorderColor, GlassLineAlpha(material, k));
            Write(app, "Brush.Divider",  scheme.BorderColor, GlassLineAlpha(material, k));
            Write(app, "Brush.Splitter", scheme.SplitterBg);

            // ---- 文字 ----
            Write(app, "Brush.Text",         scheme.TextColor);
            Write(app, "Brush.TextMuted",    scheme.TextMuted);
            Write(app, "Brush.TextOnAccent", dark ? "#F2EFE9" : "#FBFAF6");

            // ---- 强调 ----
            // hover / pressed 只压暗 4% / 9%：原先是 10% / 22%，按下去像"闪一下"，太跳。
            Write(app, "Brush.Accent",        scheme.Accent);
            Write(app, "Brush.AccentHover",   Darken(scheme.Accent, 0.94));
            Write(app, "Brush.AccentPressed", Darken(scheme.Accent, 0.87));

            // ---- 状态 ----
            // 悬浮/选中态一律用"极低 alpha"叠加，而不是灰块或高饱和色块。
            Write(app, "Brush.Hover",    dark ? Alpha("#FFFFFF", 0x12) : Alpha("#6B5539", 0x12));
            Write(app, "Brush.Selected", Alpha(scheme.Accent!, (byte)(dark ? 0x2A : 0x20)));
            Write(app, "Brush.Danger",        scheme.Danger);
            Write(app, "Brush.DangerHover",   Darken(scheme.Danger, 0.93));
            Write(app, "Brush.DangerPressed", Darken(scheme.Danger, 0.86));
            Write(app, "Brush.Success",       dark ? "#5EA97E" : "#5F8F62");

            // ---- 覆写 HandyControl 皮肤键 ----
            // HandyControl 的控件样式（TabControl 内容区 / Expander / ComboBox / ScrollViewer…）
            // 引用的是它自己那套键，不覆写的话这些控件会一直停在浅色皮肤，
            // 夜间模式下就会出现"面板变深、内容区仍惨白"的割裂。
            WriteBrush(app, "RegionBrush",          Surface(scheme.EditorBg, material, k, 1.08, 0.3));
            WriteBrush(app, "SecondaryRegionBrush", Surface(scheme.PanelBg,  material, k, 0.95));
            WriteBrush(app, "ThirdlyRegionBrush",   Surface(scheme.MenuBg,   material, k, 0.78));
            WriteBrush(app, "BackgroundBrush",      Surface(scheme.WindowBg, material, k, 1.00));
            WriteBrush(app, "DefaultBrush",         Surface(scheme.MenuBg,   material, k, 0.78));
            Write(app, "BorderBrush",          scheme.BorderColor, GlassLineAlpha(material, k));
            Write(app, "PrimaryBorderBrush",   scheme.BorderColor, GlassLineAlpha(material, k));
            Write(app, "SecondaryBorderBrush", scheme.BorderColor, GlassLineAlpha(material, k));
            Write(app, "PrimaryTextBrush",     scheme.TextColor);
            Write(app, "SecondaryTextBrush",   scheme.TextMuted);
            Write(app, "ThirdlyTextBrush",     scheme.TextMuted);
            Write(app, "TitleBrush",           scheme.TextColor);
            Write(app, "ReverseTextBrush",     dark ? "#22252A" : "#FBFAF6");
            Write(app, "PrimaryBrush",         scheme.Accent);
            Write(app, "AccentBrush",          scheme.Accent);
            Write(app, "DarkPrimaryBrush",     Darken(scheme.Accent, 0.92));
            Write(app, "DarkAccentBrush",      Darken(scheme.Accent, 0.92));
        }

        // ==================== 质感层构造 ====================

        /// <summary>
        /// 给外观设置的材质预览用：拿某套配色渲染出这一小块材质的样子。
        /// 走的是完整的 Surface 逻辑（含深浅色分档的高光强度），所以预览所见即所得。
        /// </summary>
        public static Brush PreviewSurface(Material m, ColorScheme scheme)
        {
            var prev = _isDark;
            _isDark = scheme.IsDark;
            try { return Surface(scheme.PanelBg, m, 1.0, 1.0); }
            finally { _isDark = prev; }
        }

        /// <summary>
        /// 合成一个表面画刷：底色 + 颗粒 + 柔光 + （液态玻璃的）高光与边缘光。
        /// </summary>
        /// <param name="hex">该区域底色。</param>
        /// <param name="m">材质。</param>
        /// <param name="k">用户调的强度倍率。</param>
        /// <param name="role">区域系数——正文区重一点、菜单轻一点。</param>
        private static Brush Surface(string hex, Material m, double k, double role,
                                    double throughFactor = 1.0)
        {
            var texture = Clamp(m.Texture * k * role, 0, 1);
            var sheen = Clamp(m.Sheen * k, 0, 1);
            // 编辑区传 0.3：正文字号小、长时间盯着，透光会明显影响可读性；
            // 其它区域可以放心透（背景图模式下透出来的就是真实画面）。
            var through = Clamp(m.Translucency * k * throughFactor, 0, 1);

            var baseColor = Parse(hex);

            // 玻璃的"冷"调。
            // 浅色主题下白色柔光打在浅底上几乎看不见——玻璃和纸纹会糊成一片。
            // 真实玻璃确实偏冷，所以这里把玻璃类材质的底色往冷灰拉一点：
            // 哪怕柔光看不出来，颜色本身也在说"这是玻璃"。
            if (m.Stretched && !_isDark)
                baseColor = Mix(baseColor, Color.FromRgb(0xC4, 0xD0, 0xDC), 0.14);

            if (through > 0)
                baseColor = Color.FromArgb((byte)(255 * (1 - through)), baseColor.R, baseColor.G, baseColor.B);

            // 完全没质感就直接给纯色画刷——最快，也让"纯色"材质名副其实。
            // 注意柔光那条判断带了 !m.Stretched：平铺材质的柔光不在画布里（见下），
            // 纯色材质正是平铺 + 无颗粒，不该为它白白套一层 DrawingBrush。
            if (texture <= 0 && (sheen <= 0 || !m.Stretched))
            {
                var plain = new SolidColorBrush(baseColor);
                plain.Freeze();
                return plain;
            }

            var baseBrush = new SolidColorBrush(baseColor);
            baseBrush.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                baseBrush, null, new RectangleGeometry(new Rect(0, 0, Tile, Tile))));

            // ① 颗粒：按材质选形态——细砂（纸）/ 纤维团（绒）/ 微尘（玻璃）
            if (texture > 0)
            {
                var grain = new DrawingGroup { Opacity = texture };
                grain.Children.Add(new ImageDrawing(GrainFor(m), new Rect(0, 0, Tile, Tile)));
                group.Children.Add(grain);
            }

            // ② 柔光：只对「拉伸」材质放进表面画刷。
            //    ⚠ 平铺材质绝对不能放——DrawingBrush 每个 256px tile 会各画一次
            //    完整的斜向渐变，铺开就是满屏割裂的斜向色块（这个 bug 踩过一次）。
            //    平铺材质的柔光由全窗那一道 Brush.Sheen 提供，语义上也本该如此：
            //    柔光是"整块纸面受光不均"，不是"每 256 平方厘米受光不均"。
            if (sheen > 0 && m.Stretched)
                group.Children.Add(new GeometryDrawing(
                    BuildSheenBrush(sheen), null,
                    new RectangleGeometry(new Rect(0, 0, Tile, Tile))));

            // ②' 液态玻璃独有的斜向流动高光带（清玻璃没有——它是均匀反光，没有"流动"）
            if (m.HighGloss && sheen > 0 && m.Stretched)
                group.Children.Add(new GeometryDrawing(
                    BuildGlossBand(sheen), null,
                    new RectangleGeometry(new Rect(0, 0, Tile, Tile))));

            // ③ 边缘光：玻璃边缘把光折射出的亮线。这是"玻璃感"最强的识别特征——
            //    柔光是整体氛围（谁都像），边缘光是"这是玻璃"（只有玻璃才有）。
            //    毛玻璃刻意不给：磨砂面的边缘是漫射的，不该有亮线。
            if (m.EdgeLight > 0 && m.Stretched)
            {
                var edgeAlpha = m.EdgeLight * k;
                var edge = new Pen(new SolidColorBrush(
                        Color.FromArgb((byte)(edgeAlpha * 255), 255, 255, 255)),
                    m.Kind == MaterialKind.Liquid ? 4 : 2.5);
                edge.Freeze();
                group.Children.Add(new GeometryDrawing(
                    null, edge, new RectangleGeometry(new Rect(1.5, 1.5, Tile - 3, Tile - 3))));
            }

            group.Freeze();

            var brush = new DrawingBrush(group)
            {
                Stretch = Stretch.Fill,
                TileMode = m.Stretched ? TileMode.None : TileMode.Tile,
                ViewportUnits = m.Stretched
                    ? BrushMappingMode.RelativeToBoundingBox
                    : BrushMappingMode.Absolute,
                Viewport = m.Stretched
                    ? new Rect(0, 0, 1, 1)
                    : new Rect(0, 0, Tile, Tile),
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 生成颗粒 tile：极淡的黑白双色噪点
        /// （浅色主题下显暗点、深色主题下显亮点，天然自适应）。
        /// 刻意不画"纸纤维"横线：长线条平铺后会形成规则条纹，
        /// 在编辑区那种大面积上极其显眼，观感是"屏幕脏了"而不是"纸"。
        /// </summary>
        /// <param name="maxAlpha">单颗粒的 alpha 上限。颗粒最终观感 ≈ 本值 × Texture ÷ 2，
        /// 所以要真正"看得出差别"，上限得给到 0x40 以上——早先给 0x2E 时
        /// 六种材质肉眼几乎分不出。</param>
        /// <param name="block">成块边长。1 = 逐像素细砂；3 = 3×3 纤维团（绒面用）。</param>
        private static BitmapSource BuildGrainBitmap(byte maxAlpha, int block)
        {
            const int S = Tile;
            var rnd = new Random(20260925 + block * 7919);   // 固定种子，且不同形态互不相关
            var px = new byte[S * S * 4];                    // BGRA

            for (int y = 0; y < S; y += block)
            {
                for (int x = 0; x < S; x += block)
                {
                    byte v = (byte)(rnd.Next(2) == 0 ? 255 : 0);
                    byte a = (byte)rnd.Next(0, maxAlpha);
                    for (int dy = 0; dy < block && y + dy < S; dy++)
                    {
                        for (int dx = 0; dx < block && x + dx < S; dx++)
                        {
                            int o = ((y + dy) * S + (x + dx)) * 4;
                            px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = a;
                        }
                    }
                }
            }

            var bmp = BitmapSource.Create(S, S, 96, 96, PixelFormats.Bgra32, null, px, S * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>区域内的柔光渐变强度（材质自己的 Sheen × 用户倍率）。</summary>
        private static LinearGradientBrush BuildSheenBrush(double sheen)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(sheen * 255), 255, 255, 255), 0.0));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x00, 255, 255, 255), 0.62));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 整窗的柔光层（画布层最上面那道）。
        /// 比面板内部的柔光更淡——它覆盖的是整个窗口，重了会发灰。
        /// 拉伸材质（玻璃类）已经在自己的表面画布里有一道柔光了，这里再给一道
        /// 就成了双层光，所以系数压到 0.22；平铺材质没有层内柔光，用 0.55。
        /// </summary>
        private static LinearGradientBrush BuildSheen(Material m, double k)
        {
            double factor = m.Stretched ? 0.22 : 0.55;
            double sheen = Clamp(m.Sheen * factor * k, 0, 1);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(sheen * 255), 255, 255, 255), 0.0));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x00, 255, 255, 255), 0.62));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 液态玻璃的斜向高光带。
        ///
        /// ⚠ 只画**一条**带。曾经画了两条（0.16 与 0.58 各一个亮峰），结果在浅色主题上
        /// 读起来像玻璃上的两道折痕，而不是流动的高光。
        /// 峰值也必须分深浅色：浅底上白色高光本来就显眼，同样的 alpha 观感强得多，
        /// 不压下去就是"屏幕反光坏了"。
        /// </summary>
        private static LinearGradientBrush BuildGlossBand(double sheen)
        {
            double peak = sheen * (_isDark ? 0.62 : 0.40);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.00));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(peak * 255), 255, 255, 255), 0.20));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.40));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 玻璃类材质的线要更淡——实色边框会立刻把"玻璃"说破，
        /// 玻璃的边缘应该是若隐若现的。
        /// </summary>
        private static byte GlassLineAlpha(Material m, double k)
        {
            if (m.Kind is MaterialKind.Glass or MaterialKind.Liquid) return 0xB0;
            if (m.Kind is MaterialKind.Frosted) return 0xD0;
            return 0xFF;
        }

        private static double Clamp(double v, double lo, double hi) =>
            v < lo ? lo : v > hi ? hi : v;

        // ==================== 写入工具 ====================

        private static void Write(Application app, string key, string hex, byte alpha = 0xFF)
        {
            var c = Parse(hex);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();                       // 冻结后可跨线程共享，且省内存
            app.Resources[key] = brush;
        }

        private static void WriteBrush(Application app, string key, Brush brush)
        {
            app.Resources[key] = brush;
        }

        private static Color Parse(string hex) =>
            (Color)ColorConverter.ConvertFromString(hex)!;

        /// <summary>两色线性插值，t=0 取 a，t=1 取 b。</summary>
        private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

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
