using System;
using System.IO;
using System.Linq;
using System.Text;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 作者风格档案：多作者库 + 按书绑定（用户明确：模仿对象不止一个，
/// 不同书用不同作者的文风——所以档案一人一份、书通过 AuthorStyleId 绑定）。
/// </summary>
public class AuthorStyleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astyle-" + Guid.NewGuid().ToString("N")[..8]);

    public AuthorStyleTests()
    {
        AuthorStyleStore.SetDirForTests(_dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AuthorStyleStore.ClearDirForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>造一段合成语料：对话段/战斗段/环境段/日常段各若干，段落边界清晰。</summary>
    private static string SceneParagraph(string who, string scene) => scene switch
    {
        "对话" => $"{who}轻声道：“这里不该来，上面的意思还没有传下来，谁也不知道会有什么等着我们。”\n" +
                  $"另一个人淡淡地道：“我知道。可我等不起了，家里的药钱已经欠了三个月。”\n" +
                  $"{who}低声道：“那你还来？你就不怕把命也搭进去？”\n" +
                  $"“怕。”他说，“但必须来。”",
        "战斗" => $"{who}骤然出手，一拳轰出，劲风炸响，碎石崩飞。对方旋即侧身，躲过这一击，随即反手一掌拍来，掌风凌厉，激起漫天尘土。{who}冷哼一声，脚下连退三步，猛地咬牙再度冲上。",
        "环境" => $"山道蜿蜒，两侧古木参天。雾气缓缓弥漫开来，笼罩着整片谷地，远处的灯火在雾中若隐若现。风声呜咽，像是某种古老的呜鸣，久久不散。",
        _ => $"{who}洗了把脸，把桌上的书一页页翻过去。窗外天色渐渐暗了下来，他起身点灯，又坐了回去。日子就这么一天天过去。",
    };

    private static string Corpus(int chapters = 12)
    {
        var sb = new StringBuilder();
        var scenes = new[] { "对话", "战斗", "环境", "日常" };
        for (var i = 1; i <= chapters; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"第{i}章 试炼" : $"    第{i:000}章 试炼");   // 两种格式都要认
            // 每章两块，每块同场景连续 4 段（≈250 字）——真实语料里场景也是成片出现的，
            // 且同场景连续段落才能聚出足额窗口（跨场景聚窗判不准）
            var s1 = scenes[i % 4];
            var s2 = scenes[(i + 1) % 4];
            for (var k = 0; k < 8; k++)
            {
                sb.AppendLine(SceneParagraph("萧炎", k < 4 ? s1 : s2));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void 分章要认两种格式_并剔除广告行()
    {
        var text = "===分节阅读 1===\n斗破苍穹全集\n    作者：天蚕土豆\n    申明:本书由自网络收集整理\n\n" + Corpus();
        var chapters = AuthorStyleBuilder.SplitChapters(text);

        Assert.Equal(12, chapters.Count);
        Assert.DoesNotContain(chapters, c => c.Contains("申明"));
        Assert.DoesNotContain(chapters, c => c.Contains("分节阅读"));
        Assert.DoesNotContain(chapters, c => c.Contains("作者："));
        // 章节标题行本身不进正文
        Assert.DoesNotContain(chapters, c => c.Contains("第1章 试炼"));
    }

    [Fact]
    public void 指纹统计与签名词要落在合理区间()
    {
        var p = AuthorStyleBuilder.Build("测试作者", Corpus(), "测试语料");

        Assert.Equal("测试作者", p.Id);   // 中文名直接当 Id
        Assert.True(p.SentenceMedian is > 2 and < 60, $"中位句长 {p.SentenceMedian}");
        Assert.True(p.DialogueRatio is > 0.05 and < 0.8, $"对话占比 {p.DialogueRatio}");
        // 语料里刻意塞了 旋即（战斗段）/淡淡（对话段）→ 必须进签名词表
        Assert.Contains(p.SignatureWords, w => w.Word == "旋即");
        Assert.Contains(p.SignatureWords, w => w.Word == "淡淡");
        // 引导语：轻声道/淡淡地道/低声道 是语料里显式写的
        Assert.Contains(p.DialogueTags, t => t == "轻声道" || t == "淡淡地道");
    }

    [Fact]
    public void 样本要按场景分桶_且剔除病态段()
    {
        var p = AuthorStyleBuilder.Build("测试作者", Corpus(), "测试语料");

        Assert.NotEmpty(p.Samples);
        Assert.All(p.Samples, s =>
        {
            var chars = AuthorStyleBuilder.VisibleChars(s.Text);
            Assert.InRange(chars, 100, 400);
        });
        // 有对话语料就该挑出对话场景
        Assert.Contains(p.Samples, s => s.Scene == "对话交锋");
        // 病态段（升华句）不该被选进来
        var pathological = "他知道，这一切才刚刚开始。命运的齿轮悄然转动。\n" + SceneParagraph("萧炎", "环境");
        var corpus = "第1章 试炼\n" + string.Join("\n\n", Enumerable.Repeat(pathological, 6)) + "\n" + Corpus(2);
        var p2 = AuthorStyleBuilder.Build("测试作者2", corpus, "测试");
        Assert.DoesNotContain(p2.Samples, s => s.Text.Contains("命运的齿轮"));
    }

    [Fact]
    public void 档案库按Id存取_多档案共存_非法Id拒收()
    {
        var p1 = AuthorStyleBuilder.Build("天蚕土豆", Corpus(), "语料A");
        var p2 = AuthorStyleBuilder.Build("烽火戏诸侯", Corpus(), "语料B");
        AuthorStyleStore.Save(p1);
        AuthorStyleStore.Save(p2);

        Assert.Equal(2, AuthorStyleStore.List().Count);
        Assert.Equal("天蚕土豆", AuthorStyleStore.Get("天蚕土豆")!.Name);
        Assert.Equal("烽火戏诸侯", AuthorStyleStore.Get("烽火戏诸侯")!.Name);

        // 路径穿越拒绝
        Assert.Null(AuthorStyleStore.Get("../evil"));
        Assert.False(AuthorStyleStore.Delete("../evil"));

        Assert.True(AuthorStyleStore.Delete("天蚕土豆"));
        Assert.Null(AuthorStyleStore.Get("天蚕土豆"));
        Assert.NotNull(AuthorStyleStore.Get("烽火戏诸侯"));
    }

    [Fact]
    public void 锚定块要含指纹_签名词_引导语与样本()
    {
        var p = AuthorStyleBuilder.Build("测试作者", Corpus(), "测试语料");
        p = p with { Rules = new[] { "章末用悬念句收束，不做总结升华。" } };

        var block = AuthorStyleBuilder.BuildAnchorBlock(p);

        Assert.Contains("作者文风锚定", block);
        Assert.Contains("测试作者", block);
        Assert.Contains("旋即", block);
        Assert.Contains("轻声道", block);
        Assert.Contains("作者守则", block);
        Assert.Contains("风格示例", block);
        Assert.Contains("对话交锋", block);
    }
}
