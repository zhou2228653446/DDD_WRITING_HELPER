using System.IO;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 正文排版（网页版「外观设置 → 正文排版」）的纯逻辑测试。
///
/// 重点在**清洗**：字体栈最终会被写进 CSS 变量，一个含分号/花括号的字符串
/// 就能把样式表撕开。以及**默认值**——它必须精确等于样式表里原来硬编码的
/// 那组值，否则老用户第一次升级后会看到字体变了（用户熟悉的样子不该由程序
/// 替他决定）。
/// </summary>
public class EditorTypographyTests
{
    // ---------------- 默认值 = 改动前的硬编码值 ----------------

    [Fact]
    public void 默认值必须等于样式表原来的硬编码值()
    {
        var d = new EditorTypographyConfig();
        Assert.Equal("\"Songti SC\",\"SimSun\",serif", d.FontFamily);
        Assert.Equal(16, d.FontSize);
        Assert.Equal(1.9, d.LineHeight);
        Assert.Equal(0.02, d.LetterSpacing);
    }

    [Fact]
    public void 空配置归一化后就是默认值()
    {
        var n = new EditorTypographyConfig().Normalized();
        Assert.Equal(EditorTypographyConfig.DefaultFontFamily, n.FontFamily);
        Assert.Equal(EditorTypographyConfig.DefaultFontSize, n.FontSize);
        Assert.Equal(EditorTypographyConfig.DefaultLineHeight, n.LineHeight);
        Assert.Equal(EditorTypographyConfig.DefaultLetterSpacing, n.LetterSpacing);
    }

    // ---------------- 范围夹取 ----------------

    [Theory]
    [InlineData(8, 12)]      // 太小 → 下限
    [InlineData(60, 30)]     // 太大 → 上限
    [InlineData(18, 18)]     // 正常值原样
    public void 字号越界被夹到范围内(double input, double expected)
    {
        var n = new EditorTypographyConfig { FontSize = input }.Normalized();
        Assert.Equal(expected, n.FontSize);
    }

