using System;
using System.IO;
using System.Linq;
using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 作品删除。<see cref="BookDeleter"/> 删的是用户几年的稿子，规则必须钉死：
/// ① 永不硬删（整本书进 .trash，且 .trash 必须在书库**外**——在里面会被
///    MCP 的 project_list 递归扫到，删了还列出来，像没删成功）；
/// ② 该带走的全带走（书 / .bak / 对话记忆 / 快照），不该动的一点不碰
///    （导出文件、全库共用的 .ai_memory）；
/// ③ 路径穿越一律拒绝——一个 "../" 就能把书库外的文件搬走。
/// </summary>
public class BookDeleterTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _books;

    public BookDeleterTests()
    {
        // 书库放在临时根的下一层：.trash 预期出现在临时根（书库的父目录）
        _books = Path.Combine(_root.Path, "books");
        Directory.CreateDirectory(_books);
    }

    public void Dispose() => _root.Dispose();

    /// <summary>造一本"全套"的书：书 + .bak + 对话记忆 + 快照 + 导出 + 全库共用记忆。</summary>
    private void MakeBook(string name, string content = "正文")
    {
        File.WriteAllText(Path.Combine(_books, name + ".tdxproj"), content);
        File.WriteAllText(Path.Combine(_books, name + ".tdxproj.bak"), content + "-bak");
        Directory.CreateDirectory(Path.Combine(_books, ".chat"));
        File.WriteAllText(Path.Combine(_books, ".chat", name + ".tdxproj.session.json"), "[]");
        Directory.CreateDirectory(Path.Combine(_books, ".snapshots", name + ".tdxproj"));
        File.WriteAllText(Path.Combine(_books, ".snapshots", name + ".tdxproj", "001.json"), "{}");
        File.WriteAllText(Path.Combine(_books, name + ".txt"), content);          // 导出（不该删）
        File.WriteAllText(Path.Combine(_books, name + ".docx"), content);         // 导出（不该删）
        Directory.CreateDirectory(Path.Combine(_books, ".ai_memory"));
        File.WriteAllText(Path.Combine(_books, ".ai_memory", "memory.md"), "共用"); // 全库共用（不该删）
    }

    [Fact]
    public void 删除把整本书移入回收站且书库干净了()
    {
        MakeBook("甲");

        var r = BookDeleter.Delete(_books, "甲.tdxproj");

        Assert.True(r.Ok, r.Message);
        // 书库里的书相关文件全部消失
        Assert.False(File.Exists(Path.Combine(_books, "甲.tdxproj")));
        Assert.False(File.Exists(Path.Combine(_books, "甲.tdxproj.bak")));
        Assert.False(File.Exists(Path.Combine(_books, ".chat", "甲.tdxproj.session.json")));
        Assert.False(Directory.Exists(Path.Combine(_books, ".snapshots", "甲.tdxproj")));
        // 回收站里一应俱全，且保持了相对路径（照着能搬回来）
        Assert.True(File.Exists(Path.Combine(r.TrashDir!, "甲.tdxproj")));
        Assert.True(File.Exists(Path.Combine(r.TrashDir!, "甲.tdxproj.bak")));
        Assert.True(File.Exists(Path.Combine(r.TrashDir!, ".chat", "甲.tdxproj.session.json")));
        Assert.True(File.Exists(Path.Combine(r.TrashDir!, ".snapshots", "甲.tdxproj", "001.json")));
        // 还附了恢复说明
        Assert.True(File.Exists(Path.Combine(r.TrashDir!, "_删除信息.txt")));
        Assert.Contains("甲.tdxproj", r.Moved);
    }

    [Fact]
    public void 回收站在书库外面_否则project_list递归还会列出删掉的书()
    {
        MakeBook("甲");

        var r = BookDeleter.Delete(_books, "甲.tdxproj");

        Assert.True(r.Ok);
        // 书库内一个 .trash 都不该有（MCP 的 project_list 会递归扫 *.tdxproj）
        Assert.Empty(Directory.GetDirectories(_books, ".trash"));
        // 它在书库的父目录
        var expected = Path.GetFullPath(Path.Combine(_books, "..", ".trash"));
        Assert.StartsWith(expected, Path.GetFullPath(r.TrashDir!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 导出文件不随书删_只在报告里说明()
    {
        MakeBook("甲");

        var r = BookDeleter.Delete(_books, "甲.tdxproj");

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(_books, "甲.txt")));
        Assert.True(File.Exists(Path.Combine(_books, "甲.docx")));
        Assert.Equal(2, r.LeftoverExports);
        Assert.Contains("2 个同名导出文件", r.Message);
    }

    [Fact]
    public void 全库共用的ai记忆不动()
    {
        MakeBook("甲");

        var r = BookDeleter.Delete(_books, "甲.tdxproj");

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(_books, ".ai_memory", "memory.md")));
        Assert.False(Directory.Exists(Path.Combine(r.TrashDir!, ".ai_memory")));
    }

    [Fact]
    public void 路径穿越一律拒绝()
    {
        MakeBook("甲");

        // 各种想搬走书库外文件的花样，全部挡掉
        foreach (var evil in new[]
        {
            "../evil.tdxproj",
            "..\\evil.tdxproj",
            Path.GetFullPath(Path.Combine(_books, "..", "evil.tdxproj")),
            "sub/dir/甲.tdxproj",
            "..",
        })
        {
            var r = BookDeleter.Delete(_books, evil);
            Assert.False(r.Ok, $"「{evil}」本该被拒绝");
        }
        // 被拒绝时什么都没动
        Assert.True(File.Exists(Path.Combine(_books, "甲.tdxproj")));
    }

    [Fact]
    public void 书不存在时报错且不碰回收站()
    {
        var r = BookDeleter.Delete(_books, "没有这本书.tdxproj");

        Assert.False(r.Ok);
        Assert.Contains("没有这本书", r.Message);
        Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(_books, "..", ".trash"))));
    }

    [Fact]
    public void 正打开着的书拒绝删()
    {
        MakeBook("甲");
        var target = Path.Combine(_books, "甲.tdxproj");

        var r = BookDeleter.Delete(_books, "甲.tdxproj", p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));

        Assert.False(r.Ok);
        Assert.Contains("正打开着", r.Message);
        // 一本书都没少
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void 删另一本时正打开的书不受影响()
    {
        MakeBook("甲");
        MakeBook("乙");
        var open = Path.Combine(_books, "乙.tdxproj");

        var r = BookDeleter.Delete(_books, "甲.tdxproj", p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(open), StringComparison.OrdinalIgnoreCase));

        Assert.True(r.Ok, r.Message);
        Assert.False(File.Exists(Path.Combine(_books, "甲.tdxproj")));
        Assert.True(File.Exists(open));
    }

    [Fact]
    public void 没有快照和对话记忆的书也能删()
    {
        // 新建的书往往什么都没有，只有 .tdxproj 本身
        File.WriteAllText(Path.Combine(_books, "新书.tdxproj"), "x");

        var r = BookDeleter.Delete(_books, "新书.tdxproj");

        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(r.TrashDir!, "新书.tdxproj")));
        Assert.Single(r.Moved);
    }

    [Fact]
    public void 两次删除互不覆盖_时间戳分开()
    {
        File.WriteAllText(Path.Combine(_books, "a.tdxproj"), "1");
        File.WriteAllText(Path.Combine(_books, "b.tdxproj"), "2");

        var r1 = BookDeleter.Delete(_books, "a.tdxproj");
        var r2 = BookDeleter.Delete(_books, "b.tdxproj");

        Assert.True(r1.Ok);
        Assert.True(r2.Ok);
        Assert.NotEqual(r1.TrashDir, r2.TrashDir);
        Assert.True(File.Exists(Path.Combine(r1.TrashDir!, "a.tdxproj")));
        Assert.True(File.Exists(Path.Combine(r2.TrashDir!, "b.tdxproj")));
    }
}
