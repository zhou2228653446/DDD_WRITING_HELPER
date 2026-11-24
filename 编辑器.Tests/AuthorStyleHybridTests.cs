using System.Linq;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 混合档：句子/用词学 A，段落与对话节奏按 B 的版面。
///
/// 起因（2026-11-24，用户明确）：马伯庸档案是拿来写网文的。他的句子 21~23 字、
/// 1.7 逗号本来就落在移动端区间里，但段长 52~70、长段 15~24%、留白 48~72%
/// 是纸书排版。按网文发要把段落压到 25 字上下，句子不动。
/// 所以「像不像某作者」和「版面能不能发」是两件事，得能拆开绑。
/// </summary>
[Collection("author-style-store")]
public class AuthorStyleHybridTests
{
    /// <summary>手搓档案：不依赖 Build（合成语料会被分章器当广告页丢掉）。</summary>
    private static AuthorStyleProfile P(
        string name, int sentence, double comma, double para, double paraP90,
        double shortPara, double dialogue, double quiet = 0.05, double longPara = 0.01,
        int runMedian = 2) => new()
    {
        Id = name,
        Name = name,
        SentenceMedian = sentence,
        SentenceCv = 0.5,
        CommaPerSentence = comma,
        ShortSentenceRatio = 0.09,
        ParagraphMedian = para,
        ParagraphP75 = para * 1.4,
        ParagraphP90 = paraP90,
        ShortParagraphRatio = shortPara,
        LongParagraphRatio = longPara,
        QuietLongRatio = quiet,
        DialogueRatio = dialogue,
        DialogueRunMedian = runMedian,
        DialogueTagPerK = 2.5,
        DialogueTags = new[] { "说", "问" },
        SignatureWords = System.Array.Empty<AuthorStyleProfile.SignatureWord>(),
    };

    [Fact]
    public void 混合档_句子取主档案_段落与对话取节奏档案()
    {
        var syntax = P("马伯庸", sentence: 23, comma: 1.7, para: 59, paraP90: 109,
                       shortPara: 0.10, dialogue: 0.235);
        var rhythm = P("杀虫队队员", sentence: 15, comma: 1.1, para: 25, paraP90: 51,
                       shortPara: 0.30, dialogue: 0.40, runMedian: 5);

        var anchor = AuthorStyleBuilder.BuildAnchorBlock(syntax, false, rhythm);

        // 句子：主档案
        Assert.Contains("中位句长 23 字", anchor);
        // 段落：节奏档案（不是 59）
        Assert.Contains("段长以 25 字为中心", anchor);
        Assert.DoesNotContain("段长以 59 字为中心", anchor);
        // 对话占比：节奏档案（40% ≥ 25% 走「主要推进手段」分支）
        Assert.Contains("对话是主要推进手段", anchor);
        Assert.Contains("40%", anchor);
        // 引导语表：仍是主档案的
        Assert.Contains("他说/她说", anchor);
        // 声明混合，否则模型会把两组数字平均
        Assert.Contains("混合档", anchor);
        Assert.Contains("杀虫队队员", anchor);
    }

    [Fact]
    public void 混合档_留白档位按节奏档案判定()
    {
        // 主档案是余华式（长段多、几乎无对话）→ 单用时要求每章空转；
        // 节奏档案是网文（长段 1%、留白 5%）→ 混合后不该再要求空转。
        var syntax = P("余华", sentence: 27, comma: 1.8, para: 30, paraP90: 164,
                       shortPara: 0.33, dialogue: 0.136, quiet: 0.80, longPara: 0.21);
        var rhythm = P("杀虫队队员", sentence: 15, comma: 1.1, para: 25, paraP90: 51,
                       shortPara: 0.30, dialogue: 0.40, quiet: 0.03, longPara: 0.005);

        Assert.Contains("空转", AuthorStyleBuilder.BuildAnchorBlock(syntax));

        var hybrid = AuthorStyleBuilder.BuildAnchorBlock(syntax, false, rhythm);
        Assert.DoesNotContain("空转", hybrid);
        Assert.Contains("长段按需使用", hybrid);
    }

    [Fact]
    public void 不传节奏档案时与旧行为一字不差()
    {
        var syntax = P("马伯庸", 23, 1.7, 59, 109, 0.10, 0.235);

        Assert.Equal(AuthorStyleBuilder.BuildAnchorBlock(syntax),
                     AuthorStyleBuilder.BuildAnchorBlock(syntax, false, null));
        // 同一份档案当节奏 = 不混合
        Assert.Equal(AuthorStyleBuilder.BuildAnchorBlock(syntax),
                     AuthorStyleBuilder.BuildAnchorBlock(syntax, false, syntax));
        Assert.DoesNotContain("混合档", AuthorStyleBuilder.BuildAnchorBlock(syntax));
    }

    [Fact]
    public void 样本块要给两组样本分别标注用途()
    {
        var syntax = P("马伯庸", 23, 1.7, 59, 109, 0.10, 0.235);
        var rhythm = P("杀虫队队员", 15, 1.1, 25, 51, 0.30, 0.40);

        var s1 = new AuthorStyleProfile.StyleSample("对话交锋", "陈广生把橘子往前递了递。")
            { Id = "s01", Source = "第 2 章", Note = "中位句长 23 字" };
        var s2 = new AuthorStyleProfile.StyleSample("对话交锋", "“他不在家。”她说。")
            { Id = "s01", Source = "第 9 章", Note = "中位句长 15 字" };

        var block = AuthorStyleBuilder.BuildSampleBlock(syntax, new[] { s1 }, "杀虫队队员", new[] { s2 });

        Assert.Contains("分段示范", block);
        Assert.Contains("句式示范", block);
        Assert.Contains("马伯庸", block);
        Assert.Contains("杀虫队队员", block);
        Assert.Contains(s1.Text, block);
        Assert.Contains(s2.Text, block);
        // 冲突时谁让谁，必须写死，否则模型自己折中成 40 字段落
        Assert.Contains("句子让位于前者", block);
    }

    [Fact]
    public void 单档案样本块不带分段说明()
    {
        var syntax = P("马伯庸", 23, 1.7, 59, 109, 0.10, 0.235);
        var s1 = new AuthorStyleProfile.StyleSample("对话交锋", "陈广生把橘子往前递了递。")
            { Id = "s01", Source = "第 2 章" };

        var block = AuthorStyleBuilder.BuildSampleBlock(syntax, new[] { s1 });
        Assert.DoesNotContain("分段示范", block);
        Assert.DoesNotContain("句式示范", block);
        Assert.Contains("本次风格示例", block);
    }
}
