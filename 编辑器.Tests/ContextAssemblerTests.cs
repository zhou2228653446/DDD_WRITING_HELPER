using System;
using System.Linq;
using 编辑器;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 上下文组装的隔离性。
///
/// 这里最重要的不是"超长篇效果好不好"，而是**未启用时是否一字不差地走老路**。
/// 论文的「研究对象」、公文的「事项要素」都存在 Characters 里，角色筛选一旦
/// 漏过开关判断，它们就会被当成年小说配角裁掉，而且不报错。
/// </summary>
public class ContextAssemblerTests
{
    private static NovelProject PaperProject()
    {
        var p = new NovelProject
        {
            ProjectName = "论文",
            CharacterSettings = "研究对象：某市制造业企业",
        };
        p.Characters.Add(new Character
        {
            CharacterId = "c1",
            Name = "研究对象",
            Role = "要素",
            Abilities = "属性",
            CurrentRealm = "不该出现",
        });
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "引言", Content = "正文" });
        return p;
    }

    private static NovelProject LongProject()
    {
        var p = new NovelProject { ProjectName = "长篇" };
        p.EnableUltraLong();

        var v1 = new Volume { Number = 1, Title = "第一卷 入山", Goal = "拜入宗门", Summary = "他从山村走到山门" };
        var v2 = new Volume { Number = 2, Title = "第二卷 剑冢", Goal = "拿到残剑", Summary = "剑冢试炼" };
        p.UltraLong!.Volumes.Add(v1);
        p.UltraLong.Volumes.Add(v2);

        var hero = new Character
        {
            CharacterId = "hero",
            Name = "林寒",
            Role = "主角",
            Scope = CharacterScope.Core,
            CurrentRealm = "金丹初期",
            CurrentSituation = "被困剑冢",
            CurrentGoal = "找到出口",
            KeyItems = new() { "残剑" },
        };
        var ally = new Character
        {
            CharacterId = "ally",
            Name = "苏晚",
            Role = "配角",
            Scope = CharacterScope.Arc,
            Personality = "冷静",
        };
        var dead = new Character
        {
            CharacterId = "dead",
            Name = "老王",
            Role = "配角",
            Scope = CharacterScope.Core,
            Status = CharacterStatus.Deceased,
        };
        p.Characters.AddRange(new[] { hero, ally, dead });
        p.UltraLong.ProtagonistIds.Add("hero");

        for (int i = 1; i <= 6; i++)
        {
            var c = new Chapter
            {
                ChapterNumber = i,
                Title = $"第{i}章",
                Content = "正文" + i,
                Summary = "梗概" + i,
                VolumeId = i <= 3 ? v1.VolumeId : v2.VolumeId,
            };
            p.Chapters.Add(c);
        }

        // 第 6 章（待写）标注三个角色
        p.Chapters.First(c => c.ChapterNumber == 5).CharacterIds.AddRange(new[] { "hero", "ally", "dead" });
        return p;
    }

    [Fact]
    public void 未启用时_稳定角色段与原来逐字相等()
    {
        var p = PaperProject();
        Assert.Equal(p.BuildEffectiveCharacterSettings(), ContextAssembler.BuildStableCharacters(p));
    }

    [Fact]
    public void 未启用时_动态段为空()
    {
        Assert.Equal("", ContextAssembler.BuildDynamic(PaperProject(), 1));
    }

    [Fact]
    public void 启用后_稳定段只留核心且不含状态字段()
    {
        var p = LongProject();
        var stable = ContextAssembler.BuildStableCharacters(p);

        Assert.Contains("林寒", stable);
        Assert.DoesNotContain("苏晚", stable);       // Arc 不进 system
        Assert.DoesNotContain("老王", stable);       // 已死亡
        // ★ 状态字段绝不能进 system：它每章都变，进去就会把缓存前缀打穿
        Assert.DoesNotContain("金丹初期", stable);
    }

    [Fact]
    public void 启用后_动态段带状态卷目标和本章角色()
    {
        var p = LongProject();
        var dyn = ContextAssembler.BuildDynamic(p, 6, 30_000);

        Assert.Contains("金丹初期", dyn);           // 主角状态
        Assert.Contains("本卷目标", dyn);           // 卷目标
        Assert.Contains("第二卷", dyn);
        Assert.Contains("苏晚", dyn);               // 本章相关角色
        Assert.Contains("已死亡", dyn);             // 反向名单
        Assert.Contains("最近几章梗概", dyn);
        Assert.Contains("紧邻上一章", dyn);
    }

    [Fact]
    public void 动态段不会把状态漏进稳定段()
    {
        var p = LongProject();
        var stable = ContextAssembler.BuildStableCharacters(p);
        var dyn = ContextAssembler.BuildDynamic(p, 6, 30_000);

        // 状态只该在动态段出现一次
        Assert.Contains("金丹初期", dyn);
        Assert.DoesNotContain("金丹初期", stable);
    }

    [Fact]
    public void 章号无效或未分卷时_不会把动态段拼成空壳()
    {
        var p = LongProject();
        Assert.Equal("", ContextAssembler.BuildDynamic(p, 0));
        Assert.Equal("", ContextAssembler.BuildDynamic(p, -3));
    }

    [Fact]
    public void 窗口很小时_预算被夹到下限也不会崩()
    {
        var p = LongProject();
        p.UltraLong!.DynamicBudgetRatio = 0.0001;   // 手改配置写小了
        var dyn = ContextAssembler.BuildDynamic(p, 6, 8_000);

        Assert.Contains("林寒", dyn);
        Assert.True(dyn.Length > 20);
    }

    [Fact]
    public void 未启用时_角色筛选一个字节都不改()
    {
        // 这条是给论文/公文用的保险丝：即使调用方传了"要筛选"，也不许动
        var p = PaperProject();
        var original = p.BuildEffectiveCharacterSettings();

        Assert.Equal(original, p.BuildCharacterSettings(p.Characters));
        Assert.Equal(original, ContextAssembler.BuildStableCharacters(p));
    }
}