    [Theory]
    [InlineData(0.5, 1.2)]
    [InlineData(9.0, 3.0)]
    [InlineData(2.2, 2.2)]
    public void 行距越界被夹到范围内(double input, double expected)
    {
        var n = new EditorTypographyConfig { LineHeight = input }.Normalized();
        Assert.Equal(expected, n.LineHeight);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0.2)]
    [InlineData(0.05, 0.05)]
    public void 字距越界被夹到范围内(double input, double expected)
    {
        var n = new EditorTypographyConfig { LetterSpacing = input }.Normalized();
        Assert.Equal(expected, n.LetterSpacing);
    }

    [Fact]
    public void 非数值回落默认而不是变成NaN()
    {
        var n = new EditorTypographyConfig
        {
            FontSize = double.NaN,
            LineHeight = double.PositiveInfinity,
            LetterSpacing = double.NegativeInfinity,
        }.Normalized();
        Assert.Equal(EditorTypographyConfig.DefaultFontSize, n.FontSize);
        Assert.Equal(EditorTypographyConfig.DefaultLineHeight, n.LineHeight);
        Assert.Equal(EditorTypographyConfig.DefaultLetterSpacing, n.LetterSpacing);
    }

    // ---------------- 字体栈清洗（这条最关键：防 CSS 注入）----------------

    [Theory]
    [InlineData("songti; } body { display:none } .x{")]   // 分号+花括号：撕开样式表
    [InlineData("serif}")]
    [InlineData("font\\7d")]                                // CSS 转义
    [InlineData("a:b")]
    [InlineData("a@b")]
    [InlineData("a(b)")]
    [InlineData("a<b>")]
    [InlineData("a\nb")]                                    // 换行
    [InlineData("a/b")]
    [InlineData("url(x)")]
    [InlineData("expression(alert(1))")]
    public void 非法字体栈一律拒绝(string bad)
    {
        Assert.Null(EditorTypographyConfig.SanitizeFontStack(bad));
    }

    [Theory]
    [InlineData("\"Songti SC\",\"SimSun\",serif")]
    [InlineData("-apple-system,\"PingFang SC\",\"Microsoft YaHei\",sans-serif")]
    [InlineData("Georgia, \"Times New Roman\", serif")]
    [InlineData("ui-monospace,Consolas,\"Courier New\",monospace")]
    [InlineData("宋体,黑体,serif")]                          // 中文字体名
    [InlineData("system-ui,sans-serif")]
    [InlineData("Font_One-2.0,serif")]                       // 下划线/连字符/点
    public void 合法字体栈原样通过(string good)
    {
        Assert.Equal(good, EditorTypographyConfig.SanitizeFontStack(good));
    }

    [Fact]
    public void 清洗会去掉首尾空白()
    {
        Assert.Equal("serif", EditorTypographyConfig.SanitizeFontStack("  serif  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(",,,")]        // 空壳：没有任何一个像字体名的片段
    [InlineData("\"\"")]
    public void 空壳字体栈拒绝(string? bad)
    {
        Assert.Null(EditorTypographyConfig.SanitizeFontStack(bad));
    }

    [Fact]
    public void 超长字体栈拒绝()
    {
        Assert.Null(EditorTypographyConfig.SanitizeFontStack(new string('a', 201)));
        Assert.NotNull(EditorTypographyConfig.SanitizeFontStack(new string('a', 200)));
    }

    [Fact]
    public void 非法字体栈归一化后回退默认()
    {
        var n = new EditorTypographyConfig { FontFamily = "evil;}html{" }.Normalized();
        Assert.Equal(EditorTypographyConfig.DefaultFontFamily, n.FontFamily);
    }

    // ---------------- CSS 输出 ----------------

    [Fact]
    public void 输出CSS用不变文化的小数点()
    {
        // 某些区域设置下 double.ToString() 会输出 "1,9"——那会把整条 CSS 规则废掉
        var css = new EditorTypographyConfig().ToCssDeclarations();
        Assert.Contains("font-size:16px;", css);
        Assert.Contains("line-height:1.9;", css);
        Assert.Contains("letter-spacing:0.02em;", css);
        Assert.DoesNotContain(",9", css);
        Assert.Contains("font-family:\"Songti SC\",\"SimSun\",serif;", css);
    }

    [Fact]
    public void 输出CSS前会归一化()
    {
        var css = new EditorTypographyConfig { FontSize = 999, LineHeight = -5 }.ToCssDeclarations();
        Assert.Contains("font-size:30px;", css);
        Assert.Contains("line-height:1.2;", css);
    }

    // ---------------- 字体预设 ----------------

    [Fact]
    public void 每个预设的字体栈本身都是合法的()
    {
        // 预设是我们自己写的，但它们同样会进 CSS 变量——一道洗干净了才敢发出去
        foreach (var p in EditorTypographyConfig.FontPresets)
        {
            Assert.NotNull(EditorTypographyConfig.SanitizeFontStack(p.Stack));
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
        }
    }

    [Fact]
    public void 默认字体栈就是某个预设()
    {
        // 界面上要能把当前字体显示成"宋体（默认）"而不是"自定义"
        Assert.Contains(EditorTypographyConfig.FontPresets,
            p => p.Stack == EditorTypographyConfig.DefaultFontFamily);
    }

    [Theory]
    [InlineData("kaiti")]      // id
    [InlineData("KaiTi")]      // id 大小写不敏感
    [InlineData("楷体")]        // 显示名
    [InlineData("宋体")]        // 显示名是"宋体（默认）"，前缀匹配
    [InlineData("黑体")]
    public void 按id或名字都能找到预设(string key)
    {
        Assert.NotNull(EditorTypographyConfig.FindPreset(key));
    }

    [Theory]
    [InlineData("楷书")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("comic sans")]
    public void 找不到的预设返回null(string? key)
    {
        Assert.Null(EditorTypographyConfig.FindPreset(key));
    }

    [Fact]
    public void 预设id不重复()
    {
        var ids = EditorTypographyConfig.FontPresets.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---------------- 读写往返 ----------------

    [Fact]
    public void 存取往返保持值()
    {
        using var dir = new TempDir();
        EditorTypographyConfig.SaveTo(dir.Path, new EditorTypographyConfig
        {
            FontFamily = "\"Kaiti SC\",KaiTi,STKaiti,serif",
            FontSize = 21,
            LineHeight = 2.3,
            LetterSpacing = 0.08,
        });

        var back = EditorTypographyConfig.LoadFrom(dir.Path);
        Assert.Equal("\"Kaiti SC\",KaiTi,STKaiti,serif", back.FontFamily);
        Assert.Equal(21, back.FontSize);
        Assert.Equal(2.3, back.LineHeight);
        Assert.Equal(0.08, back.LetterSpacing);
    }

    [Fact]
    public void 文件不存在时返回默认值而不是抛异常()
    {
        using var dir = new TempDir();
        var cfg = EditorTypographyConfig.LoadFrom(dir.Path);
        Assert.Equal(EditorTypographyConfig.DefaultFontFamily, cfg.FontFamily);
        Assert.Equal(16, cfg.FontSize);
    }

    [Fact]
    public void 手改坏的文件被容错成默认值()
    {
        using var dir = new TempDir();
        File.WriteAllText(EditorTypographyConfig.FilePathIn(dir.Path), "{ 这不是 JSON ");
        var cfg = EditorTypographyConfig.LoadFrom(dir.Path);
        Assert.Equal(EditorTypographyConfig.DefaultFontSize, cfg.FontSize);
    }

    [Fact]
    public void 手改越界的文件读出来被夹取()
    {
        using var dir = new TempDir();
        File.WriteAllText(EditorTypographyConfig.FilePathIn(dir.Path),
            "{\"FontFamily\":\"serif\",\"FontSize\":9999,\"LineHeight\":-3,\"LetterSpacing\":9}");
        var cfg = EditorTypographyConfig.LoadFrom(dir.Path);
        Assert.Equal(EditorTypographyConfig.MaxFontSize, cfg.FontSize);
        Assert.Equal(EditorTypographyConfig.MinLineHeight, cfg.LineHeight);
        Assert.Equal(EditorTypographyConfig.MaxLetterSpacing, cfg.LetterSpacing);
    }

    [Fact]
    public void 存盘会归一化()
    {
        using var dir = new TempDir();
        EditorTypographyConfig.SaveTo(dir.Path, new EditorTypographyConfig { FontSize = 999, FontFamily = "evil;{}" });
        var back = EditorTypographyConfig.LoadFrom(dir.Path);
        Assert.Equal(EditorTypographyConfig.MaxFontSize, back.FontSize);
        Assert.Equal(EditorTypographyConfig.DefaultFontFamily, back.FontFamily);
    }

    [Fact]
    public void 范围提示用不变文化的小数点()
    {
        // 区域设置不当会把 1.2 写成 1,2——提示里出现这种数字会让人以为填错了
        Assert.DoesNotContain(",2", EditorTypographyConfig.RangeHint);
        Assert.DoesNotContain(",3", EditorTypographyConfig.RangeHint);
        Assert.Contains("12~30px", EditorTypographyConfig.RangeHint);
    }
}
