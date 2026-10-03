using System.Text;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Mcp;

// ==================================================================
// MCP 协议的数据形状（手写，不依赖官方 SDK —— 本机 NuGet 直连不通，
// 而 MCP 本身只是 JSON-RPC 2.0 + stdio，协议面很窄）
// ==================================================================

// ⚠ 这三个类**必须用属性而不是字段**：System.Text.Json 默认只序列化公共属性，
// 写成字段的话 tools/list 会返回一堆「{}」，客户端拿到一堆没有名字的工具。
internal sealed class ToolDef
{
    public string name { get; set; } = "";
    public string description { get; set; } = "";
    public ToolSchema inputSchema { get; set; } = new();
}

internal sealed class ToolSchema
{
    public string type { get; set; } = "object";
    public Dictionary<string, ToolProp> properties { get; set; } = new();
    public List<string>? required { get; set; } = null;
}

internal sealed class ToolProp
{
    public string type { get; set; } = "string";
    public string description { get; set; } = "";
}

internal static class McpServer
{
    private static readonly Session _session = new();

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = null,   // 字段名就是 JSON 名（上面刻意都用小写）
        WriteIndented = false,
    };

    /// <summary>
    /// stdio 主循环。★ 任何日志都必须走 stderr —— stdout 是协议通道，写脏一个字节客户端就解析失败。
    /// </summary>
    /// <param name="input">可注入输入（自检用）。默认走真实 stdin。</param>
    /// <param name="output">可注入输出（自检用）。默认走真实 stdout。</param>
    public static async Task RunAsync(TextReader? input = null, TextWriter? output = null)
    {
        // ⚠ 这里刻意用注入的 reader/writer 而不是 OpenStandardInput()：
        // OpenStandardInput 拿的是**真实** stdin 句柄，Console.SetIn 影响不到它，
        // 自检时注入的假流会被无视，直接卡在等真实输入上。
        var reader = input ?? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var writer = output ?? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n",
        };

        Log("MCP 服务已启动（stdio）");

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (Exception ex)
            {
                await SendAsync(writer, RpcError(null, -32700, $"JSON 解析失败：{ex.Message}"));
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var mEl))
                {
                    await SendAsync(writer, RpcError(null, -32600, "缺少 method 字段"));
                    continue;
                }

                var method = mEl.GetString() ?? "";
                var hasId = root.TryGetProperty("id", out var idEl);
                var parameters = root.TryGetProperty("params", out var pEl) ? pEl : default;

                // 通知（notifications/*）没有 id，按协议不回复
                if (!hasId)
                {
                    Log($"← 通知 {method}");
                    continue;
                }

                Log($"← {method}");
                object response;
                try
                {
                    response = await DispatchAsync(method, parameters, idEl);
                }
                catch (Exception ex)
                {
                    Log($"处理 {method} 异常：{ex}");
                    response = RpcError(idEl, -32603, $"内部错误：{ex.Message}");
                }

                await SendAsync(writer, response);
            }
        }

        Log("stdin 关闭，退出");
    }

    private static async Task SendAsync(TextWriter writer, object response)
    {
        var json = JsonSerializer.Serialize(response, _json);
        await writer.WriteAsync(json + "\n");
        await writer.FlushAsync();
    }

    private static async Task<object> DispatchAsync(string method, JsonElement parameters, JsonElement id)
    {
        switch (method)
        {
            case "initialize":
                return RpcOk(id, new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new { listChanged = false } },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "TdxClaw 写作助手",
                        ["version"] = "1.0.0",
                    },
                });

            case "ping":
                return RpcOk(id, new Dictionary<string, object?>());

            case "tools/list":
                return RpcOk(id, new Dictionary<string, object?> { ["tools"] = ToolList() });

            case "tools/call":
                return RpcOk(id, await CallToolAsync(parameters));

            case "resources/list":
                return RpcOk(id, new Dictionary<string, object?> { ["resources"] = Array.Empty<object>() });

            case "prompts/list":
                return RpcOk(id, new Dictionary<string, object?> { ["prompts"] = Array.Empty<object>() });

            default:
                return RpcError(id, -32601, $"不支持的方法：{method}");
        }
    }

    // ==================================================================
    // 工具清单
    // ==================================================================

    private static List<ToolDef> ToolList() => new()
    {
        new ToolDef
        {
            name = "project_list",
            description = "查找 .tdxproj 项目文件。不给 directory 就扫描当前目录/文档/桌面。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["directory"] = new() { description = "要扫描的目录；留空则扫常见位置" },
                },
            },
        },
        new ToolDef
        {
            name = "project_open",
            description = "打开一个项目文件，之后所有读写都针对它。返回项目概览（章数/字数/已填设定）。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["path"] = new() { description = ".tdxproj 文件的完整路径" },
                },
                required = new List<string> { "path" },
            },
        },
        new ToolDef
        {
            name = "chapters_list",
            description = "列出当前项目所有章节（章号/标题/字数/梗概）。",
        },
        new ToolDef
        {
            name = "chapter_read",
            description = "读某一章正文。长章可用 maxChars 截断。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["number"] = new() { type = "integer", description = "章号" },
                    ["maxChars"] = new() { type = "integer", description = "最多读多少字，0 表示不限制" },
                },
                required = new List<string> { "number" },
            },
        },
        new ToolDef
        {
            name = "chapter_search",
            description = "全文搜索关键词，返回命中章节与上下文片段。找伏笔/设定是否前后矛盾时用它。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["keyword"] = new() { description = "要搜的词" },
                    ["contextChars"] = new() { type = "integer", description = "命中位置前后各取多少字，默认 80" },
                },
                required = new List<string> { "keyword" },
            },
        },
        new ToolDef
        {
            name = "settings_get",
            description = "读五项贯穿设定（大纲/章节大纲/人物/背景/文风）+ 叙事视角 + 作品简介。",
        },
        new ToolDef
        {
            name = "settings_book_get",
            description = "读设定集。不带 key 返回 12 章清单（含 key），带 key 返回该章全文。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["key"] = new() { description = "章的 SourceKey（如 characters / world / foreshadow），也可给标题片段" },
                },
            },
        },
        new ToolDef
        {
            name = "characters_stats",
            description = "角色出场统计：每人出场章次、总次数、连续缺席多少章。纯本地扫描，不花 token。",
        },
        new ToolDef
        {
            name = "chapter_write",
            description = "写章节正文。mode=replace 覆盖（会丢原内容，慎用），mode=append 追加。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["number"] = new() { type = "integer", description = "章号" },
                    ["content"] = new() { description = "正文内容" },
                    ["mode"] = new() { description = "replace（默认，覆盖）或 append（追加）" },
                },
                required = new List<string> { "number", "content" },
            },
        },
        new ToolDef
        {
            name = "chapter_create",
            description = "新建一章（章号自动取最大值+1）。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["title"] = new() { description = "章节标题" },
                    ["content"] = new() { description = "初始正文，可留空" },
                },
                required = new List<string> { "title" },
            },
        },
        new ToolDef
        {
            name = "settings_set",
            description = "改写贯穿设定某一项。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["field"] = new() { description = "full_outline / chapter_outline / characters / background / writing_style / narrative_viewpoint / description" },
                    ["text"] = new() { description = "新内容" },
                },
                required = new List<string> { "field", "text" },
            },
        },
        new ToolDef
        {
            name = "settings_book_set",
            description = "改写设定集某一章（按 key 或标题匹配）。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["key"] = new() { description = "章的 SourceKey 或标题片段" },
                    ["content"] = new() { description = "新内容" },
                    ["mode"] = new() { description = "replace（默认）或 append" },
                },
                required = new List<string> { "key", "content" },
            },
        },
        new ToolDef
        {
            name = "project_export",
            description = "导出整本书：docx / pdf / txt。论文版式传 paperMode=true。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["format"] = new() { description = "docx / pdf / txt，默认 docx" },
                    ["output"] = new() { description = "输出文件完整路径" },
                    ["paperMode"] = new() { type = "boolean", description = "是否按论文版式（首页连排摘要+数字编号章节+GB/T 7714 文献）" },
                },
                required = new List<string> { "output" },
            },
        },
        new ToolDef
        {
            name = "ai_write",
            description =
                "调用本软件配好的 AI 写作（与界面里点按钮完全同源：当前提示词方案 + 设定集 + " +
                "叙事视角约束）。task 可选 continue（续写）/ polish（润色）/ expand（扩写）/ " +
                "review（一致性审稿）/ setting_book（补设定集）/ name（起名）/ chat（自由问答）。" +
                "★ 只返回文本，不自动改稿；要落盘请再用 chapter_write。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["task"] = new() { description = "continue / polish / expand / review / setting_book / name / chat" },
                    ["number"] = new() { type = "integer", description = "针对哪一章（续写/润色/审稿必填）" },
                    ["instruction"] = new() { description = "对本次生成的具体要求" },
                    ["maxTokens"] = new() { type = "integer", description = "输出上限，0 表示按任务自动给" },
                },
                required = new List<string> { "task" },
            },
        },
    };

    // ==================================================================
    // 工具分派
    // ==================================================================

    private static async Task<object> CallToolAsync(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var nEl))
            return ToolText("缺少工具名 name。", true);

        var name = nEl.GetString() ?? "";
        var args = parameters.TryGetProperty("arguments", out var aEl) ? aEl : default;

        try
        {
            ToolResult r;

            // 不需要项目的工具（只调一次——Open 有副作用，重复调用会白白刷新文件指纹）
            switch (name)
            {
                case "project_list":
                {
                    var r0 = NovelTools.ListProjects(args);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "project_open":
                {
                    var r0 = _session.Open(NovelTools.Str(args, "path"));
                    return ToolText(r0.Text, r0.IsError);
                }
            }

            // 需要项目的工具
            if (!_session.TryGet(out var p, out var err))
                return ToolText(err.Text, true);

            switch (name)
            {
                case "chapters_list": r = NovelTools.ListChapters(p); break;
                case "chapter_read": r = NovelTools.ReadChapter(p, args); break;
                case "chapter_search": r = NovelTools.SearchChapters(p, args); break;
                case "settings_get": r = NovelTools.GetSettings(p); break;
                case "settings_book_get": r = NovelTools.GetSettingsBook(p, args); break;
                case "characters_stats": r = NovelTools.CharacterStats(p); break;

                case "chapter_write": r = NovelTools.WriteChapter(_session, p, args); break;
                case "chapter_create": r = NovelTools.CreateChapter(_session, p, args); break;
                case "settings_set": r = NovelTools.SetSetting(_session, p, args); break;
                case "settings_book_set": r = NovelTools.SetSettingsBook(_session, p, args); break;

                case "project_export": r = NovelTools.Export(p, args); break;

                case "ai_write": r = await AiTools.WriteAsync(p, args); break;

                default:
                    return ToolText($"未知工具「{name}」。用 tools/list 看有哪些工具。", true);
            }

            return ToolText(r.Text, r.IsError);
        }
        catch (Exception ex)
        {
            Log($"工具 {name} 抛异常：{ex}");
            return ToolText($"工具执行出错：{ex.Message}", true);
        }
    }

    private static object ToolText(string text, bool isError) =>
        new Dictionary<string, object?>
        {
            ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError,
        };

    private static object RpcOk(JsonElement id, object result) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : id.Clone(),
            ["result"] = result,
        };

    private static object RpcError(JsonElement? id, int code, string message)
    {
        var dict = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        };
        if (id != null && id.Value.ValueKind != JsonValueKind.Undefined)
            dict["id"] = id.Value.Clone();
        else
            dict["id"] = null;
        return dict;
    }

    private static void Log(string msg)
    {
        try { Console.Error.WriteLine($"[mcp] {msg}"); } catch { /*  stderr 不可写也不能崩  */ }
    }
}
