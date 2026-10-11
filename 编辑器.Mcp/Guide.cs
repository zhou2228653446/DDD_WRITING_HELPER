using System.Reflection;
using System.Text;

namespace 编辑器.Mcp;

/// <summary>
/// Agent 手册（docs/AGENT_GUIDE.md）——"让 agent 自己知道怎么做"的那份东西。
///
/// ★ 为什么做成"嵌入资源 + 单一真源"，而不是把文字写死在代码里：
///   1. 篇幅长（工具地图/剧本/排障），写在 C# 字符串里没法读、没法 review、改一行要重新编译理解转义；
///   2. 它同时是给人看的文档，必须是仓库里一篇正常可读的 md；
///   3. 两处各存一份必然会漂移——改了文档忘了改代码，agent 拿到的就是旧规矩。
///   所以：md 是唯一真源，编进程序集，运行时读出来。
///
/// 只暴露两个出口：
///   Guide.Instructions → initialize 的 instructions 字段（客户端注入系统提示词，见 McpServer）
///   Guide.Text         → guide 工具 / tdx://guide 资源（完整手册）
///
/// ⚠ 发布版也会带上它（编进 dll），不依赖运行目录里有 md 文件——
///   dist 与 D:\TdxClaw\web 的制作过程不受影响。
/// </summary>
internal static class Guide
{
    /// <summary>csproj 里 LogicalName 指定的资源名。改名字要同时改这里和 csproj。</summary>
    private const string ResourceId = "editor.mcp.agent-guide.md";

    private const string BeginMark = "<!-- BEGIN:instructions -->";
    private const string EndMark = "<!-- END:instructions -->";

    /// <summary>完整手册。读不到资源时给出可操作的兜底，而不是空字符串
    /// （空 instructions 会让 agent 完全抓瞎，比给个降级提示糟得多）。</summary>
    public static string Text { get; } = Load();

    /// <summary>initialize 里返回的那段（比全文短，客户端的系统提示词放不下几千字）。
    /// 取手册里 BEGIN/END 标记之间的部分——**内容仍只有一份**，改手册即改 instructions。</summary>
    public static string Instructions { get; } = Extract(Text);

    private static string Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ResourceId);
            if (stream == null)
                return Fallback($"嵌入资源 {ResourceId} 不存在");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().Trim();
            return text.Length > 0 ? text : Fallback("嵌入资源是空的");
        }
        catch (Exception ex)
        {
            return Fallback(ex.Message);
        }
    }

    private static string Extract(string full)
    {
        var b = full.IndexOf(BeginMark, StringComparison.Ordinal);
        var e = full.IndexOf(EndMark, StringComparison.Ordinal);
        if (b < 0 || e < 0 || e <= b)
        {
            // 标记被误删也不能让 agent 没有说明：退化成全文前 1200 字
            return full.Length > 1200 ? full[..1200] : full;
        }
        return full[(b + BeginMark.Length)..e].Trim();
    }

    private static string Fallback(string why) =>
        "TdxClaw 写作助手 MCP 服务器：直接读写 .tdxproj 长篇写作项目（小说 / 学术论文 / 公文 / 通用写作），" +
        "并与界面端共享同一套设定集、结构化人物卡、记忆与文献库。\n" +
        "先调用 project_list 找书、project_open 打开、project_status 看进度；" +
        "写法与顺序见 guide 工具（手册文本这次没读出来：" + why + "）。";
}
