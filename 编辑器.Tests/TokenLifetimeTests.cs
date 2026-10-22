using System;
using System.Linq;
using 编辑器.Web.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 令牌的有效期策略（2026-10-22 定）。
///
/// 规则：
///   · MCP 令牌：签发后 <b>1 小时</b>内没人用就自然过期；agent 一旦接上来
///     （哪怕只发了一次 initialize），就变成 <b>24 小时滑动</b>有效期，
///     之后每来一次请求续 24 小时 —— 也就是"被触发过之后，空闲满一天才失效"。
///   · 试用登录态：45 分钟（试用名额的语义，不能变）。
///   · 自用网页登录态：30 天（跟 Cookie 的 Max-Age 对齐；以前是被 45 分钟
///     无差别清扫扫掉，用户表现为"莫名其妙又要求登录"）。
/// </summary>
public class TokenLifetimeTests
{
    private static bool Near(DateTime actual, DateTime expected, double toleranceHours = 0.02)
        => Math.Abs((actual - expected).TotalHours) <= toleranceHours;

    [Fact]
    public void MCP令牌_签发后只有一小时()
    {
        var sessions = new AuthSessions();
        var token = sessions.Issue("self", AuthSessions.TokenKind.Mcp);

        var exp = sessions.ExpiresAtOf(token);
        Assert.NotNull(exp);
        Assert.True(Near(exp!.Value, DateTime.Now.AddHours(1)), $"到期时间 {exp} 不是 1 小时后");
    }

    [Fact]
    public void MCP令牌_被用过一次就变成二十四小时()
    {
        var sessions = new AuthSessions();
        var token = sessions.Issue("self", AuthSessions.TokenKind.Mcp);

        Assert.Equal("self", sessions.SubjectOf(token));

        var exp = sessions.ExpiresAtOf(token);
        Assert.NotNull(exp);
        Assert.True(Near(exp!.Value, DateTime.Now.AddHours(24)), $"到期时间 {exp} 不是 24 小时后");
    }

    [Fact]
    public void MCP令牌_持续接入就持续续期()
    {
        var sessions = new AuthSessions();
        var token = sessions.Issue("self", AuthSessions.TokenKind.Mcp);

        sessions.SubjectOf(token);
        // 再用一次，滚动基准应随之推后
        var first = sessions.ExpiresAtOf(token)!.Value;
        System.Threading.Thread.Sleep(1100);
        sessions.SubjectOf(token);
        var second = sessions.ExpiresAtOf(token)!.Value;

        Assert.True(second > first, $"第二次使用后到期时间没有推后：{first} → {second}");
    }

    [Fact]
    public void MCP令牌_过期后就不再认()
    {
        var sessions = new AuthSessions();
        var token = sessions.Issue("self", AuthSessions.TokenKind.Mcp);

        sessions.ForceExpire(token, DateTime.Now.AddMinutes(-1));

        Assert.Null(sessions.SubjectOf(token));
    }

    [Fact]
    public void 试用登录态_仍然四十五分钟()
    {
        var sessions = new AuthSessions();
        var token = sessions.Issue("trial:1234");

        var exp = sessions.ExpiresAtOf(token);
        Assert.NotNull(exp);
        Assert.True(Near(exp!.Value, DateTime.Now.AddMinutes(45), 0.01),
            $"到期时间 {exp} 不是 45 分钟后");
    }

    [Fact]
    public void 自用登录态_三十天_不再被四十五分钟扫掉()
    {
        var sessions = new AuthSessions();
        var token = sessions.Issue("self");

        var exp = sessions.ExpiresAtOf(token);
        Assert.NotNull(exp);
        Assert.True(Near(exp!.Value, DateTime.Now.AddDays(30), 0.02),
            $"到期时间 {exp} 不是 30 天后");
    }

    [Fact]
    public void 撤销令牌会通知回收对应会话()
    {
        var sessions = new AuthSessions();
        var revoked = new System.Collections.Generic.List<string>();
        sessions.OnRevoked = revoked.Add;

        var token = sessions.Issue("self", AuthSessions.TokenKind.Mcp);
        sessions.Revoke(token);
        Assert.Contains(token, revoked);

        // 过期也要通知
        var token2 = sessions.Issue("self", AuthSessions.TokenKind.Mcp);
        sessions.ForceExpire(token2, DateTime.Now.AddMinutes(-1));
        sessions.SubjectOf(token2);
        Assert.Contains(token2, revoked);
    }
}
