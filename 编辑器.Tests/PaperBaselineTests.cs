using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using 编辑器;

namespace 编辑器.Tests;

/// <summary>
/// 论文项目的序列化基线。
///
/// **为什么要有这条**：这个工具是四套文体共用的。给 <see cref="NovelProject"/> 或
/// <see cref="Character"/> 加一个小说专用字段（分卷、角色分级、状态面板），如果忘了
/// 处理"默认值不写出"，论文/公文项目的文件就会平白多出一堆键——不报错，但等于
/// 悄悄改了别人的数据格式。这里的基线是**阶段 0 之后、任何小说字段加入之前**的
/// 精确快照，逐字节比对：多一个空格都算破防。
///
/// ⚠ 加字段时请先跑这条。**不要为了让测试通过而去改基线文件**——那正是它要拦的事。
/// </summary>
public class PaperBaselineTests
{
    private static string FixturePath([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "paper-project.baseline.json");

    /// <summary>
    /// 一个典型的论文项目：有研究对象（存在 Characters 里）、有设定集、有章节。
    /// 所有随机/时间字段都钉死，否则基线没法比。
    /// </summary>
    private static NovelProject BuildPaperProject()
    {
        var p = new NovelProject
        {
            ProjectName = "论文基线",
            Author = "作者",
            FilePath = @"D:\fixture\paper.tdxproj",
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0),
            ModifiedDate = new DateTime(2026, 1, 1, 0, 0, 0),
            FullOutline = "1 研究问题\n2 方法\n3 结论",
            ChapterOutline = "第二章 方法：样本与变量",
            CharacterSettings = "研究对象：某市 2020—2024 年制造业企业",
            BackgroundSettings = "制度理论",
            WritingStyle = "学术书面语，术语前后一致",
            NarrativeViewpoint = "第三人称客观",
            PaperAbstract = "摘要正文",
            PaperKeywords = "制造业;制度",
        };

        p.Chapters.Add(new Chapter
        {
            ChapterId = "fixed-chapter-1",
            ChapterNumber = 1,
            Title = "引言",
            Content = "正文一",
            Summary = "梗概一",
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0),
            ModifiedDate = new DateTime(2026, 1, 1, 0, 0, 0),
            LastModified = new DateTime(2026, 1, 1, 0, 0, 0),
        });
        p.Chapters.Add(new Chapter
        {
            ChapterId = "fixed-chapter-2",
            ChapterNumber = 2,
            Title = "方法",
            Content = "正文二",
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0),
            ModifiedDate = new DateTime(2026, 1, 1, 0, 0, 0),
            LastModified = new DateTime(2026, 1, 1, 0, 0, 0),
        });

        p.Characters.Add(new Character
        {
            CharacterId = "fixed-character-1",
            Name = "研究对象",
            Role = "要素",
            Abilities = "属性",
        });

        p.SettingsBook = new SettingsBook();
        p.SettingsBook.Chapters.Add(new SettingsBookChapter
        {
            ChapterId = "fixed-sb-1",
            SourceKey = "world",
            Title = "理论基础",
            Content = "内容",
            ModifiedDate = new DateTime(2026, 1, 1, 0, 0, 0),
        });

        return p;
    }

    [Fact]
    public void 论文项目序列化结果与基线逐字节一致()
    {
        var json = JsonSerializer.Serialize(BuildPaperProject(), NovelProject.FileJson);
        var path = FixturePath();

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            Assert.Fail($"基线不存在，已生成：{path}\n检查内容确认没有小说专用字段后，重新运行。");
        }

        Assert.Equal(File.ReadAllText(path), json);
    }
}
