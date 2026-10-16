using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace 编辑器.Services;

/// <summary>
/// 正文排版（网页版「外观设置 → 正文排版」）——字体栈 / 字号 / 行距 / 字距。
///
/// ★ 为什么**不**放进 AppearanceConfig（appearance.json）而要单独一个文件：
///   1. 桌面端 <c>AppearanceManager.Save</c> 是**全量序列化**：它读进来时忽略
///      未知字段，再保存时就把它们写没了。往同一个文件加字段，用户在桌面端
///      切一次配色，网页端设的排版就被抹掉。
///   2. 桌面端是 WPF，字体体系在 XAML 里（FontFamily 资源），这一组数据对它
///      没有意义，共享也无从生效。
///   所以两者刻意分开存：外观共享（配色/材质两边同源），排版网页端专有。
///
/// ⚠ 默认值必须**精确等于**样式表里原来的硬编码值（<c>#editor</c> 的
///   <c>font:16px/1.9 "Songti SC","SimSun",serif</c> 与 <c>letter-spacing:.02em</c>）。
///   用户熟悉的样子不该由程序替他决定——新样式一律做选项，不改默认观感。
/// </summary>
public class EditorTypographyConfig
{
    /// <summary>CSS font-family 字体栈。存的是**整条栈**（含引号与回退），
    /// 因为浏览器端要看它逐级回退，只存一个名字在别的设备上会失效。</summary>
    public string FontFamily { get; set; } = DefaultFontFamily;

    /// <summary>正文字号（px）。</summary>
    public double FontSize { get; set; } = DefaultFontSize;

    /// <summary>行距倍率。</summary>
    public double LineHeight { get; set; } = DefaultLineHeight;

    /// <summary>字距（em）。原样式表是 .02em，默认保持一致。</summary>
    public double LetterSpacing { get; set; } = DefaultLetterSpacing;

    // ---------------- 默认值与范围（与前端 appearance.js 的 TYPO_DEFAULTS 一致）----------------

    /// <summary>与 #editor 原硬编码的字体栈逐字符一致。</summary>
    public const string DefaultFontFamily = "\"Songti SC\",\"SimSun\",serif";
    public const double DefaultFontSize = 16;
    public const double DefaultLineHeight = 1.9;
    public const double DefaultLetterSpacing = 0.02;

    public const double MinFontSize = 12, MaxFontSize = 30;
    public const double MinLineHeight = 1.2, MaxLineHeight = 3.0;
    public const double MinLetterSpacing = 0, MaxLetterSpacing = 0.2;

    /// <summary>把任意来源的配置收敛到合法范围；字体栈不合法则回退到默认。
    /// 读盘和写盘都过这一道——手改坏的 typo.json 不该让页面变成白板。</summary>
    public EditorTypographyConfig Normalized() => new()
    {
        FontFamily = SanitizeFontStack(FontFamily) ?? DefaultFontFamily,
        FontSize = Clamp(FontSize, MinFontSize, MaxFontSize, DefaultFontSize),
        LineHeight = Clamp(LineHeight, MinLineHeight, MaxLineHeight, DefaultLineHeight),
        LetterSpacing = Clamp(LetterSpacing, MinLetterSpacing, MaxLetterSpacing, DefaultLetterSpacing),
    };

    /// <summary>字体预设（下拉里那几项）。给的是**整条字体栈**而不是单个字体名：
    /// 只写一个名字在别的设备上会失效（iOS 有 Songti SC、Android 没有），
    /// 栈里逐级回退才稳。
    ///
    /// ★ 定义放这里而不是前端——和配色/材质"直接从 AppearanceManager 取"是
    ///   同一个道理：写死两份迟早对不上，而且 MCP 的 ui_typography 也要按
    ///   名字选字体，共用同一份才不至于两边说的"楷体"不是一回事。</summary>
    public static readonly FontPreset[] FontPresets =
    {
        new("songti",   "宋体（默认）", "\"Songti SC\",\"SimSun\",serif"),
        new("heiti",    "黑体",        "-apple-system,\"PingFang SC\",\"Microsoft YaHei\",\"Segoe UI\",sans-serif"),
        new("kaiti",    "楷体",        "\"Kaiti SC\",KaiTi,STKaiti,serif"),
        new("fangsong", "仿宋",        "\"FangSong\",FangSong_GB2312,STFangsong,serif"),
        new("yuanti",   "圆体",        "\"Yuanti SC\",\"Hiragino Sans GB\",\"Microsoft YaHei\",sans-serif"),
        new("serif-en", "西文衬线",    "Georgia,\"Songti SC\",\"Times New Roman\",serif"),
        new("mono",     "等宽",        "ui-monospace,Consolas,\"Courier New\",monospace"),
        new("system",   "系统默认",    "system-ui,-apple-system,\"Segoe UI\",\"Microsoft YaHei\",sans-serif"),
    };

