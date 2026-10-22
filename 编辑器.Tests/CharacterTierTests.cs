using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using 编辑器.Mcp;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 人物分级（Scope）与状态（Status）在 agent 侧能不能直接写。
///
/// 之前只有 character_scope_set 能改，建卡时顺手标不了——agent 明明知道
/// "这是主角"，却得先建卡、再调一次批量工具才能标上常驻，中间还可能出错。
/// </summary>
public class CharacterTierTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _file;
    private readonly Session _session = new();
    private NovelProject _project = null!;

    public CharacterTierTests()
    {
        _file = _dir.File("book.tdxproj");
        var p = new NovelProject { ProjectName = "测试书", FilePath = _file };
        p.Chapters.Add(new Chapter { ChapterNumber = 1, Title = "第一章", Content = "甲" });
        p.Save();

        var open = _session.Open(_file);
        Assert.False(open.IsError, open.Text);
        Assert.True(_session.TryGet(out var loaded, out var err), err.Text);
        _project = loaded;
    }

    public void Dispose() => _dir.Dispose();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 建卡时可以顺手标分级()
    {
        var r = NovelTools.UpsertCharacter(_session, _project,
            Args("""{"name":"林寒","role":"主角","scope":"core"}"""));
        Assert.False(r.IsError, r.Text);

        var c = _project.Characters.First(x => x.Name == "林寒");
        Assert.Equal(CharacterScope.Core, c.Scope);
        Assert.Contains("常驻", r.Text);
    }

    [Fact]
    public void 中文也能写_状态一起标()
    {
        var r = NovelTools.UpsertCharacter(_session, _project,
            Args("""{"name":"沈无咎","scope":"客串","status":"阵亡"}"""));
        Assert.False(r.IsError, r.Text);

        var c = _project.Characters.First(x => x.Name == "沈无咎");
        Assert.Equal(CharacterScope.Guest, c.Scope);
        Assert.Equal(CharacterStatus.Deceased, c.Status);
    }

    [Fact]
    public void 分级写错了要当场报错_而且不能建出半张卡()
    {
        var before = _project.Characters.Count;

        var r = NovelTools.UpsertCharacter(_session, _project,
            Args("""{"name":"赵九","scope":"boss"}"""));

        Assert.True(r.IsError);
        Assert.Equal(before, _project.Characters.Count);
        Assert.DoesNotContain(_project.Characters, x => x.Name == "赵九");
    }

    [Fact]
    public void 没开角色分级时照存_但会提示它暂时不生效()
    {
        // 默认项目没启用长篇结构 → 分级只是存着
        var r = NovelTools.UpsertCharacter(_session, _project,
            Args("""{"name":"老陈","scope":"core"}"""));

        Assert.False(r.IsError, r.Text);
        Assert.Contains("没开角色分级", r.Text);
        Assert.Equal(CharacterScope.Core,
            _project.Characters.First(x => x.Name == "老陈").Scope);
    }

    [Fact]
    public void 不传分级时保持原样_不能顺手抹掉已有分级()
    {
        NovelTools.UpsertCharacter(_session, _project,
            Args("""{"name":"苏晚","scope":"core"}"""));

        // 只改别的字段
        var r = NovelTools.UpsertCharacter(_session, _project,
            Args("""{"name":"苏晚","personality":"沉默"}"""));

        Assert.False(r.IsError, r.Text);
        Assert.Equal(CharacterScope.Core,
            _project.Characters.First(x => x.Name == "苏晚").Scope);
    }
}
