using System;
using System.IO;
using System.Linq;
using 编辑器;

namespace 编辑器.Tests;

/// <summary>
/// 分卷与角色分级的纯模型行为。
/// 重点不是"能存"，而是**卷归属只有一份真相**、以及未启用时字段不落盘。
/// </summary>
public class UltraLongModelTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private NovelProject NewBook(bool enableUltra = true, int chapters = 4)
    {
        var p = new NovelProject
        {
            ProjectName = "长篇",
            FilePath = _dir.File("long.tdxproj"),
        };
        for (int i = 1; i <= chapters; i++)
            p.Chapters.Add(new Chapter { ChapterNumber = i, Title = $"第{i}章", Content = new string('文', 10) });
        if (enableUltra) p.EnableUltraLong();
        return p;
    }

    [Fact]
    public void 章挂到卷上_按卷查询按章号升序()
    {
        var p = NewBook();
        var v1 = new Volume { Number = 1, Title = "第一卷" };
        p.UltraLong!.Volumes.Add(v1);

        // 故意倒着挂，验证查询会排序
        p.Chapters.First(c => c.ChapterNumber == 3).VolumeId = v1.VolumeId;
        p.Chapters.First(c => c.ChapterNumber == 1).VolumeId = v1.VolumeId;

        var got = p.ChaptersOfVolume(v1.VolumeId);
        Assert.Equal(new[] { 1, 3 }, got.Select(c => c.ChapterNumber).ToArray());
    }

    [Fact]
    public void 未分卷的章归到空id那一组()
    {
        var p = NewBook();
        var v1 = new Volume { Number = 1 };
        p.UltraLong!.Volumes.Add(v1);
        p.Chapters[0].VolumeId = v1.VolumeId;

        var loose = p.ChaptersOfVolume(null);
        Assert.Equal(3, loose.Count);
        Assert.DoesNotContain(1, loose.Select(c => c.ChapterNumber));
    }

    [Fact]
    public void 章在哪个卷_能反查()
    {
        var p = NewBook();
        var v2 = new Volume { Number = 2, Title = "第二卷" };
        p.UltraLong!.Volumes.Add(v2);
        p.Chapters.First(c => c.ChapterNumber == 2).VolumeId = v2.VolumeId;

        Assert.Equal("第二卷", p.VolumeOfChapter(2)?.Title);
        Assert.Null(p.VolumeOfChapter(1));          // 没挂卷
        Assert.Null(p.VolumeOfChapter(99));         // 章都不存在
    }

    [Fact]
    public void 卷被删之后_章不会跟着消失_只是查不到卷()
    {
        var p = NewBook();
        var v1 = new Volume { Number = 1 };
        p.UltraLong!.Volumes.Add(v1);
        p.Chapters[0].VolumeId = v1.VolumeId;

        p.UltraLong.Volumes.Remove(v1);

        Assert.Equal(4, p.Chapters.Count);          // 章还在
        Assert.Null(p.VolumeOfChapter(1));          // 卷没了
        // 指向已删卷的章要能被"未分卷"找回来，否则界面上等于消失了
        Assert.Single(p.ChaptersOfVolume(null), c => c.ChapterNumber == 1);
    }

    [Fact]
    public void 下一个卷号取现有最大值加一()
    {
        var p = NewBook();
        Assert.Equal(1, p.NextVolumeNumber());

        p.UltraLong!.Volumes.Add(new Volume { Number = 1 });
        p.UltraLong.Volumes.Add(new Volume { Number = 5 });
        Assert.Equal(6, p.NextVolumeNumber());
    }

    [Fact]
    public void 角色分级与状态字段_能存能读()
    {
        var p = NewBook();
        p.Characters.Add(new Character
        {
            CharacterId = "c1",
            Name = "林寒",
            Scope = CharacterScope.Core,
            Status = CharacterStatus.Deceased,
            CurrentRealm = "金丹初期",
            CurrentSituation = "被困在剑冢",
            CurrentGoal = "找到出口",
            KeyItems = new() { "残剑", "灯芯" },
        });
        p.Save();

        var back = NovelProject.Load(p.FilePath);
        var c = back.Characters.Single();
        Assert.Equal(CharacterScope.Core, c.Scope);
        Assert.Equal(CharacterStatus.Deceased, c.Status);
        Assert.Equal("金丹初期", c.CurrentRealm);
        Assert.Equal("找到出口", c.CurrentGoal);
        Assert.Equal(2, c.KeyItems!.Count);
    }

    [Fact]
    public void 角色用默认分级时_Scope与Status不写进文件()
    {
        var p = NewBook(enableUltra: false);
        p.Characters.Add(new Character { CharacterId = "c1", Name = "研究对象" });
        p.Save();

        var text = File.ReadAllText(p.FilePath);
        Assert.DoesNotContain("\"Scope\"", text);
        Assert.DoesNotContain("\"Status\"", text);
    }

    [Fact]
    public void 未启用超长篇时_章上不写VolumeId()
    {
        var p = NewBook(enableUltra: false);
        p.Save();

        Assert.DoesNotContain("VolumeId", File.ReadAllText(p.FilePath));
    }
}
