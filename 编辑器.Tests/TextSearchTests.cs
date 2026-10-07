using System;
using System.Linq;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 查找替换的核心字符串逻辑。
/// 这层以前埋在 FindReplaceWindow 里，跟 WPF 绑死、完全没法测——
/// 而"命中不重叠""索引失效复核""越界截取"恰恰是最容易在特定输入下出错的地方。
/// </summary>
public class TextSearchTests
{
    private const StringComparison C = StringComparison.Ordinal;
    private const StringComparison CI = StringComparison.OrdinalIgnoreCase;

    [Fact]
    public void FindAll_多命中_返回全部位置()
    {
        // 中文一个字一个索引：林(0)寒(1)…，(6)林(7)
        var hits = TextSearch.FindAll("林寒走过山门，林寒没有回头", "林寒", C);
        Assert.Equal(new[] { 0, 7 }, hits);
    }

    [Fact]
    public void FindAll_命中不重叠()
    {
        // "aaa" 里找 "aa"：视觉上只有一处（重叠的第二处用户根本感知不到），
        // 报两处会让"替换选中"连点两次却只改掉一半，非常困惑
        Assert.Equal(new[] { 0 }, TextSearch.FindAll("aaa", "aa", C));
    }

    [Fact]
    public void FindAll_忽略大小写()
    {
        Assert.Equal(new[] { 0, 5 }, TextSearch.FindAll("Word word", "word", CI));
    }

    [Fact]
    public void FindAll_区分大小写时_只命中完全匹配()
    {
        Assert.Equal(new[] { 5 }, TextSearch.FindAll("Word word", "word", C));
    }

    [Fact]
    public void FindAll_上限生效()
    {
        var text = string.Concat(Enumerable.Repeat("ab", 50));
        Assert.Equal(3, TextSearch.FindAll(text, "ab", C, max: 3).Count);
    }

    [Fact]
    public void FindAll_空输入_返回空()
    {
        Assert.Empty(TextSearch.FindAll("", "x", C));
        Assert.Empty(TextSearch.FindAll("abc", "", C));
    }

    [Fact]
    public void FindAll_末尾命中不越界()
    {
        Assert.Equal(new[] { 3 }, TextSearch.FindAll("abcxyz", "xyz", C));
    }

    [Fact]
    public void MatchesAt_位置对得上才返回真()
    {
        Assert.True(TextSearch.MatchesAt("林寒走过", 0, "林寒", C));
        Assert.False(TextSearch.MatchesAt("林寒走过", 1, "林寒", C));
    }

    [Fact]
    public void MatchesAt_越界返回假_不抛异常()
    {
        // 替换前必须复核索引是否还有效；越界时要安全地返回假，让调用方去重查
        Assert.False(TextSearch.MatchesAt("短", 5, "林寒", C));
        Assert.False(TextSearch.MatchesAt("短", -1, "林寒", C));
    }

    [Fact]
    public void ReplaceAt_只改指定那处()
    {
        Assert.Equal("X走过", TextSearch.ReplaceAt("林寒走过", 0, 2, "X"));
    }

    [Fact]
    public void ReplaceAt_中间替换_长度可变()
    {
        Assert.Equal("林寒慢慢走过", TextSearch.ReplaceAt("林寒走过", 2, 0, "慢慢"));
    }

    [Fact]
    public void ReplaceAt_越界安全退化()
    {
        Assert.Equal("原文", TextSearch.ReplaceAt("原文", 99, 1, "X"));
    }

    [Fact]
    public void Snippet_命中词被括出来()
    {
        var s = TextSearch.Snippet("前面一大段文字林寒出现在后面", 7, 2);
        Assert.Contains("【林寒】", s);
    }

    [Fact]
    public void Snippet_超出边界不抛异常()
    {
        // 命中在开头 / 结尾时 from、to 会被 clamp，不能越界
        var head = TextSearch.Snippet("林寒开头", 0, 2);
        var tail = TextSearch.Snippet("结尾是林寒", 3, 2);
        Assert.Contains("【林寒】", head);
        Assert.Contains("【林寒】", tail);
    }

    [Fact]
    public void Snippet_换行被压平成空格()
    {
        var s = TextSearch.Snippet("第一行\n第二行林寒\n第三行", 6, 2);
        Assert.DoesNotContain("\n", s);
    }

    [Fact]
    public void Count_与FindAll一致()
    {
        Assert.Equal(2, TextSearch.Count("aXaXa", "X", C));
    }
}
