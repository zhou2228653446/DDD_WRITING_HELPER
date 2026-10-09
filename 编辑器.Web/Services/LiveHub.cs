using Microsoft.AspNetCore.SignalR;

namespace 编辑器.Web;

/// <summary>
/// 实时通道。两件事会往这里推：
///   1) 人自己改了内容 → 广播给其它打开着的页面（比如你手机上那台）
///   2) **agent 通过 MCP 操作时 → 广播给网页**，让人看见 AI 在做什么、写到哪了
///
/// 第 2 条是"人机协同"的关键：它替代桌面版那个 McpLiveBridge——
/// 后者靠本机命名管道，只能在同一个 Windows 桌面上用，远程场景下完全失效。
/// </summary>
public sealed class LiveHub : Hub
{
}
