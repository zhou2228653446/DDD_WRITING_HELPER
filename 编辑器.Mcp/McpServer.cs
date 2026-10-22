using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    // ⚠ 无必填参数时必须省略 required 字段，不能序列化成 "required": null，
    // 否则严格遵循 JSON Schema 的客户端（如 Antigravity / Gemini）会报 Schema 校验错误。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? required { get; set; } = null;
}

internal sealed class ToolProp
{
    public string type { get; set; } = "string";
    public string description { get; set; } = "";
}

internal static class McpServer
{
    /// <summary>
    /// stdio 模式的默认会话。一个进程就是一个 agent，不存在"选哪本"的歧义。
    /// HTTP 模式（网页 /mcp）不用它，见 <see cref="_agentSessions"/>。
    /// </summary>
    private static readonly Session _defaultSession = new();

    /// <summary>
    /// HTTP 模式下按 **agent** 分的会话表，key = 调用方带来的令牌。
    ///
    /// ★ 为什么必须分：以前这里是 `static Session session`，一个服务进程只有
    ///   "当前打开的一本书"。两个 agent 同时接入时会互相切书——A 刚 open 甲，
    ///   B 一 open 乙，A 的下一次 chapter_write 就写进了乙（工具读的是会话里的
    ///   Project，没有"这次请求属于哪本书"的概念）。表现不是报错，是**写错书**。
    ///
    /// ★ 为什么用令牌当 key：网页「帮助 → 接入 Agent → 生成并复制 MCP 配置」
    ///   每点一次就签发一个新令牌（见 /api/accounts/mcp-token），
    ///   所以「每个 agent 粘一份自己的配置」天然就是「每个 agent 一个会话」。
    ///   同一份配置粘给两个 agent 会共用一个会话——这也符合直觉。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Session> _agentSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// 闲置多久回收会话。每个会话常驻一份整书对象（百万字约 5MB 内存），
    /// 不回收的话，agent 换得越多占得越多。
    /// </summary>
    internal static TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = null,   // 字段名就是 JSON 名（上面刻意都用小写）
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

        // 装上通知通道：生成过程中要能把进度推给客户端（见 NotifyProgress）
        _notify = o =>
        {
            lock (_writeLock)
            {
                writer.Write(JsonSerializer.Serialize(o, _json) + "\n");
                writer.Flush();
            }
        };

