using System;
using System.Collections.Generic;
using System.Linq;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 风格审计 v2 的行为约束。
///
/// v1 被评审打回，主要四条：① 只用 CV 判节奏 →"前半章长句、后半章短句"能刷满分；
/// ② 段落/对话用固定黄金区间 → 误伤不同体裁的章节；③ 死词表挡不住同义改写；
/// ④ 短样本（稻草人）也能跑出漂亮分数。下面的用例逐条钉住这些修复。
/// </summary>
public class StyleAuditTests
{
    // 一段"人写"的素材：句长参差、有对话、有具体名词
    private static readonly string[] HumanLines =
    {
        "刀锋贴着锁骨滑过去，凉的。",
        "他往后仰，鞋跟在青石上蹭出一声尖响，人已经退到半步之外。",
        "「你姓什么？」",
        "「姓什么重要吗。」",
        "那人没追，只把手里的刀转了个方向，刀背上有一道旧豁口，像被谁咬过一口。",
        "巷子尽头有盏灯，灯下站着个人，看不清脸。",
        "他站着，等到那盏灯灭了，才把后背贴到墙上，吐出一口气。",
        "墙根堆着半筐碎瓦，瓦缝里长出草来，割手。",
        "「走吧。」",
        "「往哪走。」",
        "「你来的地方。」",
        "他笑了一声，很短，不像笑。",
    };

    // AI 腔：句长几乎一样，套话密集
    private static readonly string[] AiLines =
    {
        "他走进屋子。他看见桌子。他想起往事。他慢慢坐下。",
        "他眼中闪过一丝慌乱。他嘴角勾起一抹苦笑。他深吸一口气。",
        "他缓缓开口。他心中一凛。他神色复杂。他沉默了片刻。",
        "他若有所思地点头。他不动声色地看着窗外。他波澜不惊地笑了。",
    };