    /// <summary>按 id 或显示名找一个预设（MCP 那边两种都允许：agent 说
    /// "换成楷体"和"用 kaiti"都该认）。都找不到返回 null。</summary>
    public static FontPreset? FindPreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        return FontPresets.FirstOrDefault(p =>
            p.Id.Equals(k, StringComparison.OrdinalIgnoreCase) ||
            p.Name == k ||
            p.Name.StartsWith(k, StringComparison.Ordinal));   // 「黑体」能命中「黑体」（去括号后缀）
    }

    public record FontPreset(string Id, string Name, string Stack);

    // ---------------- 读写（Web 端与 MCP 共用，避免两处各写一份路径/容错）----------------

    /// <summary>配置文件位置。放在**主体配置目录**下：自用与桌面端同一个目录（但文件名
    /// 独立于 appearance.json，原因见类注释），试用者各一份。</summary>
    public static string FilePathIn(string configDir) => Path.Combine(configDir, "typo.json");

    /// <summary>读配置。文件不存在、读坏、字段越界都收敛到合法值——宁可回到默认，
    /// 也不能让一个手改坏的 JSON 把编辑区变成白板。</summary>
    public static EditorTypographyConfig LoadFrom(string configDir)
    {
        try
        {
            var path = FilePathIn(configDir);
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<EditorTypographyConfig>(File.ReadAllText(path));
                if (cfg != null) return cfg.Normalized();
            }
        }
        catch { /* 读坏就用默认 */ }
        return new EditorTypographyConfig();
    }

    /// <summary>写配置（写盘前归一化）。目录不存在会自动建。</summary>
    public static void SaveTo(string configDir, EditorTypographyConfig cfg)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(FilePathIn(configDir), JsonSerializer.Serialize(cfg.Normalized()));
    }

    private static double Clamp(double v, double lo, double hi, double fallback)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
        return v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// 字体栈清洗。它最终会被写进 CSS 变量（<c>--editor-font</c>），所以要挡住
    /// 能破坏样式表或注入规则的字符：分号、花括号、括号、反斜杠（CSS 转义）、
    /// 冒号、@ 等一律拒绝，**整条栈作废**而不是挑着删——挑着删会留下一条
    /// 语义不明的栈，不如老实回退到默认。
    ///
    /// 允许：字母（含中文，字体名里会出现「宋体」）、数字、空格、逗号、
    /// 单双引号、连字符、下划线、点。这些足够表达任意合法的字体栈。
    /// </summary>
    public static string? SanitizeFontStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return null;
        var s = stack.Trim();
        if (s.Length > 200) return null;

        foreach (var ch in s)
        {
            bool ok = char.IsLetterOrDigit(ch) || ch is ' ' or ',' or '\'' or '"' or '-' or '_' or '.';
            if (!ok) return null;
        }

        // 至少得有一个像字体名的非空白片段，否则是 ",,," 这种空壳
        var hasName = s.Split(',').Any(p => p.Trim().Trim('"', '\'').Length > 0);
        return hasName ? s : null;
    }

    /// <summary>格式化成一个可直接放进 CSS 的值（数字用不变文化，避免某些区域
    /// 设置下小数点变成逗号，把 1.9 写成 "1,9" 直接废掉整条规则）。</summary>
    public string ToCssDeclarations()
    {
        var n = Normalized();
        var sb = new StringBuilder();
        sb.Append("font-family:").Append(n.FontFamily).Append(';');
        sb.Append("font-size:").Append(Fmt(n.FontSize)).Append("px;");
        sb.Append("line-height:").Append(Fmt(n.LineHeight)).Append(';');
        sb.Append("letter-spacing:").Append(Fmt(n.LetterSpacing)).Append("em;");
        return sb.ToString();
    }

    private static string Fmt(double v) =>
        Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>给工具/界面描述用的范围提示。用具不变文化格式化——区域设置
    /// 不同时 1.2 会被写成 "1,2"，提示里出现这种数字会让人以为填错了。</summary>
    public static string RangeHint =>
        $"字号 {Fmt(MinFontSize)}~{Fmt(MaxFontSize)}px / " +
        $"行距 {Fmt(MinLineHeight)}~{Fmt(MaxLineHeight)} / " +
        $"字距 {Fmt(MinLetterSpacing)}~{Fmt(MaxLetterSpacing)}em";
}
