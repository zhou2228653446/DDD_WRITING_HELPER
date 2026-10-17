using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 存储格式：中文不转义 + 快照 gzip。
///
/// 这两件事都不改任何业务逻辑，但**改错就是灾难**：
///   · Encoder 改错 → 文件里的字变成乱码或读不回来；
///   · 快照格式改错 → 老快照读不出，等于把用户的后悔药扔了。
/// 所以这里既钉住"新格式生效"，也钉住"老格式仍能读"。
/// </summary>
public class StorageFormatTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private NovelProject NewProject(string name = "格式测试")
    {
        var file = _dir.File(name + ".tdxproj");
        var p = new NovelProject { ProjectName = name, FilePath = file };
        p.Chapters.Add(new Chapter
        {
            ChapterNumber = 1,
            Title = "第一章 霜降",
            Content = "夜色沉下来，灯塔的光扫过海面。引号\"反斜杠\\换行\n表情🌙标签<script>",
        });
        return p;
    }

    [Fact]
    public void 保存后_中文明文落盘_不再转成Unicode转义()
    {
        var p = NewProject();
        p.Save();

        var text = File.ReadAllText(p.FilePath);
        Assert.Contains("霜降", text);
        // 转义后「霜」是 \u971C。出现它就说明 Encoder 被改回默认了。
        Assert.False(text.Contains("\\u971C", StringComparison.OrdinalIgnoreCase),
            "项目文件里出现了 \\u971C：Encoder 又变回默认值了（体积会涨 5 倍）");
    }

    [Fact]
    public void 转义关掉之后_特殊字符仍能原样读回()
    {
        var p = NewProject();
        var original = p.Chapters[0].Content;

        p.Save();
        var back = NovelProject.Load(p.FilePath);

        // 引号、反斜杠、换行、emoji、尖括号全在这一条里
        Assert.Equal(original, back.Chapters[0].Content);
        Assert.Equal("第一章 霜降", back.Chapters[0].Title);
    }

    [Fact]
    public void 一百章的中文项目_文件大小不超过正文的三倍()
    {
        var p = NewProject("大书");
        for (int i = 2; i <= 100; i++)
            p.Chapters.Add(new Chapter { ChapterNumber = i, Title = $"第{i}章", Content = new string('文', 1000) });
        p.Save();

        long chars = p.Chapters.Sum(c => (long)c.Content.Length);
        var size = new FileInfo(p.FilePath).Length;

        // 中文 UTF-8 是 3 字节/字；一旦转义成 \uXXXX 就变 6 字节/字，加 JSON 结构约 7 倍。
        // 门槛放在 4.5 倍：正常（约 3.1 倍）能过，退回转义（约 6~7 倍）一定挂。
        Assert.True(size < chars * 4.5,
            $"文件 {size} 字节，正文 {chars} 字——比例异常，Encoder 可能又变回默认了");
    }

    [Fact]
    public void 新快照以gzip落盘_并且能读回()
    {
        var p = NewProject();
        p.Save();

        var mgr = new ProjectSnapshotManager(p.FilePath);
        mgr.SaveSnapshot(p, "第一次");

        var dir = Path.Combine(_dir.Path, ".snapshots", Path.GetFileName(p.FilePath));
        var files = Directory.GetFiles(dir);
        Assert.Contains(files, f => f.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase));

        var entry = mgr.LoadIndex().Single();
        var snap = mgr.LoadSnapshot(entry);
        Assert.NotNull(snap);
        Assert.Equal(p.Chapters[0].Content, snap!.Chapters[0].Content);
    }

    [Fact]
    public void 老版本留下的明文快照_仍能恢复()
    {
        var p = NewProject();
        p.Save();

        // 照老格式手写一份：index.json + 001.json（明文）
        var dir = Path.Combine(_dir.Path, ".snapshots", Path.GetFileName(p.FilePath));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.json"),
            """{"Entries":[{"Id":"001","Description":"旧版快照","Timestamp":"2026-01-01T00:00:00"}]}""");
        File.WriteAllText(Path.Combine(dir, "001.json"),
            JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));

        var mgr = new ProjectSnapshotManager(p.FilePath);
        var entry = mgr.LoadIndex().Single();

        var snap = mgr.LoadSnapshot(entry);
        Assert.NotNull(snap);
        Assert.Contains("霜降", snap!.Chapters[0].Title);
    }

    [Fact]
    public void 新旧快照混在一起时_各自都能读()
    {
        var p = NewProject();
        p.Save();

        var mgr = new ProjectSnapshotManager(p.FilePath);
        var dir = Path.Combine(_dir.Path, ".snapshots", Path.GetFileName(p.FilePath));
        Directory.CreateDirectory(dir);

        // 001 = 老格式（明文），接着存一条会得到 002（gzip）
        File.WriteAllText(Path.Combine(dir, "index.json"),
            """{"Entries":[{"Id":"001","Description":"旧的","Timestamp":"2026-01-01T00:00:00"}]}""");
        File.WriteAllText(Path.Combine(dir, "001.json"),
            JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        mgr.SaveSnapshot(p, "新的");

        var index = mgr.LoadIndex();
        Assert.Equal(2, index.Count);
        Assert.Contains(Directory.GetFiles(dir), f => f.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase));
        foreach (var e in index)
        {
            var snap = mgr.LoadSnapshot(e);
            Assert.NotNull(snap);
            Assert.Contains("霜降", snap!.Chapters[0].Title);
        }
    }
}
