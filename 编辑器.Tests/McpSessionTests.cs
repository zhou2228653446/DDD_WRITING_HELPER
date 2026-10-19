using System;
using System.IO;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// MCP 会话隔离：一个 agent 一个会话。
///
/// 这一组钉死的是 2026-10-19 之前的老毛病——`McpServer` 只有一个 `static Session`，
/// 服务进程里"当前打开的书"是全局唯一的。两个 agent 同时接入时：
///   A 打开《甲》→ B 打开《乙》→ A 的 chapter_write 落进《乙》。
/// 不报错，不崩溃，只是**写错书**。所以这里既要测"两个会话互不相干"，
/// 也要测"闲置能被回收"（每个会话常驻一份整书对象）。
/// </summary>
public class McpSessionTests
{
    private static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void 不同令牌拿到不同会话_同令牌拿到同一个()
    {
        var k1 = NewKey();
        var k2 = NewKey();

        var a = 编辑器.Mcp.McpServer.SessionFor(k1);
        var b = 编辑器.Mcp.McpServer.SessionFor(k2);

        Assert.NotSame(a, b);
        Assert.Same(a, 编辑器.Mcp.McpServer.SessionFor(k1));
    }

    [Fact]
    public void 没有令牌时落到同一个默认会话()
    {
        // stdio 模式（一个进程一个 agent）与"本机免登录的 curl"走这里
        Assert.Same(
            编辑器.Mcp.McpServer.SessionFor(null),
            编辑器.Mcp.McpServer.SessionFor(""));
    }

    [Fact]
    public void 两个会话可以各开各的书()
    {
        using var dir = new TempDir();
        var pathA = dir.File("甲.tdxproj");
        var pathB = dir.File("乙.tdxproj");

        new NovelProject { ProjectName = "甲", FilePath = pathA }.Save();
        new NovelProject { ProjectName = "乙", FilePath = pathB }.Save();

        var a = 编辑器.Mcp.McpServer.SessionFor(NewKey());
        var b = 编辑器.Mcp.McpServer.SessionFor(NewKey());

        Assert.False(a.Open(pathA).IsError);
        Assert.False(b.Open(pathB).IsError);

        // 关键断言：B 打开乙之后，A 仍然握着甲
        Assert.Equal("甲", a.Project!.ProjectName);
        Assert.Equal("乙", b.Project!.ProjectName);
        Assert.NotEqual(a.Path, b.Path);
    }

    [Fact]
    public void 闲置会话会被回收()
    {
        var key = NewKey();
        var s = 编辑器.Mcp.McpServer.SessionFor(key);
        s.LastUsedUtc = DateTime.UtcNow.AddHours(-2);

        // 回收阈值给 1 分钟：刚刚还在用的会话（LastUsedUtc=现在）不会被误伤
        var removed = 编辑器.Mcp.McpServer.SweepIdleSessions(TimeSpan.FromMinutes(1));

        Assert.True(removed >= 1);
        Assert.NotSame(s, 编辑器.Mcp.McpServer.SessionFor(key));   // 再取是新的一份
    }
}
