using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using 编辑器;

namespace 编辑器.Tests;

/// <summary>
/// 长篇增强结构的"默认关闭"语义。
///
/// 这里测的不是分卷功能，而是**它不在的时候什么都不该发生**：
/// 论文/公文项目既不该被自动启用，也不该在文件里看到这个键。
/// </summary>
public class UltraLongConfigTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private NovelProject NewPaper()
    {
        var p = new NovelProject
        {
            ProjectName = "论文",
            FilePath = _dir.File("paper.tdxproj"),
            CharacterSettings = "研究对象：制造业企业",
        };
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "引言", Content = "正文" });
        return p;
    }

    [Fact]
    public void 未启用时_新建项目里没有这个结构()
    {
        var p = NewPaper();
        Assert.Null(p.UltraLong);
    }

    [Fact]
    public void 未启用时_文件里连UltraLong这个键都不该出现()
    {
        var p = NewPaper();
        p.Save();

        var text = File.ReadAllText(p.FilePath);
        Assert.DoesNotContain("UltraLong", text);
        Assert.DoesNotContain("ultraLong", text);
    }

    [Fact]
    public void 启用后能存能读()
    {
        var p = NewPaper();
        var cfg = p.EnableUltraLong();
        cfg.Volumes.Add(new Volume { Number = 1, Title = "第一卷", Goal = "入城", Summary = "回顾" });
        cfg.ProtagonistIds.Add("c1");
        p.Save();

        var back = NovelProject.Load(p.FilePath);
        Assert.NotNull(back.UltraLong);
        Assert.Equal(1, back.UltraLong!.SchemaVersion);
        Assert.Single(back.UltraLong.Volumes);
        Assert.Equal("入城", back.UltraLong.Volumes[0].Goal);
        Assert.Equal("c1", back.UltraLong.ProtagonistIds.Single());
    }

    [Fact]
    public void 启用是幂等的_不会把已有配置冲掉()
    {
        var p = NewPaper();
        var first = p.EnableUltraLong();
        first.Volumes.Add(new Volume { Title = "已经建好的卷" });

        var second = p.EnableUltraLong();

        Assert.Same(first, second);
        Assert.Single(second.Volumes);
    }

    [Fact]
    public void 旧项目文件没有这个字段_读出来是null()
    {
        // 手写一份"旧版"JSON（没有任何 UltraLong 键）
        var json = """{"ProjectName":"旧项目","Chapters":[]}""";
        var p = JsonSerializer.Deserialize<NovelProject>(json, NovelProject.FileJson);

        Assert.NotNull(p);
        Assert.Null(p!.UltraLong);
    }

    [Theory]
    [InlineData(0, 0.12)]        // 手改配置填 0（会成为负数预算）
    [InlineData(-1, 0.12)]
    [InlineData(0.01, 0.08)]     // 太小 → 抬到下限
    [InlineData(0.5, 0.20)]      // 太大 → 压到上限
    [InlineData(0.12, 0.12)]
    public void 预算比例越界会被夹到合法区间(double input, double expected)
    {
        var cfg = new UltraLongConfig { DynamicBudgetRatio = input };
        Assert.Equal(expected, cfg.EffectiveBudgetRatio, 3);
    }

    [Fact]
    public void 预算比例是NaN时退回默认值()
    {
        var cfg = new UltraLongConfig { DynamicBudgetRatio = double.NaN };
        Assert.Equal(0.12, cfg.EffectiveBudgetRatio, 3);
    }
}
