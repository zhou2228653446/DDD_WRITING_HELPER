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
        // 新指纹：标点习惯、短句占比、对话节奏
        Assert.True(p.CommaPerSentence > 0.5, $"每句逗号 {p.CommaPerSentence}");
        Assert.True(p.ShortSentenceRatio > 0, $"短句占比 {p.ShortSentenceRatio}");
        Assert.True(p.DialogueRunMedian >= 1, $"引号段连发中位 {p.DialogueRunMedian}");
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
        // 有对话语料就该挑出对话场景；多章语料还该有章首样本
        Assert.Contains(p.Samples, s => s.Scene == "对话交锋");
        Assert.Contains(p.Samples, s => s.Scene == "开篇起笔");
        Assert.Contains(p.Samples, s => s.Scene == "章末收束");
        Assert.True(p.Samples.Count <= AuthorStyleBuilder.MaxSamples);
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

    // ─────────────── 作者相似度（打分职能的新家）───────────────

    private const string LongStyleParagraph =
        "他缓缓地抬起了头，目光越过人群，落在了远处那座高塔的顶端，旋即在心底泛起了一阵难以名状的悸动。";

    private static string LongStyleCorpus(int chapters = 10)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= chapters; i++)
        {
            sb.AppendLine($"第{i}章 试炼");
            // 每章 10 段 ≈ 450 字，须超过"太短当广告"的 300 字门槛
            for (var k = 0; k < 10; k++) sb.AppendLine(LongStyleParagraph);
        }
        return sb.ToString();
    }

    [Fact]
    public void 同风格文本得分高_相反风格得分低()
    {
        var profile = AuthorStyleBuilder.Build("测试作者", LongStyleCorpus(), "语料");

        // 同风格：同样的长句连缀 + 签名词按目标密度出现（千字各 1 次左右）
        var same = string.Join("\n", Enumerable.Repeat(LongStyleParagraph, 22));

        // 相反风格：短句剁节奏、签名词绝迹
        var opposite = string.Join("", Enumerable.Repeat("他看她。她低头。屋里很静。风起了。灯灭了。", 4));

        var s1 = AuthorStyleBuilder.ScoreSimilarity(profile, same).Score;
        var s2 = AuthorStyleBuilder.ScoreSimilarity(profile, opposite).Score;

        Assert.True(s1 >= 70, $"同风格只有 {s1}");
        Assert.True(s2 <= 50, $"相反风格也有 {s2}");
        Assert.True(s1 - s2 >= 20, $"区分度只有 {s1 - s2}");
    }

    [Fact]
    public void 病态句要扣相似度分()
    {
        var profile = AuthorStyleBuilder.Build("测试作者", LongStyleCorpus(), "语料");
        var clean = string.Join("\n", Enumerable.Repeat(LongStyleParagraph, 22));
        var withDisease = clean + "\n他知道，这一切才刚刚开始。命运的齿轮悄然转动。";

        Assert.True(
            AuthorStyleBuilder.ScoreSimilarity(profile, withDisease).Score
            < AuthorStyleBuilder.ScoreSimilarity(profile, clean).Score,
            "病态句没有扣分");
    }

    [Fact]
    public void 锚定块只放指纹与守则_样本单独成块()
    {
        var p = AuthorStyleBuilder.Build("测试作者", Corpus(), "测试语料");
        p = p with { Rules = new[] { "章末用悬念句收束，不做总结升华。" } };

        var anchor = AuthorStyleBuilder.BuildAnchorBlock(p);

        Assert.Contains("作者文风锚定", anchor);
        Assert.Contains("测试作者", anchor);
        Assert.Contains("旋即", anchor);
        Assert.Contains("轻声道", anchor);
        Assert.Contains("作者守则", anchor);
        // ★ 稳定前缀里不能有原文样本：它每次请求都换，塞进缓存段会打断缓存。
        Assert.DoesNotContain("风格示例", anchor);
        Assert.DoesNotContain("对话交锋", anchor);
        Assert.Equal(anchor, AuthorStyleBuilder.BuildAnchorBlock(p));

        var samples = AuthorStyleBuilder.SelectSamples(p, "对话交锋", 7);
        var block = AuthorStyleBuilder.BuildSampleBlock(p, samples);
        Assert.NotEmpty(samples);
        Assert.Contains("本次风格示例", block);
        Assert.Contains("只学怎么写", block);
        Assert.Contains(samples[0].Text, block);
    }

    // ─────────────── 样本池 / 动态选样 / 守则独立 ───────────────

    private static AuthorStyleProfile Pool(int count = 18) => new()
    {
        Id = "pool",
        Name = "池作者",
        Samples = Enumerable.Range(0, count).Select(i =>
            new AuthorStyleProfile.StyleSample(
                i % 2 == 0 ? "对话交锋" : "战斗冲突",
                $"这是第 {i} 段示例原文，用来验证按场景挑选与章号轮换是否稳定。他看着她，没有说话。")
            {
                Id = $"s{i + 1:00}",
                Source = $"第{i + 1}章",
                Note = "示范：短句白描",
                Fit = 0.8,
                Enabled = true,
            }).ToArray(),
    };

    [Fact]
    public void 样本池要带元数据_且每类场景可不止一段()
    {
        var p = AuthorStyleBuilder.Build("测试作者", Corpus(20), "测试语料");

        Assert.NotEmpty(p.Samples);
        Assert.All(p.Samples, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Id));
            Assert.False(string.IsNullOrWhiteSpace(s.Source));
            Assert.False(string.IsNullOrWhiteSpace(s.Note));
            Assert.False(string.IsNullOrWhiteSpace(s.Text));
            Assert.InRange(s.Fit, 0, 1);
            Assert.True(s.Enabled);
        });
        Assert.Equal(p.Samples.Count, p.Samples.Select(s => s.Id).Distinct().Count());
        Assert.True(p.Samples.Count <= AuthorStyleBuilder.MaxSamples);
        // 同一场景允许多段：池不再是"每场景至多 1 段"
        Assert.True(p.Samples.GroupBy(s => s.Scene).Any(g => g.Count() > 1),
            "样本池没有扩起来，仍是每场景 1 段");
    }

    [Fact]
    public void 动态选样_同章稳定_换章轮换_场景命中()
    {
        var p = Pool();
        var a1 = AuthorStyleBuilder.SelectSamples(p, "对话交锋", 7);
        var a2 = AuthorStyleBuilder.SelectSamples(p, "对话交锋", 7);
        var b = AuthorStyleBuilder.SelectSamples(p, "对话交锋", 8);

        Assert.Equal(AuthorStyleBuilder.SamplesPerRequest, a1.Count);
        Assert.Equal(a1.Select(x => x.Id), a2.Select(x => x.Id));      // 同章反复生成完全一致
        Assert.All(a1, s => Assert.Equal("对话交锋", s.Scene));          // 只取命中场景的
        Assert.NotEqual(a1.Select(x => x.Id), b.Select(x => x.Id));      // 换章自动换组
        Assert.NotEqual(
            AuthorStyleBuilder.BuildSampleBlock(p, a1),
            AuthorStyleBuilder.BuildSampleBlock(p, b));
    }

    [Fact]
    public void 禁用样本不再入选_空池不报错()
    {
        var p = Pool(4);
        var first = p.Samples[0].Id;
        var disabled = p with
        {
            Samples = p.Samples.Select(s => s.Id == first ? s with { Enabled = false } : s).ToArray(),
        };
        Assert.DoesNotContain(AuthorStyleBuilder.SelectSamples(disabled, null, 3, 4),
            s => s.Id == first);

        var empty = p with { Samples = Array.Empty<AuthorStyleProfile.StyleSample>() };
        Assert.Empty(AuthorStyleBuilder.SelectSamples(empty, "对话交锋", 1));
        Assert.Equal("", AuthorStyleBuilder.BuildSampleBlock(empty,
            AuthorStyleBuilder.SelectSamples(empty, null, 1)));
    }

    [Fact]
    public void 场景归一与兜底判定()
    {
        Assert.Equal("战斗冲突", AuthorStyleBuilder.NormalizeScene("这一章是大战"));
        Assert.Equal("对话交锋", AuthorStyleBuilder.NormalizeScene("dialogue"));
        Assert.Equal("章末收束", AuthorStyleBuilder.NormalizeScene("章末收束"));
        Assert.Null(AuthorStyleBuilder.NormalizeScene("随便写点什么"));
        // 标题里带场景词时优先用标题，不被正文带偏
        Assert.Equal("开篇起笔", AuthorStyleBuilder.GuessScene("开篇 起笔", null, "无关的正文内容"));
    }

    [Fact]
    public void 守则独立保存_重算档案不覆盖()
    {
        var p = AuthorStyleBuilder.Build("守则作者", Corpus(4), "语料")
                with { Rules = new[] { "章末不点题。", "情绪外化成动作。" } };
        AuthorStyleStore.Save(p);

        // 模拟 style_profile_import：重算同名档案，Rules 为空
        var rebuilt = AuthorStyleBuilder.Build("守则作者", Corpus(4), "新语料");
        Assert.Empty(rebuilt.Rules);
        AuthorStyleStore.Save(rebuilt);

        var loaded = AuthorStyleStore.Get("守则作者")!;
        Assert.Equal(2, loaded.Rules.Count);
        Assert.Contains("章末不点题。", loaded.Rules);

        // 显式改写守则仍生效（rules.json 是唯一真相）
        AuthorStyleStore.SaveRules("守则作者", new[] { "只有一条。" });
        Assert.Single(AuthorStyleStore.Get("守则作者")!.Rules);
    }
}
