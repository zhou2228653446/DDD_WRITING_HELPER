using System;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 版本号（stamp）在前后端之间的表示。
///
/// 抓的是这个 bug：stamp 用的是 <c>LastWriteTimeUtc.Ticks</c>（量级 6.4e17），
/// 直接当 JSON 数字发给浏览器，会被 JS 的 Number（精确到 2^53-1 ≈ 9.0e15）
/// 四舍五入；传回来一比对必然不等，于是**每次从浏览器保存都被判成
/// "这本书已经被别处改过了"**——用户敲的字一个字都存不进去，
/// 界面上只有一句含糊报错，完全看不出是精度问题。
/// </summary>
public class StampTokenTests
{
    [Fact]
    public void 真实时间戳确实超出JS安全整数范围()
    {
        // 取一个"看起来就像真实文件时间"的值：整秒再加 1234 个 tick。
        // ⚠ 别用 00:00:00 那种整点值 —— 它的低 7 位恰好是 0，正好能被 double
        //   精确表示，断言会假通过（当初就是这么写错一版的）。
        var ticks = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.Zero).UtcTicks + 1234;

        // 前提成立，这个测试才有意义
        Assert.False(StampToken.IsJavaScriptSafe(ticks));
        // 而且走 double 一定丢精度 —— 这就是不能发数字的原因
        Assert.NotEqual(ticks, (long)(double)ticks);
    }

    [Fact]
    public void 字符串往返无损()
    {
        var ticks = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.Zero).UtcTicks;

        var token = StampToken.From(ticks);

        Assert.IsType<string>(token);          // 必须是字符串，不能悄悄变回数字
        Assert.Equal(ticks.ToString(), token);
        Assert.Equal(ticks, StampToken.Parse(token));
    }

    [Fact]
    public void 认不出来就当没带版本号()
    {
        Assert.Null(StampToken.Parse(null));
        Assert.Null(StampToken.Parse(""));
        Assert.Null(StampToken.Parse("   "));
        Assert.Null(StampToken.Parse("不是数字"));
        Assert.Null(StampToken.Parse("1.5"));            // 小数不是合法 ticks
        Assert.Null(StampToken.Parse("64000000000000000000")); // 溢出 long
    }

    [Fact]
    public void 小于安全上限的旧值也照常往返()
    {
        // 0 表示"项目没打开"，不能因为它是 0 就被当成"没带"
        Assert.Equal(0, StampToken.Parse(StampToken.From(0)));
        Assert.True(StampToken.IsJavaScriptSafe(0));
    }
}