    private static string Build(string[] lines, int times, int rotate = 0)
    {
        var sb = new System.Text.StringBuilder();
        for (var t = 0; t < times; t++)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var idx = (i + rotate * t) % lines.Length;
                sb.AppendLine(lines[idx]);
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void 人写与AI腔要拉得开()
    {
        var human = StyleAudit.Analyze(Build(HumanLines, 4));
        var ai = StyleAudit.Analyze(Build(AiLines, 6));

        Assert.True(human.Score >= 70, $"人写样本只有 {human.Score} 分");
        Assert.True(ai.Score <= 45, $"AI 样本居然有 {ai.Score} 分");
        Assert.True(human.Score > ai.Score + 25, $"差距不够：{human.Score} vs {ai.Score}");
    }

    [Fact]
    public void 聚簇型长句不能靠方差刷分()
    {
        // 评审点名的漏洞：前 5 句超长、后 5 句超短，CV 极高但读着机械。
        var clustered =
            "夜色像一层洗不掉的釉，厚厚地罩在整条巷子上，把两侧的墙、门板、招牌和晾着的衣裳全裹进同一种昏暗里，" +
            "只有远处那盏灯还亮着，把一小块湿漉漉的石板照出一点反光，像是有人在黑里划开了一道口子，透出点别的什么来，" +
            "他站在那儿，脚下是水，鞋底和石板之间发出极轻的、几乎听不见的黏响，然后他动了，" +
            "沿着墙根往那盏灯的方向走了过去，走得不快，也不慢，像是早就知道那盏灯下面有什么在等他，" +
            "巷子尽头的风把灯笼吹得晃了晃，光在他脸上扫过去，又暗下来。";

        var r = StyleAudit.Analyze(clustered);

        // 交替率必须识破它：长度在"聚簇"里并不来回换方向
        Assert.True(r.Rhythm.FlipRate < 0.2, $"交替率 {r.Rhythm.FlipRate} 太高，聚簇型没被识破");
        Assert.True(r.SubRhythm < 12, $"句长节奏居然拿了 {r.SubRhythm} 分");
    }

    [Fact]
    public void 翻译腔与升华句要被抓出来()
    {
        var text = string.Join("\n", Enumerable.Repeat(
            "与此同时，他的眼中闪过一丝复杂的神色，他缓缓地走到了桌边，伸手拿起了那本厚重的名册，嘴角勾起一抹僵硬的弧度。" +
            "空气中弥漫着陈旧纸张的气息，他感到一种难以言喻的情绪在胸口流淌，进行了一场无声的挣扎，他的目光深邃，仿佛能看穿这深不见底的黑暗。" +
            "没有人能独善其身。他知道，这一切才刚刚开始。命运的齿轮悄然转动，等待他们的，将是更加残酷的风暴。", 4));

        var r = StyleAudit.Analyze(text);

        Assert.True(r.SubFlavor <= 14, $"模型腔只扣了 {35 - r.SubFlavor} 分，太宽松");
        Assert.Contains(r.Hits, h => h.Kind == "情绪量词" || h.Kind == "感官套话" || h.Kind == "翻译腔");
        Assert.True(r.Flavor.ElevationHits > 0, "章末升华句没被抓到");
    }

    [Fact]
    public void 流水句要单独扣分()
    {
        var text = string.Join("\n", Enumerable.Repeat(
            "他站在原地，看着眼前的一切，脑子里乱成一团，他不知道该往哪走，也没有人告诉他该往哪走，" +
            "他只能站在这里，看着那些来来往往的人，看着他们的脸，看着他们的眼睛，" +
            "没有人停下来，没有人对他说过一句话，没有人问过他从哪里来，要到哪里去。", 4));

        var r = StyleAudit.Analyze(text);

        Assert.True(r.Rhythm.FlowRatio > 0.5, $"流水句占比只有 {r.Rhythm.FlowRatio}");
        Assert.True(r.SubFlow <= 3, $"流水句只扣了 {10 - r.SubFlow} 分");
    }

    [Fact]
    public void 短样本要折减分数_不能拿满分()
    {
        var shortText = "他走进屋子。他看见桌子。他想起往事。";
        var r = StyleAudit.Analyze(shortText);

        Assert.True(r.Confidence < 0.7, $"置信度 {r.Confidence} 太高");
        Assert.True(r.Score < 60, $"短样本居然 {r.Score} 分");
        Assert.Contains("置信度", r.ToText());
    }

    [Fact]
    public void 连续同头会被点名_加状语起手后改由另一个维度接住()
    {
        var plain = StyleAudit.Analyze("他没有说话。他往前走了一步。他停下来看了看。他终于开口了。");
        Assert.Contains(plain.Hits, h => h.Kind == "同头");

        // 评审点名的规避手法：句首塞状语，同头率归零
        var dodged = StyleAudit.Analyze(
            "下一秒，他没有说话。紧接着，他往前走了一步。就在这时，他停下来看了看。与此同时，他终于开口了。");
        Assert.True(dodged.SameHeadRatio < plain.SameHeadRatio, "塞状语后同头率应该下降");
        Assert.True(dodged.Flavor.LeadInHits > 0, "句首状语没有被计入");
    }

    [Fact]
    public void 对话占比要相对全书基线_孤身潜行章不该被扣分()
    {
        // 全书基线：对话多的书
        var baseline = StyleBaseline.From(Build(HumanLines, 4));

        // 一章完全没有对话的潜行章
        var solo = string.Join("\n", Enumerable.Repeat(
            "他沿着水渠往回走，渠底的青苔滑得厉害，两只手扶着壁上的砖缝，一步一步往前挪。" +
            "远处有动静，听不出是什么，他停下，等了很久，声音又没了。" +
            "他继续走，脚底的水泡破了，又疼又痒，他没停。", 4));

        var absolute = StyleAudit.Analyze(solo);
        var relative = StyleAudit.Analyze(solo, baseline);

        // 相对判：对话占比与全书差得远，但这是体裁，不该重罚
        Assert.True(absolute.SubDialogue >= 7, $"绝对判对话分只有 {absolute.SubDialogue}");
        Assert.True(relative.SubDialogue >= 5, $"相对判对话分只有 {relative.SubDialogue}，太狠了");
    }

    [Fact]
    public void 段落节奏相对全书_移动端短段排版不该被扣光()
    {
        // 全是短段（手机端"瀑布流"排版）
        var waterfall = string.Join("\n", Enumerable.Repeat(
            "他动了。\n灯灭了。\n风停了。\n他往前走。\n水声近了。\n他没停。", 8));

        // 整本书都是短段排版 → 基线本来就是短段，这章不该被扣
        var waterfallBook = Enumerable.Range(0, 4)
            .Select(i => string.Join("\n", Enumerable.Repeat(
                "他动了。\n灯灭了。\n风停了。\n他往前走。\n水声近了。\n他没停。", 2)))
            .ToList();
        var baseline = StyleBaseline.FromChapters(waterfallBook);
        var relative = StyleAudit.Analyze(waterfall, baseline);

        Assert.True(relative.SubParagraph >= 6, $"整本短段排版的书，段落分只有 {relative.SubParagraph}");

        // 换成一本长段落的书，这章会被轻罚，但不清零
        var longBook = StyleBaseline.FromChapters(
            Enumerable.Range(0, 4).Select(i => Build(HumanLines, 1, i)).ToList());
        var mismatched = StyleAudit.Analyze(waterfall, longBook);
        Assert.True(mismatched.SubParagraph >= 2, $"段落分被清零了：{mismatched.SubParagraph}");
    }

    [Fact]
    public void 空文本给零分而不是抛异常()
    {
        var r = StyleAudit.Analyze("");
        Assert.Equal(0, r.Score);
        Assert.Equal(0, r.SentenceCount);
        Assert.Empty(r.Hits);
    }
}