        Log("MCP 服务已启动（stdio）");

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var response = await HandleRequestAsync(line);
            if (response != null) await SendAsync(writer, response);
        }

        Log("stdin 关闭，退出");
    }

    /// <summary>
    /// 处理一行 JSON-RPC 请求，返回响应对象；返回 null 表示"不回复"
    /// （空行，或 notifications/* 这类不带 id 的通知——按协议本来就不该回）。
    ///
    /// ★ 抽出来是为了让 **stdio 与 HTTP 两种传输共用同一份分发逻辑**：
    /// 桌面 agent 客户端走 stdio，Web 版走 HTTP，但工具实现只有一份。
    /// 否则两边各写一遍分发，改一个工具要改两处，迟早不一致。
    /// </summary>
    /// <param name="sessionKey">
    /// 哪一个 agent 的会话。HTTP 模式传调用方令牌（每个 agent 一份配置 = 一个令牌）；
    /// stdio 传 null，落到默认会话。
    /// </param>
    /// <param name="prepare">
    /// 真正执行工具之前、**已经拿到会话锁**时调用一次。网页版用它把"还没开过书的
    /// 新会话"带到网页当前打开的那本上——放到锁外做会和正在跑的请求抢会话。
    /// </param>
    internal static async Task<object?> HandleRequestAsync(
        string line, string? sessionKey = null, Action<Session>? prepare = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (Exception ex)
        {
            return RpcError(null, -32700, $"JSON 解析失败：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var mEl))
                return RpcError(null, -32600, "缺少 method 字段");

            var method = mEl.GetString() ?? "";
            var hasId = root.TryGetProperty("id", out var idEl);
            var parameters = root.TryGetProperty("params", out var pEl) ? pEl : default;

            // 通知（notifications/*）没有 id，按协议不回复
            if (!hasId)
            {
                Log($"← 通知 {method}");
                return null;
            }

            Log($"← {method}");
            var session = SessionFor(sessionKey);
            await session.Gate.WaitAsync();
            try
            {
                prepare?.Invoke(session);
                return await DispatchCoreAsync(method, parameters, idEl, session);
            }
            catch (Exception ex)
            {
                Log($"处理 {method} 异常：{ex}");
                return RpcError(idEl, -32603, $"内部错误：{ex.Message}");
            }
            finally
            {
                session.LastUsedUtc = DateTime.UtcNow;
                session.Gate.Release();
            }
        }
    }

    /// <summary>
    /// 确保**这个 agent 的会话**打开的是指定那本书（已经开着就不动）。
    ///
    /// Web 版需要它：网页上打开的书和 agent 手里的书必须是同一本，
    /// 否则 agent 改的是 A 书、网页显示的却是 B 书——两个入口各有一份状态，
    /// 这在"人看着 agent 写"的场景下是致命的。
    ///
    /// ★ 但只在会话**还没开过书**时调用（见 /mcp 里的 prepare 回调）：
    ///   一旦 agent 自己 project_open 过，就钉在它自己的书上，网页切书不再拽它走，
    ///   否则两个 agent 根本没法各写各的。
    /// </summary>
    internal static ToolResult EnsureOpen(Session session, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("路径为空");
        if (string.Equals(session.Path, path, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Ok("already-open");
        return session.Open(path);
    }

    /// <summary>
    /// 装通知通道。stdio 模式写 stdout；Web 版改成往 SignalR 广播，
    /// 这样 agent 的操作能实时出现在用户网页上（人机协同的关键一环）。
    /// </summary>
    internal static void SetNotifier(Action<object>? notify) => _notify = notify;

    private static async Task SendAsync(TextWriter writer, object response)
    {
        var json = JsonSerializer.Serialize(response, _json);
        lock (_writeLock)
        {
            writer.Write(json + "\n");
        }
        await writer.FlushAsync();
    }

    /// <summary>
    /// 发一条 JSON-RPC 通知（没有 id、不等回复）。由 RunAsync 在启动时装上实现。
    ///
    /// ★ 为什么需要：ai_write 生成几千字要几十秒，这期间客户端那边是完全黑屏的。
    /// 用户在对话里等着，看不到任何动静，只会以为卡死或断线——
    /// 而界面里早就有 OnNotice / OnProgress 这套阶段性提示了，MCP 侧一直没接。
    /// </summary>
    private static Action<object>? _notify;
    private static readonly object _writeLock = new();

    internal static void NotifyProgress(JsonElement? token, int progress, int total, string message)
    {
        if (_notify == null || token == null || token.Value.ValueKind == JsonValueKind.Undefined) return;

        _notify(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = new Dictionary<string, object?>
            {
                ["progressToken"] = token.Value.Clone(),
                ["progress"] = progress,
                ["total"] = total,
                ["message"] = message,
            },
        });
    }

    /// <summary>
    /// 取（必要时创建）某个 agent 的会话。key 为空 = stdio / 本机免登录的默认会话。
    /// </summary>
    internal static Session SessionFor(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return _defaultSession;

        var s = _agentSessions.GetOrAdd(key, _ => new Session());
        s.LastUsedUtc = DateTime.UtcNow;
        SweepIdleSessions();
        return s;
    }

    /// <summary>
    /// 回收闲置会话。正在执行请求（Gate 被占）或当前 token 的那一个不动。
    /// </summary>
    internal static int SweepIdleSessions(TimeSpan? idle = null)
    {
        var cut = DateTime.UtcNow - (idle ?? SessionIdleTimeout);
        var removed = 0;
        foreach (var kv in _agentSessions)
        {
            if (kv.Value.LastUsedUtc > cut) continue;
            if (kv.Value.Gate.CurrentCount == 0) continue;   // 有请求在跑
            if (_agentSessions.TryRemove(kv.Key, out _)) removed++;
        }
        return removed;
    }

    /// <summary>当前活跃的 agent 会话数（诊断用）。</summary>
    internal static int AgentSessionCount => _agentSessions.Count;

    /// <summary>
    /// 丢掉某个 agent 的会话。令牌被撤销或过期时调用——否则那个会话还要等
    /// 30 分钟的闲置回收才消失，而它常驻着一整本书的内存。
    /// </summary>
    internal static bool DropSession(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return _agentSessions.TryRemove(key, out _);
    }

    private static async Task<object> DispatchCoreAsync(
        string method, JsonElement parameters, JsonElement id, Session session)
    {
        switch (method)
        {
            case "initialize":
                return RpcOk(id, new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new Dictionary<string, object?>
                    {
                        ["tools"] = new { listChanged = false },
                        // 章节/设定也是资源：支持的客户端可以直接在对话里 @ 引用整章，
                        // 省掉「先 list 再 read」一轮往返（见 Resources.cs）
                        ["resources"] = new { listChanged = false, subscribe = false },
                    },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "TdxClaw 写作助手",
                        ["version"] = "1.0.0",
                    },
                    // ★ Antigravity 等支持懒加载的客户端会把 instructions 保存为 instructions.md 注入系统提示词，
                    // 让 Agent 不用逐个翻工具的 JSON schema 就能掌握完整工作流。
                    // 内容来自 docs/AGENT_GUIDE.md 的 BEGIN/END:instructions 段（见 Guide.cs）——
                    // 手册是唯一真源，这里不再写死一份，免得两处漂移。
                    ["instructions"] = Guide.Instructions,
                });

            case "ping":
                return RpcOk(id, new Dictionary<string, object?>());

            case "tools/list":
                return RpcOk(id, new Dictionary<string, object?> { ["tools"] = ToolList() });

            case "tools/call":
                return RpcOk(id, await CallToolAsync(parameters, session));

            case "resources/list":
                return RpcOk(id, new Dictionary<string, object?>
                {
                    ["resources"] = Resources.List(session),
                });

            case "resources/read":
            {
                var uri = parameters.TryGetProperty("uri", out var uEl) ? uEl.GetString() ?? "" : "";
                var (contents, isErr, msg) = Resources.Read(session, uri);
                if (isErr) return RpcError(id, -32602, msg!);
                return RpcOk(id, new Dictionary<string, object?> { ["contents"] = contents });
            }

            case "resources/templates/list":
                return RpcOk(id, new Dictionary<string, object?> { ["resourceTemplates"] = Resources.Templates() });

            case "prompts/list":
                return RpcOk(id, new Dictionary<string, object?> { ["prompts"] = Array.Empty<object>() });

            default:
                return RpcError(id, -32601, $"不支持的方法：{method}");
        }
    }

    // initialize 的 instructions 已移到 docs/AGENT_GUIDE.md（经 Guide.Instructions 注入），
    // 见文件顶部的说明——不要在代码里再养第二份工作流描述。

    // ==================================================================
    // 工具清单
    // ==================================================================

    private static List<ToolDef> ToolList() => new()
    {
        // 放在第一个：agent 扫工具列表时先看到它。手册内容来自 docs/AGENT_GUIDE.md
        // （见 Guide.cs），含黄金规则、工具地图、五套剧本、长篇上下文治理、出错处置。
        new ToolDef
        {
            name = "guide",
            description =
                "读这份使用手册（完整版）：黄金规则、29 个工具的分组地图、五套任务剧本（从零立书 / 续写 / 审稿 / " +
                "改设定回填 / 导出）、长篇上下文该怎么管、出错怎么处置、明文禁区、HTTP 接入方式。" +
                "★ 第一次接入、或不确定「下一步该调哪个工具」时，先读它。" +
                "不支持工具以外通道的客户端也可以读资源 tdx://guide，两者是同一份文本。",
        },
        new ToolDef
        {
            name = "project_list",
            description = "查找 .tdxproj 项目文件。不给 directory 就自动扫描软件默认项目目录（Documents\\TdxClaw\\Projects 或自定义路径）、当前目录、文档与桌面。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["directory"] = new() { description = "要扫描的目录（也支持传 path）；留空则扫软件默认项目目录与常见位置" },
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
            description = "读五项贯穿设定（大纲/章节大纲/人物（含人物卡汇总）/背景/文风）+ 叙事视角 + 作品简介。",
        },
        new ToolDef
        {
            name = "settings_book_get",
            description = "读设定集。不带 key 返回 12 章清单（含 key），带 key 返回该章全文。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["key"] = new() { description = "章的 SourceKey（内置 12 章：world / power / geography / history / factions / customs / characters / relations / timeline / foreshadow / style / glossary），也可给标题片段" },
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
                    ["summary"] = new() { description = "章节梗概，可留空" },
                },
                required = new List<string> { "title" },
            },
        },
        new ToolDef
        {
            name = "settings_set",
            description = "改写贯穿设定。既可传 field + text 改单项，也可直接传一个或多个字段名（full_outline / chapter_outline / characters / background / writing_style / narrative_viewpoint / description）一次性批量写入多项。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["field"] = new() { description = "单项修改时的字段名：full_outline / chapter_outline / characters / background / writing_style / narrative_viewpoint / description" },
                    ["text"] = new() { description = "单项修改时的新内容（也兼容 content）" },
                    ["full_outline"] = new() { description = "全文大纲（批量模式直接传）" },
                    ["chapter_outline"] = new() { description = "章节大纲（批量模式直接传）" },
                    ["characters"] = new() { description = "人物设定自由文本（批量模式直接传）" },
                    ["background"] = new() { description = "背景设定（批量模式直接传）" },
                    ["writing_style"] = new() { description = "文风设定（批量模式直接传）" },
                    ["narrative_viewpoint"] = new() { description = "叙事视角硬约束（批量模式直接传）" },
                    ["description"] = new() { description = "作品简介（批量模式直接传）" },
                },
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
                    ["key"] = new() { description = "章的 SourceKey（world / power / geography / history / factions / customs / characters / relations / timeline / foreshadow / style / glossary）或标题片段" },
                    ["content"] = new() { description = "新内容（也兼容 text）" },
                    ["mode"] = new() { description = "replace（默认）或 append" },
                },
                required = new List<string> { "key", "content" },
            },
        },
        new ToolDef
        {
            name = "project_status",
            description =
                "一次拿到这本书的进度画像：章数 / 总字数 / 几章还是空的 / 设定填了哪些 / " +
                "有几张人物卡和快照，并给出「下一步该做什么」的建议。" +
                "用户问「写到哪了」「接下来干嘛」时先用这个。",
        },
        new ToolDef
        {
            name = "chapter_rename",
            description = "给某一章改标题。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["number"] = new() { type = "integer", description = "章号" },
                    ["title"] = new() { description = "新标题" },
                },
                required = new List<string> { "number", "title" },
            },
        },
        new ToolDef
        {
            name = "chapter_delete",
            description =
                "删除某一章，其余章节自动重排编号。" +
                "删之前会自动存快照，删错了用 snapshot_list + snapshot_restore 撤回来。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["number"] = new() { type = "integer", description = "要删除的章号" },
                },
                required = new List<string> { "number" },
            },
        },
        new ToolDef
        {
            name = "chapter_reorder",
            description =
                "调整章节顺序：把第 number 章移到第 toNumber 个位置，其余顺移后重新连续编号。" +
                "（章号会整体重排，后续请用新的章号定位。）",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["number"] = new() { type = "integer", description = "要移动的章号（也兼容 fromNumber）" },
                    ["toNumber"] = new() { type = "integer", description = "移到第几个位置（从 1 开始）" },
                },
                required = new List<string> { "number", "toNumber" },
            },
        },
        new ToolDef
        {
            name = "chapter_summary_set",
            description =
                "写/改某一章的梗概。★ 长篇务必每章写完顺手存一句：" +
                "ai_write 续写/审稿时会自动注入前序章节梗概链 + 紧邻上章末尾 800 字，靠梗概链保持跨章连贯。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["number"] = new() { type = "integer", description = "章号" },
                    ["summary"] = new() { description = "梗概内容；留空表示清空" },
                },
                required = new List<string> { "number" },
            },
        },
        new ToolDef
        {
            name = "snapshot_list",
            description =
                "列出这个项目的历史快照。写入正文、改写设定、AI 生成、删除章节前都会自动存，" +
                "所以「刚才那次改坏了，撤回」在这里找。",
        },
        new ToolDef
        {
            name = "snapshot_restore",
            description =
                "恢复到某个快照（用 snapshot_list 取 id）。恢复前会自动再存一份当前状态，" +
                "所以这一步本身也是可以反悔的。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["id"] = new() { description = "快照 id，如 007" },
                },
                required = new List<string> { "id" },
            },
        },
        new ToolDef
        {
            name = "characters_list",
            description = "列出结构化人物卡（带 id / 角色 / 年龄 / 性格 / 能力 / 关系 / 背景 / 备注）。人物卡会自动并入 AI 写作上下文。",
        },
        new ToolDef
        {
            name = "character_upsert",
            description =
                "新建或更新一张人物卡。给 id 就是改；不给 id 按 name 匹配，都没有就新建。" +
                "★ 只改本次传入的字段，没传的保持原样（不会清空）。人物卡会自动注入 ai_write 上下文。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["name"] = new() { description = "姓名（必填）" },
                    ["id"] = new() { description = "要修改的人物卡 id，前缀匹配即可；不给则按姓名匹配" },
                    ["role"] = new() { description = "主角 / 配角 / 反派 等" },
                    ["age"] = new() { type = "integer", description = "年龄（整数，如 27）" },
                    ["gender"] = new() { description = "性别" },
                    ["occupation"] = new() { description = "身份职业" },
                    ["appearance"] = new() { description = "外貌" },
                    ["personality"] = new() { description = "性格" },
                    ["background"] = new() { description = "背景经历" },
                    ["abilities"] = new() { description = "能力 / 技能 / 专长" },
                    ["relationships"] = new() { description = "人际关系" },
                    ["notes"] = new() { description = "备注 / 口头禅 / 补充设定" },
                    ["scope"] = new() { description = "长篇分级：core 常驻（每轮都进上下文）/ arc 本卷关键（默认）/ guest 单章客串" },
                    ["status"] = new() { description = "人物状态：active 活跃（默认）/ inactive 幕后 / retired 退场 / deceased 阵亡（阵亡后不再进上下文，审稿当严禁出场）" },
                },
                required = new List<string> { "name" },
            },
        },
        new ToolDef
        {
            name = "character_delete",
            description = "删除一张人物卡（给 id 或 name）。删前自动存快照。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["id"] = new() { description = "人物卡 id（前缀匹配）" },
                    ["name"] = new() { description = "姓名；二选一" },
                },
            },
        },
        new ToolDef
        {
            name = "memory_get",
            description = "读 AI 写作记忆（每次生成都会自动带上的那份经验与偏好）。",
        },
        new ToolDef
        {
            name = "memory_set",
            description =
                "写入 AI 写作记忆。★ 用户长期性的纠正与偏好（「别写死主角」「对话别太长」）" +
                "应该沉淀到这里，否则下一轮就忘了。mode=append 追加，默认 replace 覆盖。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["text"] = new() { description = "要记下的内容（也兼容 content）" },
                    ["mode"] = new() { description = "replace（默认）或 append" },
                },
                required = new List<string> { "text" },
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
                    ["output"] = new() { description = "输出文件完整路径（也兼容传 path）" },
                    ["path"] = new() { description = "output 的别名，导出文件完整路径" },
                    ["paperMode"] = new() { type = "boolean", description = "是否按论文版式（首页连排摘要+数字编号章节+GB/T 7714 文献）" },
                },
            },
        },
        new ToolDef
        {
            name = "ai_write",
            description =
                "调用本软件配好的 AI 写作（与界面里点按钮完全同源：当前提示词方案 + 五项贯穿设定 + " +
                "结构化人物卡 + 设定集 + 章节梗概链与上章末尾 + 叙事视角硬约束 + AI 写作记忆 + 参考文献库 + 技能）。" +
                "task 可选 continue（续写）/ polish（润色）/ expand（扩写）/ " +
                "review（一致性审稿）/ name（起名）/ chat（自由问答）/ " +
                "outline（全文大纲）/ chapter_outline（章节大纲）/ character（人物设定）/ " +
                "background（背景设定）/ write_style（文风）/ setting_book（补设定集）。" +
                "后五个是「从零把一本书立起来」的那一步：已有内容时自动改成在原有基础上完善。" +
                "★ 默认只返回文本、不动项目；要落盘传 writeBack=true——正文类写章节" +
                "（续写/扩写默认追加、润色默认替换），设定类写对应设定字段，" +
                "可用 writeMode 指定，同样受防覆盖保护。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["task"] = new() { description = "continue / polish / expand / review / name / chat / outline / chapter_outline / character / background / write_style / setting_book" },
                    ["number"] = new() { type = "integer", description = "针对哪一章（续写/润色/审稿必填）" },
                    ["instruction"] = new() { description = "对本次生成的具体要求" },
                    ["maxTokens"] = new() { type = "integer", description = "输出上限，0 表示按任务自动给（续写/扩写/对话/设定集默认 16384，润色/审稿 8192~16384，设定生成默认 8192）" },
                    ["writeBack"] = new() { type = "boolean", description = "true 时把结果写回该章正文或对应设定（默认 false，只返回文本由你决定）" },
                    ["writeMode"] = new() { description = "写回方式：append 追加 / replace 覆盖；默认续写·扩写为 append、润色为 replace" },
                    ["profile"] = new() { description = "用哪套 API 配置（如论文走 GPT、小说走 DeepSeek）；留空用当前启用的" },
                    ["skill"] = new() { description = "技能名或 id（如「黄金三章」）；留空用当前方案记住的那个，传 none 表示不用技能" },
                    ["preset"] = new() { description = "本次用哪套提示词方案：novel / academic / official / general / ultra_long_novel 或自定义方案 id；留空用当前生效方案。★ 只影响这一次请求，不改全局设置" },
                },
                required = new List<string> { "task" },
            },
        },
        new ToolDef
        {
            name = "ai_config_check",
            description =
                "检查本软件的 AI 配置是否正常（服务商 / 模型 / Key 是否填了、能不能连通）。" +
                "ai_write 报失败时先调这个定位原因：401 是 Key 失效或欠费，404 是模型名不对。" +
                "★ 任何情况下都不输出 Key 本身。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["profile"] = new() { description = "要看哪个配置；留空看当前启用的" },
                    ["probe"] = new() { type = "boolean", description = "是否真的发一个最小请求探测连通性（默认 true）" },
                },
            },
        },
        new ToolDef
        {
            name = "project_create",
            description =
                "新建一个 .tdxproj 项目并立刻打开（自动补设定集 12 章骨架）。" +
                "支持传 directory 或 path（可直接传完整 .tdxproj 文件路径），不传目录时默认放到软件项目目录（Documents\\TdxClaw\\Projects 或自定义路径），并可在建书时顺带写入初始贯穿设定。" +
                "同名文件已存在时报错而不是覆盖——直接覆盖别人的稿子是灾难。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["name"] = new() { description = "项目名（也是文件名；若 path 已含 .tdxproj 文件名可省略）" },
                    ["directory"] = new() { description = "放到哪个目录；留空放软件默认项目目录（Documents\\TdxClaw\\Projects）" },
                    ["path"] = new() { description = "directory 的别名，支持传目录路径或完整 .tdxproj 文件路径" },
                    ["description"] = new() { description = "作品简介，可留空" },
                    ["full_outline"] = new() { description = "初始全文大纲，可留空" },
                    ["chapter_outline"] = new() { description = "初始章节大纲，可留空" },
                    ["characters"] = new() { description = "初始人物设定，可留空" },
                    ["background"] = new() { description = "初始背景设定，可留空" },
                    ["writing_style"] = new() { description = "初始文风设定，可留空" },
                    ["narrative_viewpoint"] = new() { description = "初始叙事视角，可留空" },
                },
            },
        },
        new ToolDef
        {
            name = "project_delete",
            description =
                "删除一整本书（不可逆操作，需二次确认）。" +
                "★ confirm 必须原样回传目标文件名（project_list 里看到的那个，如 \"守灯人.tdxproj\"）才会真删——" +
                "这是防止 agent 听错一个书名就删掉整部稿子的保险。" +
                "删除不是硬删：整本书（含 .bak / 对话记忆 / 快照）移入书库旁的 .trash/，照着里面的 _删除信息.txt 可原样搬回。" +
                "正打开着的书拒绝删。同名导出文件（.txt/.docx）不随书删，会在结果里说明。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["name"] = new() { description = "书名或文件名（如 守灯人 或 守灯人.tdxproj）" },
                    ["confirm"] = new() { description = "二次确认：必须与目标文件名完全一致（如 \"守灯人.tdxproj\"）。不一致就不删" },
                },
                required = new List<string> { "name", "confirm" },
            },
        },
        new ToolDef
        {
            name = "ui_typography",
            description =
                "读写网页版的正文排版（字体 / 字号 / 行距 / 字距，只作用于写作区）。" +
                "用户说「字太小」「行距太挤」「换成楷体」这类话时用它，别让他自己去外观设置里翻。" +
                "不传参数 = 查看当前值 + 列出可用字体预设；传了参数 = 修改并立即生效（网页端开着会自动应用）。" +
                "传 reset=true 恢复默认。允许范围：" + EditorTypographyConfig.RangeHint + "。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["preset"] = new() { description = "字体预设名或 id：宋体 / 黑体 / 楷体 / 仿宋 / 圆体 / 西文衬线 / 等宽 / 系统默认" },
                    ["fontFamily"] = new() { description = "自定义字体栈（一般用 preset 就够）。只能含字母、数字、空格、逗号、引号、连字符、下划线、点" },
                    ["fontSize"] = new() { type = "number", description = "正文字号 px，如 20。允许范围见预设说明（不传参数调用可看到）" },
                    ["lineHeight"] = new() { type = "number", description = "行距倍率，如 2.2" },
                    ["letterSpacing"] = new() { type = "number", description = "字距 em，默认 0.02" },
                    ["reset"] = new() { type = "boolean", description = "true = 恢复全部默认排版" },
                },
                required = new List<string>(),   // 一个都不传 = 只读（看当前值与可用预设）
            },
        },
        new ToolDef
        {
            name = "ultra_config",
            description =
                "查看或启用「长篇增强结构」（分卷 / 角色分级 / 主角状态面板）。" +
                "★ 这三项只影响启用了它的项目：论文、公文、普通小说项目保持未启用，行为一字不变。" +
                "action=get 查看；action=enable 启用（需 confirm=\"enable\"，可选 autoVolumes=true 按现有章节切卷）；" +
                "action=update 改开关或动态预算比例。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["action"] = new() { description = "get（默认）/ enable / update" },
                    ["confirm"] = new() { description = "enable 时必须传 \"enable\"，防止 agent 误开" },
                    ["autoVolumes"] = new() { type = "boolean", description = "启用时顺便按现有章节切卷（默认 false）" },
                    ["chaptersPerVolume"] = new() { type = "integer", description = "自动切卷时每卷多少章（默认 50）" },
                    ["enableVolumes"] = new() { type = "boolean", description = "update：是否启用分卷" },
                    ["enableCharacterLifecycle"] = new() { type = "boolean", description = "update：是否启用角色分级筛选" },
                    ["enableStateBoard"] = new() { type = "boolean", description = "update：是否注入主角状态面板" },
                    ["dynamicBudgetRatio"] = new() { type = "number", description = "update：动态素材占窗口比例（0.08~0.20，默认 0.12）" },
                },
                required = new List<string>(),
            },
        },
        new ToolDef
        {
            name = "volume_list",
            description =
                "列出全书的分卷：每卷的章节范围、字数、目标与回顾，以及有多少章还没分卷。" +
                "只在启用了长篇结构的项目里可用。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>(),
                required = new List<string>(),
            },
        },
        new ToolDef
        {
            name = "volume_upsert",
            description =
                "新建一卷，或更新已有卷（标题 / 目标 / 卷回顾）。" +
                "不传 volumeId 就是新建，序号默认取现有最大值 +1；传 volumeId 就是改那一卷。" +
                "卷回顾会在超长篇的续写/审稿里代替「全卷正文」作为前情，写得越准越省 token。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["volumeId"] = new() { description = "要改哪一卷；留空 = 新建" },
                    ["number"] = new() { type = "integer", description = "卷序号（新建时留空自动取下一个）" },
                    ["title"] = new() { description = "卷名，如「第二卷 剑冢」" },
                    ["goal"] = new() { description = "本卷要达成的核心目标（进 AI 上下文）" },
                    ["summary"] = new() { description = "本卷回顾（卷末写；进 AI 上下文，代替全卷正文）" },
                },
                required = new List<string>(),
            },
        },
        new ToolDef
        {
            name = "volume_delete",
            description =
                "删掉一个卷。★ confirm 必须原样回传卷名。删卷**不删章节**——相关章节转为「未分卷」，正文一字不动。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["volumeId"] = new() { description = "卷 id（与 number 二选一）" },
                    ["number"] = new() { type = "integer", description = "卷序号（与 volumeId 二选一）" },
                    ["confirm"] = new() { description = "二次确认：原样回传卷名，如「第二卷 剑冢」" },
                },
                required = new List<string> { "confirm" },
            },
        },
        new ToolDef
        {
            name = "chapter_volume_set",
            description =
                "把一批章节挂到某个卷，或改为「未分卷」（不传 volumeId）。" +
                "章号支持 numbers=\"1-5,7\" 或数组 [1,2,3]；卷可以传 volumeId 或卷序号。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["numbers"] = new() { description = "章号：\"1-5,7\" 或 [1,2,3]（与 number 二选一）" },
                    ["number"] = new() { type = "integer", description = "单章章号" },
                    ["volumeId"] = new() { description = "目标卷 id 或卷序号；留空 = 改为未分卷" },
                },
                required = new List<string>(),
            },
        },
        new ToolDef
        {
            name = "state_board_get",
            description =
                "读主角的当前状态面板：境界/阶段、当前处境、当前目标、关键道具、生死状态。" +
                "续写前先看这个，免得写出「早就升过级却还在用旧招」的段落。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>(),
                required = new List<string>(),
            },
        },
        new ToolDef
        {
            name = "state_board_update",
            description =
                "更新主角状态（升级、换地图、得失关键道具、状态变化）。" +
                "只改传入的字段，没传的保持原样。改完网页端下次生成就会用新状态。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["characterId"] = new() { description = "角色 id（与 name 二选一）" },
                    ["name"] = new() { description = "角色名（与 characterId 二选一）" },
                    ["realm"] = new() { description = "当前境界/职级/阶段，如「金丹初期」" },
                    ["situation"] = new() { description = "一句话当前处境，如「被困剑冢」" },
                    ["goal"] = new() { description = "当前目标，如「找到出口」" },
                    ["items"] = new() { description = "关键道具列表：[\"残剑\",\"灯芯\"] 或 \"残剑,灯芯\"（整体替换）" },
                    ["status"] = new() { description = "active / inactive / retired / deceased" },
                },
                required = new List<string>(),
            },
        },
        new ToolDef
        {
            name = "character_scope_set",
            description =
                "批量设置角色的分级与生死状态（归档一批龙套、把某角色标为阵亡时用）。" +
                "scope 可选 core（核心常驻）/ arc（本卷关键）/ guest（单章客串）；" +
                "status 可选 active / inactive / retired / deceased。" +
                "标成 deceased 后，续写会把他放进「严禁出场」名单。",
            inputSchema = new ToolSchema
            {
                properties = new Dictionary<string, ToolProp>
                {
                    ["names"] = new() { description = "角色名列表：[\"老王\",\"苏晚\"] 或 \"老王,苏晚\"" },
                    ["ids"] = new() { description = "角色 id 列表" },
                    ["name"] = new() { description = "单个角色名" },
                    ["characterId"] = new() { description = "单个角色 id" },
                    ["scope"] = new() { description = "core / arc / guest" },
                    ["status"] = new() { description = "active / inactive / retired / deceased" },
                },
                required = new List<string>(),
            },
        },
    };

    // ==================================================================
    // 工具分派
    // ==================================================================

    private static async Task<object> CallToolAsync(JsonElement parameters, Session session)
    {
        if (!parameters.TryGetProperty("name", out var nEl))
            return ToolText("缺少工具名 name。", true);

        var name = nEl.GetString() ?? "";
        var args = parameters.TryGetProperty("arguments", out var aEl) ? aEl : default;

        // 客户端在 params._meta.progressToken 里给了令牌，就表示它愿意收进度通知
        var progressToken = parameters.TryGetProperty("_meta", out var metaEl)
                            && metaEl.ValueKind == JsonValueKind.Object
                            && metaEl.TryGetProperty("progressToken", out var ptEl)
            ? ptEl
            : (JsonElement?)null;

        try
        {
            PublishToolStart(name, args, session);
            ToolResult r;

            // 不需要项目的工具（只调一次——Open 有副作用，重复调用会白白刷新文件指纹）
            switch (name)
            {
                case "guide":
                    // 手册与项目无关：没打开书也要能读到（正是这种时候最需要它）
                    return ToolText(Guide.Text, false);
                case "project_list":
                {
                    var r0 = NovelTools.ListProjects(args);
                    PublishToolDone(name, args, r0, projectModified: false, session);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "project_open":
                {
                    var r0 = session.Open(NovelTools.Str(args, "path"));
                    PublishToolDone(name, args, r0, projectModified: !r0.IsError, session);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "project_create":
                {
                    var r0 = NovelTools.CreateProject(session, args);
                    PublishToolDone(name, args, r0, projectModified: !r0.IsError, session);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "project_delete":
                {
                    // 删书不需要先打开项目（删的往往正是"没打开的那本"），
                    // 但它自己会拒绝删当前打开着的书。
                    var r0 = NovelTools.DeleteProject(session, args);
                    PublishToolDone(name, args, r0, projectModified: false, session);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "ai_config_check":
                {
                    var r0 = await AiTools.ConfigCheckAsync(args);
                    PublishToolDone(name, args, r0, projectModified: false, session);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "ui_typography":
                {
                    // 改的是服务器上的排版配置（typo.json），不碰项目文件——
                    // 所以 projectModified=false。网页端收到 done 会重拉一次排版。
                    var r0 = NovelTools.Typography(args);
                    PublishToolDone(name, args, r0, projectModified: false, session);
                    return ToolText(r0.Text, r0.IsError);
                }
            }

            // 需要项目的工具
            if (!session.TryGet(out var p, out var err))
                return ToolText(err.Text, true);

            bool mutatesProject = false;
            switch (name)
            {
                case "project_status": r = NovelTools.ProjectStatus(session, p); break;
                case "chapters_list": r = NovelTools.ListChapters(p); break;
                case "chapter_read": r = NovelTools.ReadChapter(p, args); break;
                case "chapter_search": r = NovelTools.SearchChapters(p, args); break;
                case "settings_get": r = NovelTools.GetSettings(p); break;
                case "settings_book_get": r = NovelTools.GetSettingsBook(p, args); break;
                case "characters_stats": r = NovelTools.CharacterStats(p); break;

                case "chapter_write": r = NovelTools.WriteChapter(session, p, args); mutatesProject = true; break;
                case "chapter_create": r = NovelTools.CreateChapter(session, p, args); mutatesProject = true; break;
                case "chapter_rename": r = NovelTools.RenameChapter(session, p, args); mutatesProject = true; break;
                case "chapter_delete": r = NovelTools.DeleteChapter(session, p, args); mutatesProject = true; break;
                case "chapter_reorder": r = NovelTools.ReorderChapter(session, p, args); mutatesProject = true; break;
                case "chapter_summary_set": r = NovelTools.SetChapterSummary(session, p, args); mutatesProject = true; break;

                case "settings_set": r = NovelTools.SetSetting(session, p, args); mutatesProject = true; break;
                case "settings_book_set": r = NovelTools.SetSettingsBook(session, p, args); mutatesProject = true; break;

                case "snapshot_list": r = NovelTools.ListSnapshots(session); break;
                case "snapshot_restore": r = NovelTools.RestoreSnapshot(session, args); mutatesProject = true; break;

                case "characters_list": r = NovelTools.ListCharacters(p); break;
                case "character_upsert": r = NovelTools.UpsertCharacter(session, p, args); mutatesProject = true; break;
                case "character_delete": r = NovelTools.DeleteCharacter(session, p, args); mutatesProject = true; break;

                case "memory_get": r = NovelTools.GetMemory(p); break;
                case "memory_set": r = NovelTools.SetMemory(session, p, args); mutatesProject = true; break;

                // ── 超长篇结构（仅启用了它的项目可用，未启用时返回明确提示）──
                case "ultra_config":
                    r = UltraTools.Config(session, p, args);
                    // get 是只读；enable/update 才算改项目
                    mutatesProject = !string.Equals(
                        NovelTools.Str(args, "action", "get"), "get", StringComparison.OrdinalIgnoreCase);
                    break;
                case "volume_list": r = UltraTools.ListVolumes(p); break;
                case "volume_upsert": r = UltraTools.UpsertVolume(session, p, args); mutatesProject = true; break;
                case "volume_delete": r = UltraTools.DeleteVolume(session, p, args); mutatesProject = true; break;
                case "chapter_volume_set": r = UltraTools.SetChapterVolume(session, p, args); mutatesProject = true; break;
                case "state_board_get": r = UltraTools.GetStateBoard(p); break;
                case "state_board_update": r = UltraTools.UpdateStateBoard(session, p, args); mutatesProject = true; break;
                case "character_scope_set": r = UltraTools.SetCharacterScope(session, p, args); mutatesProject = true; break;

                case "project_export": r = NovelTools.Export(p, args); break;

                case "ai_write":
                    r = await AiTools.WriteAsync(session, p, args, progressToken);
                    mutatesProject = NovelTools.Bool(args, "writeBack", false);
                    break;

                default:
                    return ToolText($"未知工具「{name}」。用 tools/list 看有哪些工具。", true);
            }

            PublishToolDone(name, args, r, projectModified: mutatesProject && !r.IsError, session);
            return ToolText(r.Text, r.IsError);
        }
        catch (Exception ex)
        {
            Log($"工具 {name} 抛异常：{ex}");
            McpLiveBridge.Publish(new McpLiveEvent
            {
                EventType = "error",
                ToolName = name,
                ProjectPath = session.Path,
                Summary = $"🤖 MCP 工具 {name} 出错：{ex.Message}",
            });
            return ToolText($"工具执行出错：{ex.Message}", true);
        }
    }

    private static void PublishToolStart(string name, JsonElement args, Session session)
    {
        int chNum = ResolveTargetChapterNumber(name, args, beforeExec: true, session);
        var task = name == "ai_write" ? NovelTools.Str(args, "task", "continue").Trim().ToLowerInvariant() : null;
        var summary = name switch
        {
            "project_create" => $"🤖 MCP 正在新建项目「{NovelTools.Str(args, "name", "新项目")}」…",
            "project_delete" => $"🤖 MCP 正在删除作品「{NovelTools.Str(args, "name")}」…",
            "project_open" => "🤖 MCP 正在打开项目…",
            "chapter_create" => $"🤖 MCP 正在新建章节「{NovelTools.Str(args, "title")}」…",
            "chapter_write" => $"🤖 MCP 正在写入第 {chNum} 章…",
            "chapter_rename" => $"🤖 MCP 正在重命名第 {chNum} 章…",
            "chapter_delete" => $"🤖 MCP 正在删除第 {chNum} 章…",
            "chapter_reorder" => "🤖 MCP 正在调整章节顺序…",
            "chapter_summary_set" => $"🤖 MCP 正在写入第 {chNum} 章梗概…",
            "settings_set" => "🤖 MCP 正在更新作品设定…",
            "settings_book_set" => $"🤖 MCP 正在写入设定集（{NovelTools.Str(args, "key")}）…",
            "character_upsert" => $"🤖 MCP 正在更新人物卡「{NovelTools.Str(args, "name")}」…",
            "character_delete" => $"🤖 MCP 正在删除人物卡「{NovelTools.Str(args, "name")}」…",
            "memory_set" => "🤖 MCP 正在更新 AI 写作记忆…",
            "ui_typography" => "🤖 MCP 正在调整网页端正文排版…",
            "snapshot_restore" => "🤖 MCP 正在恢复历史快照…",
            "project_export" => $"🤖 MCP 正在导出项目（{NovelTools.Str(args, "format", "txt").ToUpperInvariant()}）…",
            "ai_write" => $"🤖 MCP AI 正在{AiTools.TaskDisplayLabel(task ?? "continue")}{(chNum > 0 ? $"（第 {chNum} 章）" : "")}…",
            "chapter_read" => $"🤖 MCP 正在阅读第 {chNum} 章…",
            _ => $"🤖 MCP 正在执行 {name}…",
        };

        McpLiveBridge.Publish(new McpLiveEvent
        {
            EventType = "start",
            ToolName = name,
            TaskName = task,
            ProjectPath = session.Path,
            ChapterNumber = chNum,
            SettingField = ResolveSettingField(name, args, task),
            WriteBack = name == "ai_write" && NovelTools.Bool(args, "writeBack", false),
            Summary = summary,
        });
    }

    private static void PublishToolDone(string name, JsonElement args, ToolResult result, bool projectModified, Session session)
    {
        int chNum = ResolveTargetChapterNumber(name, args, beforeExec: false, session);
        var task = name == "ai_write" ? NovelTools.Str(args, "task", "continue").Trim().ToLowerInvariant() : null;
        var p = session.Project;
        var ch = chNum > 0 && p != null ? p.Chapters.FirstOrDefault(c => c.ChapterNumber == chNum) : null;

        string summary;
        if (result.IsError)
        {
            summary = $"🤖 MCP {name} 未成功：{Session.Truncate(result.Text, 60)}";
        }
        else
        {
            summary = name switch
            {
                "project_create" => $"🤖 MCP 已新建项目《{p?.ProjectName ?? ""}》",
                "project_delete" => $"🤖 MCP 已删除作品《{NovelTools.Str(args, "name")}》（已移入回收站）",
                "project_open" => $"🤖 MCP 已打开项目《{p?.ProjectName ?? ""}》",
                "chapter_create" => ch != null
                    ? $"🤖 MCP 已新建第 {ch.ChapterNumber} 章「{ch.Title}」"
                    : "🤖 MCP 已新建章节",
                "chapter_write" => ch != null
                    ? $"🤖 MCP 已写入第 {ch.ChapterNumber} 章「{ch.Title}」（共 {ch.WordCount} 字）"
                    : $"🤖 MCP 已写入第 {chNum} 章",
                "chapter_rename" => ch != null
                    ? $"🤖 MCP 已重命名第 {ch.ChapterNumber} 章为「{ch.Title}」"
                    : "🤖 MCP 已重命名章节",
                "chapter_delete" => $"🤖 MCP 已删除第 {chNum} 章",
                "chapter_reorder" => "🤖 MCP 已调整章节顺序",
                "chapter_summary_set" => $"🤖 MCP 已更新第 {chNum} 章梗概",
                "settings_set" => "🤖 MCP 已更新作品设定",
                "settings_book_set" => $"🤖 MCP 已更新设定集（{NovelTools.Str(args, "key")}）",
                "character_upsert" => $"🤖 MCP 已更新人物卡「{NovelTools.Str(args, "name")}」",
                "character_delete" => $"🤖 MCP 已删除人物卡「{NovelTools.Str(args, "name")}」",
                "memory_set" => "🤖 MCP 已更新 AI 写作记忆",
                "ui_typography" => "🤖 MCP 已调整网页端正文排版",
                "snapshot_restore" => "🤖 MCP 已恢复历史版本快照",
                "project_export" => $"🤖 MCP 已导出项目（{NovelTools.Str(args, "format", "txt").ToUpperInvariant()}）",
                "ai_write" => $"🤖 MCP AI 已完成{AiTools.TaskDisplayLabel(task ?? "continue")}{(ch != null ? $" · 第 {ch.ChapterNumber} 章「{ch.Title}」（{ch.WordCount} 字）" : "")}",
                "chapter_read" => ch != null
                    ? $"🤖 MCP 已读取第 {ch.ChapterNumber} 章「{ch.Title}」"
                    : $"🤖 MCP 已读取第 {chNum} 章",
                _ => $"🤖 MCP 已完成 {name}",
            };
        }

        McpLiveBridge.Publish(new McpLiveEvent
        {
            EventType = result.IsError ? "error" : "done",
            ToolName = name,
            TaskName = task,
            ProjectPath = session.Path,
            ChapterNumber = chNum,
            SettingField = ResolveSettingField(name, args, task),
            ProjectModified = projectModified,
            WriteBack = name == "ai_write" && NovelTools.Bool(args, "writeBack", false),
            PreviewText = name == "ai_write" ? result.Text : null,
            Summary = summary,
        });
    }

    private static int ResolveTargetChapterNumber(string name, JsonElement args, bool beforeExec, Session session)
    {
        if (name == "chapter_reorder")
            return NovelTools.Int(args, "toNumber", NovelTools.Int(args, "fromNumber", 0));

        var n = NovelTools.Int(args, "number", 0);
        if (n > 0) return n;

        if (name == "chapter_create" && !beforeExec && session.Project != null && session.Project.Chapters.Count > 0)
            return session.Project.Chapters.Max(c => c.ChapterNumber);

        return 0;
    }

    private static string? ResolveSettingField(string name, JsonElement args, string? task) => name switch
    {
        "settings_set" => NovelTools.Str(args, "field"),
        "settings_book_set" => NovelTools.Str(args, "key"),
        "character_upsert" or "character_delete" => "characters",
        "ai_write" when task != null => AiTools.SettingTargetFor(task),
        _ => null,
    };

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

    /// <summary>
    /// 日志。默认只写 stderr；设环境变量 TDX_MCP_LOG=&lt;文件路径&gt; 时同时落盘。
    /// ★ 有些客户端（Antigravity 等）会吞掉子进程的 stderr，排障时没有日志等于瞎子，
    /// 所以留一个落盘开关。stdout 是协议通道，一个字节都不能污染。
    /// </summary>
    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
        try { Console.Error.WriteLine($"[mcp] {line}"); } catch { /* stderr 不可写也不能崩 */ }

        var path = Environment.GetEnvironmentVariable("TDX_MCP_LOG");
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                // 目录不存在时 AppendAllText 会直接抛，被下面的 catch 吞掉后用户只会看到
                // "我明明配了 TDX_MCP_LOG 却没有日志"——排障时没有日志等于瞎子，
                // 所以这里主动把目录建出来。
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + "\n");
            }
            catch { }
        }
    }
}
