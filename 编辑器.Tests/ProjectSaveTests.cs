using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 项目文件的写盘与快照。
///
/// 整本书就一个文件，所以这两件事必须钉死：
///   1) 写盘不能留下"半截 JSON"的窗口（写 .tmp → 原子替换）
///   2) 快照必须存的是**改之前**的状态——存成改完的样子就完全失去了意义，
///      而这个错误极其隐蔽：代码看起来是对的，只是调用位置差了几行。
/// </summary>
public class ProjectSaveTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _file;

    public ProjectSaveTests()
    {
        _file = _dir.File("book.tdxproj");
    }

    public void Dispose() => _dir.Dispose();

    private NovelProject NewProject()
    {
        var p = new NovelProject { ProjectName = "测试", FilePath = _file };
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "第一章", Content = "原文" });
        return p;
    }

    [Fact]
    public void 保存后_文件存在且能读回()
    {
        var p = NewProject();
        p.Save();

        Assert.True(File.Exists(_file));
        var back = NovelProject.Load(_file);
        Assert.Equal("原文", back.Chapters[0].Content);
    }

    [Fact]
    public void 第二次保存_留下上一版bak()
    {
        var p = NewProject();
        p.Save();
        p.Chapters[0].Content = "新文";
        p.Save();

        Assert.True(File.Exists(_file + ".bak"));
        var bak = NovelProject.Load(_file + ".bak");
        Assert.Equal("原文", bak.Chapters[0].Content);   // bak 里是上一版
    }

    [Fact]
    public void 保存后_不残留tmp文件()
    {
        var p = NewProject();
        p.Save();

        Assert.False(File.Exists(_file + ".tmp"));
    }

    [Fact]
    public void 路径为空时_抛明确异常而不是静默()
    {
        var p = new NovelProject { ProjectName = "x", FilePath = "" };
        Assert.Throws<InvalidOperationException>(() => p.Save());
    }

    [Fact]
    public void 快照记的是调用那一刻的状态()
    {
        var p = NewProject();
        p.Save();

        var mgr = new ProjectSnapshotManager(_file);
        mgr.SaveSnapshot(p, "改之前");

        // 关键：改完之后，快照里必须还是旧内容
        p.Chapters[0].Content = "改之后";
        p.Save();

        var entry = mgr.LoadIndex().Last();
        var snap = mgr.LoadSnapshot(entry);
        Assert.NotNull(snap);
        Assert.Equal("原文", snap!.Chapters[0].Content);
        Assert.Equal("改之前", entry.Description);
    }

    [Fact]
    public void 多次快照_索引按时间累积()
    {
        var p = NewProject();
        p.Save();

        var mgr = new ProjectSnapshotManager(_file);
        mgr.SaveSnapshot(p, "第一次");
        p.Chapters[0].Content = "v2";
        mgr.SaveSnapshot(p, "第二次");

        var index = mgr.LoadIndex();
        Assert.Equal(2, index.Count);
        Assert.Contains("第一次", index[0].Description);
        Assert.Contains("第二次", index[1].Description);
    }

    [Fact]
    public void 恢复快照_快照里的过期路径被换成当前项目路径()
    {
        // 快照里存的是"存快照那一刻"的 FilePath。项目被复制/挪动后那是过期值，
        // 不覆盖的话后续 Save 会写到别的地方去（打开副本时踩过同一个坑）。
        var p = NewProject();
        p.Save();
        new ProjectSnapshotManager(_file).SaveSnapshot(p, "s1");

        var moved = _dir.File("moved.tdxproj");
        File.Copy(_file, moved, true);

        var session = new Mcp.Session();
        Assert.False(session.Open(moved).IsError);
        Assert.True(session.TryGet(out _, out _));

        var mgr2 = new ProjectSnapshotManager(moved);
        var id = mgr2.LoadIndex().Last().Id;

        var r = Mcp.NovelTools.RestoreSnapshot(session, JsonDocument.Parse($"{{\"id\":\"{id}\"}}").RootElement);
        Assert.False(r.IsError, r.Text);

        Assert.True(session.TryGet(out var restored, out _));
        Assert.Equal(moved, restored.FilePath);
    }
}
