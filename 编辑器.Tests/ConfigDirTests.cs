using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 配置目录 / 书库目录的解析。
///
/// 这一组是为了钉死 2026-10-18 那个事故：SYSTEM 开机任务把服务拉起来后，
/// %APPDATA% 指向 systemprofile，`TDXCLAW_CONFIG_DIR` 虽然被钉住了，
/// 但解析书库时**只看 %APPDATA%**，于是读不到 paths.json，退回兜底路径；
/// 而 SYSTEM 下 MyDocuments 是空串，兜底拼出相对路径 "TdxClaw\Projects"，
/// 落进发布目录 —— 网页上「一本书都没有」，真正的书库其实完好。
/// </summary>
public class ConfigDirTests
{
    // 环境变量是进程级的，xUnit 默认并行跑测试类；这组会和别的用例抢变量，
    // 所以统一串行化。改成并行后这里仍然是唯一会写 TDXCLAW_CONFIG_DIR 的地方。
    private static readonly object Gate = new();

    [Fact]
    public void 配置目录被钉住时书库也跟着走()
    {
        using var cfg = new TempDir();
        using var books = new TempDir();
        File.WriteAllText(cfg.File("paths.json"),
            JsonSerializer.Serialize(new { ProjectsDirectory = books.Path }));

        lock (Gate)
        {
            var (oldCfg, oldProj) = Snapshot();
            try
            {
                Environment.SetEnvironmentVariable("TDXCLAW_PROJECTS_DIR", null);
                Environment.SetEnvironmentVariable("TDXCLAW_CONFIG_DIR", cfg.Path);

                var resolved = 编辑器.Mcp.NovelTools.ResolveProjectsDirectory();
                Assert.Equal(
                    Path.GetFullPath(books.Path).TrimEnd('\\'),
                    Path.GetFullPath(resolved).TrimEnd('\\'));
            }
            finally { Restore(oldCfg, oldProj); }
        }
    }

    [Fact]
    public void 显式的书库环境变量优先级最高()
    {
        using var cfg = new TempDir();
        using var books = new TempDir();
        using var overrideDir = new TempDir();
        File.WriteAllText(cfg.File("paths.json"),
            JsonSerializer.Serialize(new { ProjectsDirectory = books.Path }));

        lock (Gate)
        {
            var (oldCfg, oldProj) = Snapshot();
            try
            {
                Environment.SetEnvironmentVariable("TDXCLAW_CONFIG_DIR", cfg.Path);
                Environment.SetEnvironmentVariable("TDXCLAW_PROJECTS_DIR", overrideDir.Path);

                Assert.Equal(
                    Path.GetFullPath(overrideDir.Path).TrimEnd('\\'),
                    Path.GetFullPath(编辑器.Mcp.NovelTools.ResolveProjectsDirectory()).TrimEnd('\\'));
            }
            finally { Restore(oldCfg, oldProj); }
        }
    }

    [Fact]
    public void 兜底路径在_MyDocuments_为空时仍然绝对()
    {
        // SYSTEM 账户下 MyDocuments 是空串，UserProfile 也拿不到 → 必须退到程序目录。
        // 只要这里拼出相对路径，服务就会在自己的工作目录里凭空造一个空书库。
        var byDocs = 编辑器.Mcp.NovelTools.FallbackProjectsDir(@"C:\Users\me\Documents", @"C:\Users\me", @"C:\app");
        Assert.Equal(@"C:\Users\me\Documents\TdxClaw\Projects", byDocs);

        var byProfile = 编辑器.Mcp.NovelTools.FallbackProjectsDir("", @"C:\Windows\system32\config\systemprofile", @"C:\app");
        Assert.Equal(@"C:\Windows\system32\config\systemprofile\TdxClaw\Projects", byProfile);

        var byBase = 编辑器.Mcp.NovelTools.FallbackProjectsDir(null, null, @"D:\TdxClaw\web");
        Assert.Equal(@"D:\TdxClaw\web\TdxClaw\Projects", byBase);
        Assert.True(Path.IsPathRooted(byBase));
    }

    [Fact]
    public void 实际解析出来的永远是绝对路径()
    {
        using var cfg = new TempDir();
        lock (Gate)
        {
            var (oldCfg, oldProj) = Snapshot();
            try
            {
                Environment.SetEnvironmentVariable("TDXCLAW_PROJECTS_DIR", null);
                Environment.SetEnvironmentVariable("TDXCLAW_CONFIG_DIR", cfg.Path);

                var resolved = 编辑器.Mcp.NovelTools.ResolveProjectsDirectory();
                Assert.True(Path.IsPathRooted(resolved), $"解析结果是相对路径：{resolved}");
            }
            finally { Restore(oldCfg, oldProj); }
        }
    }

    private static (string?, string?) Snapshot() => (
        Environment.GetEnvironmentVariable("TDXCLAW_CONFIG_DIR"),
        Environment.GetEnvironmentVariable("TDXCLAW_PROJECTS_DIR"));

    private static void Restore(string? cfg, string? projects)
    {
        Environment.SetEnvironmentVariable("TDXCLAW_CONFIG_DIR", cfg);
        Environment.SetEnvironmentVariable("TDXCLAW_PROJECTS_DIR", projects);
    }
}
