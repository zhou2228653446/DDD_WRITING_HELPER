using System.Linq;

namespace 编辑器.Tests;

/// <summary>
/// 章节顺序：移动与重排。
///
/// 章号是唯一定位依据（chapter_read / chapter_write / 资源 URI），
/// 顺序一旦算错，表现是"工具说成功了、实际没动"——静默失败，最难查。
/// 这层已经有单元测试抓出过一个 bug（Renumber 按章号排序导致移动失效），
/// 所以移动逻辑收敛到 NovelProject 后继续钉住。
/// </summary>
public class ChapterOrderTests
{
    private static NovelProject ThreeChapters()
    {
        var p = new NovelProject { ProjectName = "t" };
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "甲" });
        p.Chapters.Add(new Chapter { ChapterNumber = 2, Title = "乙" });
        p.Chapters.Add(new Chapter { ChapterNumber = 3, Title = "丙" });
        return p;
    }

    private static string[] Titles(NovelProject p) =>
        p.Chapters.OrderBy(c => c.ChapterNumber).Select(c => c.Title).ToArray();

    [Fact]
    public void 上移_换到前一位()
    {
        var p = ThreeChapters();
        var b = p.Chapters.First(c => c.Title == "乙");

        Assert.True(p.MoveChapter(b, -1));
        Assert.Equal(new[] { "乙", "甲", "丙" }, Titles(p));
    }

    [Fact]
    public void 下移_换到后一位()
    {
        var p = ThreeChapters();
        var b = p.Chapters.First(c => c.Title == "乙");

        Assert.True(p.MoveChapter(b, +1));
        Assert.Equal(new[] { "甲", "丙", "乙" }, Titles(p));
    }

    [Fact]
    public void 移动后_章号连续且跟着走()
    {
        var p = ThreeChapters();
        var c = p.Chapters.First(x => x.Title == "丙");

        p.MoveChapter(c, -2);   // 丙移到最前

        Assert.Equal(new[] { "丙", "甲", "乙" }, Titles(p));
        Assert.Equal(1, c.ChapterNumber);
        Assert.Equal(new[] { 1, 2, 3 }, p.Chapters.OrderBy(x => x.ChapterNumber).Select(x => x.ChapterNumber));
    }

    [Fact]
    public void 已在最前_上移返回false且不动()
    {
        var p = ThreeChapters();
        var a = p.Chapters.First(c => c.Title == "甲");

        Assert.False(p.MoveChapter(a, -1));
        Assert.Equal(new[] { "甲", "乙", "丙" }, Titles(p));
    }

    [Fact]
    public void 已在最后_下移返回false且不动()
    {
        var p = ThreeChapters();
        var c = p.Chapters.First(x => x.Title == "丙");

        Assert.False(p.MoveChapter(c, +1));
        Assert.Equal(new[] { "甲", "乙", "丙" }, Titles(p));
    }

    [Fact]
    public void 不属于本项目_返回false()
    {
        var p = ThreeChapters();
        var outsider = new Chapter { ChapterNumber = 9, Title = "外人" };

        Assert.False(p.MoveChapter(outsider, -1));
    }

    [Fact]
    public void 删除后重排_章号不留空洞()
    {
        var p = ThreeChapters();
        p.Chapters.Remove(p.Chapters.First(c => c.Title == "乙"));
        p.RenumberChapters();

        Assert.Equal(new[] { "甲", "丙" }, Titles(p));
        Assert.Equal(new[] { 1, 2 }, p.Chapters.OrderBy(c => c.ChapterNumber).Select(c => c.ChapterNumber));
    }

    [Fact]
    public void 重排按列表顺序_不按旧章号()
    {
        // 这条专门盯上次那个 bug：列表已换成新顺序、章号还是旧值时，
        // 若按 ChapterNumber 排序再编号，等于把顺序还原回去，移动就白做了
        var p = ThreeChapters();
        p.Chapters = p.Chapters.OrderByDescending(c => c.ChapterNumber).ToList();
        p.RenumberChapters();

        Assert.Equal(new[] { "丙", "乙", "甲" }, Titles(p));
    }
}
