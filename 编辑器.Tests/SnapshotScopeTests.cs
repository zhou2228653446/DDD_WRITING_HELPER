using System;
using System.IO;
using System.Linq;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 快照的存放作用域。
///
/// 抓的是这个 bug：快照以前全部塞在 &lt;项目目录&gt;/.snapshots/ —— **按目录**而不是
/// 按书分。一个目录只放一本书时看不出问题，但网页版所有书稿都躺在同一个
/// books/ 目录下，于是「快照」列表里会混进别的书的快照，点「恢复」就是把
/// **另一本书**的内容盖进当前这本。这已经不是"看着乱"，是能毁稿子的。
/// </summary>
public class SnapshotScopeTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string MakeProject(string name)
    {
        var file = _dir.File(name + ".tdxproj");
        var p = new NovelProject { ProjectName = name, FilePath = file };
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "第一章", Content = "正文" });
        p.Save();
        return file;
    }

    [Fact]
    public void 同一目录下的两本书快照互不可见()
    {
        var a = MakeProject("甲书");
        var b = MakeProject("乙书");

        var pa = NovelProject.Load(a);
        new ProjectSnapshotManager(a).SaveSnapshot(pa, "甲书的快照");

        // 乙书不该看到甲书的快照 —— 看到就等于能拿它盖掉自己的正文
        var mb = new ProjectSnapshotManager(b);
        Assert.Empty(mb.LoadIndex());
        Assert.False(mb.HasSnapshots);

        Assert.Single(new ProjectSnapshotManager(a).LoadIndex());
    }

    [Fact]
    public void 快照文件各存各的子目录()
    {
        var a = MakeProject("甲书");
        var b = MakeProject("乙书");
        new ProjectSnapshotManager(a).SaveSnapshot(NovelProject.Load(a), "s");
        new ProjectSnapshotManager(b).SaveSnapshot(NovelProject.Load(b), "s");

        var root = Path.Combine(_dir.Path, ".snapshots");
        Assert.True(Directory.Exists(Path.Combine(root, Path.GetFileName(a))));
        Assert.True(Directory.Exists(Path.Combine(root, Path.GetFileName(b))));
    }

    [Fact]
    public void 目录里只有一本书时会认领旧版目录级快照()
    {
        var a = MakeProject("孤本书");
        var legacy = Path.Combine(_dir.Path, ".snapshots");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "index.json"),
            """{"Entries":[{"Id":"001","Description":"旧快照","Timestamp":"2026-01-01T00:00:00"}]}""");
        File.WriteAllText(Path.Combine(legacy, "001.json"), "{}");

        var index = new ProjectSnapshotManager(a).LoadIndex();

        Assert.Single(index);
        Assert.Equal("旧快照", index[0].Description);
        // 搬走之后旧位置不该再留一份，否则下次还会被别的书认领
        Assert.False(File.Exists(Path.Combine(legacy, "index.json")));
    }

    [Fact]
    public void 目录里有多本书时不去猜旧快照归谁()
    {
        MakeProject("甲书");
        var b = MakeProject("乙书");
        var legacy = Path.Combine(_dir.Path, ".snapshots");
        Directory.CreateDirectory(legacy);
        var indexFile = Path.Combine(legacy, "index.json");
        File.WriteAllText(indexFile,
            """{"Entries":[{"Id":"001","Description":"不知谁的旧快照","Timestamp":"2026-01-01T00:00:00"}]}""");

        Assert.Empty(new ProjectSnapshotManager(b).LoadIndex());

        // 宁可不猜：认错等于把别人的稿子当成本书的快照摆出来让人恢复
        Assert.True(File.Exists(indexFile));
    }

    [Fact]
    public void 快照编号在各自的书里从头开始()
    {
        var a = MakeProject("甲书");
        var b = MakeProject("乙书");
        new ProjectSnapshotManager(a).SaveSnapshot(NovelProject.Load(a), "甲1");
        new ProjectSnapshotManager(a).SaveSnapshot(NovelProject.Load(a), "甲2");

        var mb = new ProjectSnapshotManager(b);
        mb.SaveSnapshot(NovelProject.Load(b), "乙1");

        // 共用目录时两本书的编号会互相顶掉，序号会莫名跳号
        Assert.Equal("甲2", new ProjectSnapshotManager(a).LoadIndex().Last().Description);
        Assert.Equal("001", mb.LoadIndex().Single().Id);
    }

    public void Dispose() => _dir.Dispose();
}
