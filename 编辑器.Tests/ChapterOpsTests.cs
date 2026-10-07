using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using 编辑器.Mcp;

namespace 编辑器.Tests;

/// <summary>
/// MCP 侧的章节管理：改名 / 删除 / 重排 / 梗概。
///
/// 这层是给 agent 用的，用户只说一句话、agent 直接执行，出错就是静默改坏书稿。
/// 尤其是"章号必须连续"——章号是 chapter_read / chapter_write 唯一的定位依据，
/// 中间留个空洞，agent 后面就找不到章了。
/// </summary>
public class ChapterOpsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _file;
    private readonly Session _session = new();
    private NovelProject _project = null!;

    public ChapterOpsTests()
    {
        _file = _dir.File("book.tdxproj");

        var p = new NovelProject { ProjectName = "测试书", FilePath = _file };
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "第一章", Content = "甲" });
        p.Chapters.Add(new Chapter { ChapterNumber = 2, Title = "第二章", Content = "乙" });
        p.Chapters.Add(new Chapter { ChapterNumber = 3, Title = "第三章", Content = "丙" });
        p.Save();

        var open = _session.Open(_file);
        Assert.False(open.IsError, open.Text);
        Assert.True(_session.TryGet(out var loaded, out var err), err.Text);
        _project = loaded;
    }

    public void Dispose() => _dir.Dispose();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 重排_把后面的章移到最前_其余顺移()
    {
        var r = NovelTools.ReorderChapter(_session, _project, Args("""{"number":3,"toNumber":1}"""));
        Assert.False(r.IsError, r.Text);

        var ordered = _project.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        Assert.Equal("第三章", ordered[0].Title);
        Assert.Equal("第一章", ordered[1].Title);
        Assert.Equal("第二章", ordered[2].Title);
    }

    [Fact]
    public void 重排后_章号连续()
    {
        NovelTools.ReorderChapter(_session, _project, Args("""{"number":3,"toNumber":1}"""));

        Assert.Equal(new[] { 1, 2, 3 },
            _project.Chapters.OrderBy(c => c.ChapterNumber).Select(c => c.ChapterNumber));
    }

    [Fact]
    public void 删除中间章_章号重排不留空洞()
    {
        var r = NovelTools.DeleteChapter(_session, _project, Args("""{"number":2}"""));
        Assert.False(r.IsError, r.Text);

        var ordered = _project.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        Assert.Equal(2, ordered.Count);
        // 关键：不能是 1 和 3，否则按章号定位会找不到
        Assert.Equal(new[] { 1, 2 }, ordered.Select(c => c.ChapterNumber));
    }

    [Fact]
    public void 删除不存在的章_给错误而不是崩()
    {
        var r = NovelTools.DeleteChapter(_session, _project, Args("""{"number":99}"""));
        Assert.True(r.IsError);
        Assert.Contains("没有第 99 章", r.Text);
    }

    [Fact]
    public void 改名_标题真的改了()
    {
        var r = NovelTools.RenameChapter(_session, _project, Args("""{"number":2,"title":"新的名字"}"""));
        Assert.False(r.IsError, r.Text);

        Assert.Equal("新的名字", _project.Chapters.First(c => c.ChapterNumber == 2).Title);
    }

    [Fact]
    public void 改名_空标题被拒()
    {
        var r = NovelTools.RenameChapter(_session, _project, Args("""{"number":1,"title":"  "}"""));
        Assert.True(r.IsError);
    }

    [Fact]
    public void 写梗概_内容落进章节()
    {
        var r = NovelTools.SetChapterSummary(_session, _project,
            Args("""{"number":1,"summary":"林寒入山，遇雪。"}"""));
        Assert.False(r.IsError, r.Text);

        Assert.Equal("林寒入山，遇雪。", _project.Chapters.First(c => c.ChapterNumber == 1).Summary);
    }

    [Fact]
    public void 空梗概表示清空()
    {
        NovelTools.SetChapterSummary(_session, _project, Args("""{"number":1,"summary":"abc"}"""));
        NovelTools.SetChapterSummary(_session, _project, Args("""{"number":1,"summary":""}"""));

        Assert.Equal("", _project.Chapters.First(c => c.ChapterNumber == 1).Summary);
    }

    [Fact]
    public void 删章前存了快照_删掉的内容能捞回来()
    {
        var before = _project.Chapters.Count;
        NovelTools.DeleteChapter(_session, _project, Args("""{"number":2}"""));

        var mgr = new Services.ProjectSnapshotManager(_file);
        Assert.True(mgr.HasSnapshots);

        var entry = mgr.LoadIndex().Last();
        Assert.Contains("删除", entry.Description);

        var snap = mgr.LoadSnapshot(entry);
        Assert.NotNull(snap);
        Assert.Equal(before, snap!.Chapters.Count);
    }

    [Fact]
    public void 磁盘被外部改过后_写入被拒绝()
    {
        // 防覆盖：有人在别处（比如界面）存了一版，就不能再写，否则会静默覆盖掉人家的稿子
        File.WriteAllText(_file, File.ReadAllText(_file) + " ");

        var r = NovelTools.RenameChapter(_session, _project, Args("""{"number":1,"title":"X"}"""));
        Assert.True(r.IsError);
        Assert.Contains("已被其他程序修改", r.Text);
    }
}
