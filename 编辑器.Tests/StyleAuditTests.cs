using System;
using System.Linq;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 风格审计 v5：**"AI 味指数"已退役**（统计阈值抓不住隐形目标）。
/// style_check 只出两类可信的东西：事实统计 + 病态证据清单。
/// 打分职能在作者相似度线（见 AuthorStyleTests）。
/// </summary>
public class StyleAuditTests
{
    [Fact]
    public void 空文本给空结果而不是抛异常()
    {
        var r = StyleAudit.Analyze("");
        Assert.Equal(0, r.SentenceCount);
        Assert.Empty(r.Hits);
        Assert.DoesNotContain("AI 味指数", r.ToText());
    }

    [Fact]
    public void 翻译腔与升华句要被抓出来()
    {
        var r = StyleAudit.Analyze(
            "进行了一场无声的挣扎。他知道，这一切才刚刚开始。" +
            "他看着窗外的雨，雨下了很久，他也没有动，只是看着雨水顺着玻璃往下淌。");
        Assert.Contains(r.Hits, h => h.Kind == "翻译腔");
        Assert.Contains(r.Hits, h => h.Kind == "升华");
    }

    [Fact]
    public void 嘴角裂开弧度要被句式树抓住()
    {
        var r = StyleAudit.Analyze(
            "周主任抬起头，惨白的脸在屏幕绿光映照下一片惨淡，嘴角裂开一个生硬的弧度。" +
            "他看着对面的人，把那份文件推了过去，等对方自己看清楚里面的内容。");
        Assert.Contains(r.Hits, h => h.Kind == "僵笑弧度");
    }

    [Fact]
    public void 连续同头会作为证据列出()
    {
        var r = StyleAudit.Analyze("他没有说话。他往前走了一步。他停下来看了看。他终于开口了。");
        Assert.Contains(r.Hits, h => h.Kind == "同头");
    }

    [Fact]
    public void 单人演讲不算对白()
    {
        var speech = string.Join("\n", Enumerable.Repeat(
            "“各位，我今天不讲成绩，我讲五个人，讲一讲他们是谁，他们做过什么。”", 8));
        var r = StyleAudit.Analyze(speech);
        Assert.True(r.DialogueRatio < 0.1, $"演讲被算成了对白：{r.DialogueRatio:P0}");
    }

    [Fact]
    public void 章节标题行不进统计()
    {
        var body = string.Join("\n", Enumerable.Repeat("他沿着走廊往深处走，脚步声在墙上来回撞。", 8));
        var r = StyleAudit.Analyze("=== 第3章 常住人口摸排（1856 字）===\n" + body);

        Assert.DoesNotContain(r.Hits.Select(h => h.Excerpt), e => e.Contains("==="));
        Assert.True(!r.ToText().Contains("==="), "标题行混进了统计");
    }

    [Fact]
    public void 事实层要有逗号密度与短句占比()
    {
        var r = StyleAudit.Analyze(string.Join("", Enumerable.Repeat(
            "他缓缓地走到了桌边，伸手拿起了那本厚重的名册，嘴角勾起一抹僵硬的弧度。", 6)));
        Assert.True(r.CommaPerSentence > 1, $"每句逗号 {r.CommaPerSentence}");
        Assert.True(r.ToText().Contains("每句逗号"));
        Assert.True(r.ToText().Contains("短句占比"));
    }

    [Fact]
    public void 输出不再含打分判读()
    {
        var r = StyleAudit.Analyze(string.Join("", Enumerable.Repeat(
            "他缓缓地走到了桌边，伸手拿起了那本厚重的名册。", 8)));
        var t = r.ToText();
        Assert.DoesNotContain("AI 味指数", t);
        Assert.DoesNotContain("判读", t);
        Assert.DoesNotContain("/100", t);
    }
}
