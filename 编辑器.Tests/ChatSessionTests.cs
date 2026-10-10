using System.IO;
using System.Linq;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 万能聊天的对话记忆落盘位置。
///
/// 抓的是这个 bug：会话文件曾写死在 &lt;项目目录&gt;/.chat/session.json ——
/// **按目录**而不是按书分。桌面端一个目录只放一本书时看不出来，
/// 网页版所有书稿都在同一个 books/ 目录下，等于全书共用一份对话记忆：
/// 换一本书，AI 还记得上一本书的情节。这类错误不会报错、只会"答得不对"，
/// 所以必须有断言钉住。
/// </summary>
public class ChatSessionTests : IDisposable
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

    private static void AddRound(ChatSessionStore s, string q) => s.Add(q, "回复：" + q);

    [Fact]
    public void 同一目录下的两本书各有各的对话记忆()
    {
        var a = MakeProject("甲书");
        var b = MakeProject("乙书");

        var sa = new ChatSessionStore(a);
        AddRound(sa, "甲书的问题");

        var sb = new ChatSessionStore(b);
        Assert.Equal(0, sb.RoundCount);              // 乙书不该看到甲书的对话

        AddRound(sb, "乙书的问题");
        Assert.Equal(1, sb.RoundCount);

        // 重新打开：各自读回各自的
        Assert.Contains("甲书的问题", string.Join("|",
            new ChatSessionStore(a).BuildHistory().Select(m => m.Content)));
        Assert.DoesNotContain("甲书的问题", string.Join("|",
            new ChatSessionStore(b).BuildHistory().Select(m => m.Content)));
    }

    [Fact]
    public void 对话记忆确实落盘且重新打开还在()
    {
        var a = MakeProject("落盘书");
        var s = new ChatSessionStore(a);
        AddRound(s, "第一问");
        AddRound(s, "第二问");

        var reopened = new ChatSessionStore(a);
        Assert.Equal(2, reopened.RoundCount);
        Assert.False(reopened.IsEmpty);
    }

    [Fact]
    public void 目录里只有一本书时会认领旧版session_json()
    {
        var a = MakeProject("孤本书");
        var chatDir = Path.Combine(_dir.Path, ".chat");
        Directory.CreateDirectory(chatDir);
        File.WriteAllText(Path.Combine(chatDir, "session.json"),
            """[{"Role":"user","Content":"旧对话"},{"Role":"assistant","Content":"旧回复"}]""");

        var s = new ChatSessionStore(a);

        Assert.Equal(1, s.RoundCount);
        Assert.Contains("旧对话", s.BuildHistory()[0].Content);
        // 迁移后旧文件名不该再留着，否则下次还会被别的书认领
        Assert.False(File.Exists(Path.Combine(chatDir, "session.json")));
    }

    [Fact]
    public void 目录里有多本书时不去猜旧session_json归谁()
    {
        MakeProject("甲书");
        var b = MakeProject("乙书");

        var chatDir = Path.Combine(_dir.Path, ".chat");
        Directory.CreateDirectory(chatDir);
        var legacy = Path.Combine(chatDir, "session.json");
        File.WriteAllText(legacy,
            """[{"Role":"user","Content":"不知谁的旧对话"},{"Role":"assistant","Content":"旧回复"}]""");

        var s = new ChatSessionStore(b);

        // 宁可不猜——认错等于把别人的对话灌进这本书
        Assert.Equal(0, s.RoundCount);
        Assert.True(File.Exists(legacy));   // 原样留着，用户还能自己救
    }

    [Fact]
    public void 清空之后重新打开仍然是空的()
    {
        var a = MakeProject("清空书");
        var s = new ChatSessionStore(a);
        AddRound(s, "会被清掉");

        s.Clear();

        Assert.Equal(0, new ChatSessionStore(a).RoundCount);
    }

    public void Dispose() => _dir.Dispose();
}
