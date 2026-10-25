using System.Linq;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 风格审计（AI 味评分）。
///
/// 这层是"去 AI 味"的地基：没有可复现的分数，任何提示词/采样调整都只能靠感觉，
/// 也无法回答"改完到底好没好"。所以这里不测具体数字，只测**方向**：
/// 长短交错的文字必须明显高于句长均匀的文字；套话必须被抓到。
/// </summary>
public class StyleAuditTests
{
    // 句长长短不一、有对话、没有套话
    private const string Humanish = """
        刀锋贴着锁骨滑过去，凉的。
        他往后仰，鞋跟在青石上蹭出一声尖响，人已经退到半步之外。那人没追，只把手里的刀转了个方向，刀背上有一道旧豁口，像被谁咬过一口。
        「你姓什么？」
        「姓什么重要吗。」
        对方笑了一声，收刀入鞘，转身走了。巷子尽头有盏灯，灯下站着个人,看不清脸。
        他站在原地，等到那盏灯灭了，才慢慢把后背贴到墙上，吐出一口气。
        """;

    // 句长几乎一样，全是套话
    private const string AiIsh = """
        他走进了那间屋子。他看见了那张桌子。他想起了那些往事。他慢慢地坐了下来。
        他眼中闪过一丝慌乱。他嘴角勾起一抹苦笑。他深吸一口气。他缓缓开口说话。
        他心中一凛。他神色复杂。他沉默了片刻。他若有所思地点头。
        """;

    [Fact]
    public void 长短交错的文字明显高于句长均匀的()
    {
        var human = StyleAudit.Analyze(Humanish);
        var ai = StyleAudit.Analyze(AiIsh);

        Assert.True(human.Score > ai.Score + 20,
            $"人写样本 {human.Score} 分，AI 样本 {ai.Score} 分，差距不够");
        Assert.True(human.Score >= 70, $"人写样本只有 {human.Score} 分");
        Assert.True(ai.Score <= 40, $"AI 样本居然有 {ai.Score} 分");
    }

    [Fact]
    public void 套话要被抓出来并且进禁用建议()
    {
        var r = StyleAudit.Analyze(AiIsh);

        Assert.True(r.ClichePerK > 0);
        Assert.Contains(r.Hits, h => h.Kind == "套话");
        Assert.Contains(r.Clichés, c => c.Key.Contains("眼中闪过一丝"));
        Assert.Contains("建议加入项目禁用清单", r.ToText());
    }

    [Fact]
    public void 句长均匀会被判为节奏单一()
    {
        var r = StyleAudit.Analyze(AiIsh);

        Assert.True(r.SentenceCv < 0.35, $"变异系数 {r.SentenceCv} 不该这么高");
        Assert.True(r.SubSentence < 5, $"句长分项 {r.SubSentence} 不该这么高");
    }

    [Fact]
    public void 对话占比能算出来()
    {
        var r = StyleAudit.Analyze(Humanish);
        Assert.True(r.DialogueRatio > 0.03, $"对话占比 {r.DialogueRatio} 偏低");
        Assert.True(r.SubDialogue > 0, "对话得分不该是 0");
    }

    [Fact]
    public void 连续同头的句子会被点名()
    {
        var text = "他没有说话。他往前走了一步。他停下来看了看。他终于开口了。";
        var r = StyleAudit.Analyze(text);
        Assert.Contains(r.Hits, h => h.Kind == "同头");
    }

    [Fact]
    public void 空文本给零分而不是抛异常()
    {
        var r = StyleAudit.Analyze("");
        Assert.Equal(0, r.Score);
        Assert.Equal(0, r.SentenceCount);
        Assert.Empty(r.Hits);
    }

    [Fact]
    public void 短样本会提示分数不可信()
    {
        var r = StyleAudit.Analyze("他慢慢地来了。他又慢慢地走了。");
        Assert.True(r.TooShort);
        Assert.Contains("仅供参考", r.ToText());
    }
}
