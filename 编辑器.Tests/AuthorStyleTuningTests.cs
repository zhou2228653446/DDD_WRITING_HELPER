using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using 编辑器.Mcp;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 调参（tuning）：使用者能改「说不说某条规则」和「这条规则怎么措辞」，
/// 而不必改代码。风险分级与确认码也在这一层。
/// </summary>
[Collection("author-style-store")]
public class AuthorStyleTuningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atune-" + Guid.NewGuid().ToString("N")[..8]);

    public AuthorStyleTuningTests()
    {
        AuthorStyleStore.SetDirForTests(_dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // ★ 不要在这里 ClearDirForTests：override 一清空，Directory 立刻指向
        //   真实的 %APPDATA%/TdxClaw/author_profiles，而另一个测试实例可能还在跑，
        //   结果把测试数据写进真实档案库（2026-11-24 踩过：余华档案被写入
        //   QuietMode=force / ShortParaMin=0.3）。目录删掉即可，下一个实例
        //   构造时会重新 SetDirForTests；进程结束 override 自然失效。
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>造一份档案；段落形状用参数覆盖，方便直接测三档。</summary>
    private static AuthorStyleProfile Profile(
        double quietLong = 0.9, double longPara = 0.2, double shortPara = 0.35)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 8; i++)
        {
            sb.AppendLine($"第{i}章 试炼");
            // 每章须 ≥300 字，否则 SplitChapters 当广告页丢掉（见其 300 字门槛）
            for (var k = 0; k < 16; k++)
                sb.AppendLine(k % 5 == 0
                    ? "“你还来？”他问。"
                    : "他洗了把脸，把桌上的书一页页翻过去，窗外的天色渐渐暗了下来。");
        }
        var p = AuthorStyleBuilder.Build("调参测试", sb.ToString(), "合成语料");
        return p with
        {
            QuietLongRatio = quietLong,
            LongParagraphRatio = longPara,
            ShortParagraphRatio = shortPara,
        };
    }

    private static string Block(AuthorStyleProfile p) => AuthorStyleBuilder.BuildAnchorBlock(p);

    // ── 默认与三档 ──

    [Fact]
    public void 默认按统计走_留白高就强制_短段高就量化()
    {
        var p = Profile();
        var block = Block(p);

        Assert.Contains("长段要有一个「锚」", block);
        Assert.Contains("每一章都要留至少一处这样的空转", block);
        Assert.Contains("15 字以内的短段约占全章", block);
    }

    [Fact]
    public void 留白低的档案默认不提留白_只说长段按需()
    {
        var p = Profile(quietLong: 0.03, longPara: 0.01, shortPara: 0.1);
        var block = Block(p);

        Assert.DoesNotContain("长段要有一个「锚」", block);
        Assert.DoesNotContain("空转", block);
        Assert.Contains("长段按需使用", block);
        Assert.DoesNotContain("15 字以内的短段约占全章", block);   // 短段低 → 退回通用断段规则
        Assert.Contains("段落按动作、视线或对话的自然切换断开", block);
    }

    [Theory]
    [InlineData("off", null, null)]
    [InlineData("anchor", null, "留白不必每章都有")]
    [InlineData("force", null, "每一章都要留至少一处这样的空转")]
    public void 手动档位覆盖自动判定(string mode, string? _, string? expect)
    {
        // 档案本身是「留白低」的，自动会判 none；手动设档位后必须听手动的
        var p = Profile(quietLong: 0.03, longPara: 0.01);
        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning { QuietMode = mode });

        var block = Block(p);
        if (mode == "off")
        {
            Assert.DoesNotContain("长段要有一个「锚」", block);
            Assert.Contains("长段按需使用", block);
        }
        else
        {
            Assert.Contains("长段要有一个「锚」", block);
            Assert.DoesNotContain("长段按需使用", block);
            Assert.Contains(expect!, block);
        }
    }

    [Fact]
    public void 短段量化可以强制打开和关掉()
    {
        var p = Profile(shortPara: 0.05);
        Assert.DoesNotContain("15 字以内的短段约占全章", Block(p));

        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning { ShortParaMode = "on" });
        Assert.Contains("15 字以内的短段约占全章", Block(p));

        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning { ShortParaMode = "off" });
        p = Profile(shortPara: 0.9);
        Assert.DoesNotContain("15 字以内的短段约占全章", Block(p));
    }

    [Fact]
    public void 阈值可改_抬高留白门槛后自动判定变none()
    {
        var p = Profile(quietLong: 0.6, longPara: 0.2);
        Assert.Contains("长段要有一个「锚」", Block(p));      // 0.6 ≥ 0.45

        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning { QuietLongMin = 0.95 });
        Assert.DoesNotContain("长段要有一个「锚」", Block(p));
    }

    // ── 单条覆盖 ──

    [Fact]
    public void 单条覆盖替换原文_空串删除该条()
    {
        var p = Profile();

        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning
        {
            Overrides = new() { [StylePromptKeys.ParaNoCounting] = "· 自定义：不要写数字。" },
        });
        var block = Block(p);
        Assert.Contains("自定义：不要写数字。", block);
        Assert.DoesNotContain("每件事都带上精确数量", block);

        // 空串 = 删掉这一条，不是恢复默认
        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning
        {
            Overrides = new() { [StylePromptKeys.ParaNoCounting] = "" },
        });
        block = Block(p);
        Assert.DoesNotContain("每件事都带上精确数量", block);
        Assert.DoesNotContain("自定义：不要写数字。", block);
    }

    [Fact]
    public void 覆盖不受档位影响_档位关掉后覆盖仍然生效()
    {
        var p = Profile();
        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning
        {
            QuietMode = "off",
            Overrides = new() { [StylePromptKeys.ParaLongLoose] = "· 这是我自己的长段要求。" },
        });

        var block = Block(p);
        Assert.DoesNotContain("长段要有一个「锚」", block);
        Assert.Contains("这是我自己的长段要求。", block);
        Assert.DoesNotContain("长段按需使用", block);   // 系统那句被覆盖掉了
    }

    [Fact]
    public void 追加指令排在锚定块末尾()
    {
        var p = Profile();
        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning
        {
            Extra = new() { "每章不超过三个场景。", "不要写梦境。" },
        });

        var block = Block(p);
        Assert.Contains("【补充要求】", block);
        Assert.Contains("- 每章不超过三个场景。", block);
        Assert.Contains("- 不要写梦境。", block);
    }

    // ── 存储 ──

    [Fact]
    public void 调参存取往返_空则删文件()
    {
        var t = new StyleTuning
        {
            QuietMode = "force",
            ShortParaMin = 0.3,
            Overrides = new() { ["para.anchor"] = "x" },
            Extra = new() { "y" },
        };
        AuthorStyleStore.SaveTuning("调参测试", t);
        var file = Path.Combine(_dir, "调参测试" + StyleTuning.FileSuffix);
        Assert.True(File.Exists(file));

        var back = AuthorStyleStore.LoadTuning("调参测试");
        Assert.Equal("force", back.QuietMode);
        Assert.Equal(0.3, back.ShortParaMin);
        Assert.Equal("x", back.Overrides!["para.anchor"]);
        Assert.Single(back.Extra!);

        AuthorStyleStore.SaveTuning("调参测试", new StyleTuning());
        Assert.False(File.Exists(file));
        Assert.True(AuthorStyleStore.LoadTuning("调参测试").IsEmpty);
    }

    [Fact]
    public void 坏文件不炸生成_退回全自动()
    {
        File.WriteAllText(Path.Combine(_dir, "坏档" + StyleTuning.FileSuffix), "{ 不是 json");
        var t = AuthorStyleStore.LoadTuning("坏档");
        Assert.True(t.IsEmpty);
    }

    // ── 风险分级与确认码 ──

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 高风险要两阶段_未确认不落盘()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);

        // L2：改阈值 → 只生成确认码
        var r = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"quietLongMin","value":"0.95"}
            """));
        Assert.False(r.IsError);
        Assert.Contains("尚未生效", r.Text);
        Assert.True(AuthorStyleStore.LoadTuning(p.Id).QuietLongMin == null);   // 没落盘

        var pending = AuthorStyleStore.LoadPending(p.Id);
        Assert.NotNull(pending);
        Assert.Equal(6, pending!.Code.Length);

        // 错误确认码被拒
        var bad = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"approve","value":"XXXXXX"}
            """));
        Assert.True(bad.IsError);

        // 正确确认码落盘
        var ok = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"approve","value":"{{pending.Code}}"}
            """));
        Assert.False(ok.IsError);
        Assert.Equal(0.95, AuthorStyleStore.LoadTuning(p.Id).QuietLongMin);
        Assert.Null(AuthorStyleStore.LoadPending(p.Id));         // 一次性
        Assert.Contains("quietLongMin", AuthorStyleStore.LoadAudit(p.Id).Last());
    }

    [Fact]
    public void 中风险要confirm_低风险直接生效()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);

        var no = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"quietMode","value":"off"}
            """));
        Assert.Contains("尚未生效", no.Text);
        Assert.Null(AuthorStyleStore.LoadTuning(p.Id).QuietMode);

        var yes = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"quietMode","value":"off","confirm":"yes"}
            """));
        Assert.False(yes.IsError);
        Assert.Equal("off", AuthorStyleStore.LoadTuning(p.Id).QuietMode);

        // 低风险：extra 直接生效
        var extra = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"extra","value":"每章不超过三个场景。"}
            """));
        Assert.False(extra.IsError);
        Assert.Single(AuthorStyleStore.LoadTuning(p.Id).Extra!);
    }

    [Fact]
    public void 覆盖提示词条目属于高风险_空串表示删除()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);

        var r = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"para.anchor","value":""}
            """));
        Assert.Contains("尚未生效", r.Text);
        var pending = AuthorStyleStore.LoadPending(p.Id);
        Assert.NotNull(pending);
        Assert.Contains("删除条目 para.anchor", pending!.Summary);

        AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"approve","value":"{{pending.Code}}"}
            """));
        Assert.Equal("", AuthorStyleStore.LoadTuning(p.Id).Overrides!["para.anchor"]);
    }

    [Fact]
    public void 过期确认码被拒并清理()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);

        // 手工造一条 11 分钟前的待确认
        var old = new StylePendingChange
        {
            Code = "ABC234",
            Created = DateTimeOffset.Now.AddMinutes(-11).ToString("yyyy-MM-dd HH:mm:ss"),
            Summary = "过期测试",
            Tuning = new StyleTuning { QuietLongMin = 0.9 },
        };
        AuthorStyleStore.SavePending(p.Id, old);

        var r = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"approve","value":"ABC234"}
            """));
        Assert.True(r.IsError);
        Assert.Contains("过期", r.Text);
        Assert.Null(AuthorStyleStore.LoadPending(p.Id));
        Assert.Null(AuthorStyleStore.LoadTuning(p.Id).QuietLongMin);
    }

    [Fact]
    public void reset要确认_确认后全清()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);
        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning { QuietMode = "force", Extra = new() { "x" } });

        var no = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"reset"}
            """));
        Assert.Contains("将清空", no.Text);
        Assert.False(AuthorStyleStore.LoadTuning(p.Id).IsEmpty);

        var yes = AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"reset","confirm":"yes"}
            """));
        Assert.False(yes.IsError);
        Assert.True(AuthorStyleStore.LoadTuning(p.Id).IsEmpty);
    }

    // ── A1 对话闲话（2026-11-24）──
    //   旧版只有「不要为凑比例让角色没话找话」，把不推进剧情的对话一起禁掉了。

    [Fact]
    public void 对话闲话_文学档允许岔开_且不再有反着挡的那句()
    {
        var block = Block(Profile());   // 留白 0.9 → force 档
        Assert.Contains("对话不必句句有用", block);
        Assert.Contains("岔出去", block);
        Assert.DoesNotContain("没话找话", block);
    }

    [Fact]
    public void 对话闲话_网文档只给一处且要短()
    {
        var block = Block(Profile(quietLong: 0.03, longPara: 0.01, shortPara: 0.1));
        Assert.Contains("对话不必句句有用", block);
        Assert.Contains("一处两三句就够", block);
        Assert.DoesNotContain("岔出去", block);   // 网文不能岔开三段
    }

    [Fact]
    public void 对话闲话可整条关掉()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);
        AuthorStyleStore.SaveTuning(p.Id, new StyleTuning
        {
            Overrides = new() { [StylePromptKeys.DialogueChatter] = "" },
        });

        var block = Block(p);
        Assert.DoesNotContain("对话不必句句有用", block);
        Assert.Contains("不为凑对话比例注水", block);   // 另一条不受影响
    }

    [Fact]
    public void 未知key与非法值要拒绝_别静默走错分支()
    {
        var p = Profile();
        AuthorStyleStore.Save(p);

        Assert.True(AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"para.不存在","value":"x"}
            """)).IsError);

        Assert.True(AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"quietMode","value":"maybe"}
            """)).IsError);

        Assert.True(AuthorStyleTools.Tuning(null!, Args($$"""
            {"id":"{{p.Id}}","action":"set","key":"quietLongMin","value":"9"}
            """)).IsError);
    }
}
