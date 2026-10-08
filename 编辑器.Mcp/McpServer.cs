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
    private static readonly Session _session = new();

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
    private static readonly SemaphoreSlim _dispatchLock = new(1, 1);

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

    private static async Task<object> DispatchAsync(string method, JsonElement parameters, JsonElement id)
    {
        await _dispatchLock.WaitAsync();
        try
        {
            return await DispatchCoreAsync(method, parameters, id);
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    private static async Task<object> DispatchCoreAsync(string method, JsonElement parameters, JsonElement id)
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
                    // 让 Agent 不用逐个翻 28 个工具的 JSON schema 就能掌握完整工作流。
                    ["instructions"] = ServerInstructions,
                });

            case "ping":
                return RpcOk(id, new Dictionary<string, object?>());

            case "tools/list":
                return RpcOk(id, new Dictionary<string, object?> { ["tools"] = ToolList() });

            case "tools/call":
                return RpcOk(id, await CallToolAsync(parameters));

            case "resources/list":
                return RpcOk(id, new Dictionary<string, object?>
                {
                    ["resources"] = Resources.List(_session),
                });

            case "resources/read":
            {
                var uri = parameters.TryGetProperty("uri", out var uEl) ? uEl.GetString() ?? "" : "";
                var (contents, isErr, msg) = Resources.Read(_session, uri);
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

    private const string ServerInstructions =
        "TdxClaw 写作助手 MCP 服务器：直接读写 .tdxproj 长篇写作项目（小说 / 学术论文 / 公文 / 通用写作），并与桌面端共享同一套 AI 提示词方案、设定集、结构化人物卡、记忆与文献库。\n\n" +
        "推荐工作流：\n" +
        "1. 找书与打开：先用 project_list 查找 .tdxproj，调用 project_open(path) 打开，或用 project_create(name, directory/path, description, ...) 新建（支持直接传完整 .tdxproj 路径并顺带填初始设定）；打开后优先调 project_status 获取全书进度画像与下一步建议。\n" +
        "2. 读稿与检索：chapters_list 看目录，chapter_read 读单章，chapter_search 全书搜词查伏笔/矛盾，settings_get 读五项贯穿设定（含结构化人物卡汇总），settings_book_get 读 12 章设定集，characters_list / characters_stats 看人物卡与出场统计，memory_get 读 AI 写作记忆。\n" +
        "3. 写稿与管章：chapter_create(title, content, summary) 新建章，chapter_write(number, content, mode=replace/append) 写正文，chapter_rename 改标题，chapter_reorder 调序，chapter_delete 删章，chapter_summary_set(number, summary) 写章节梗概（每章写完建议顺手更新梗概，续写/审稿会自动注入全部前情梗概 + 紧邻上章末尾 800 字）。\n" +
        "4. 改设定与记忆：settings_set 支持单项 (field, text) 或批量同时传多个字段 (full_outline / chapter_outline / characters / background / writing_style / narrative_viewpoint / description)；settings_book_set(key, content, mode) 改设定集（12 章内置 key：world / power / geography / history / factions / customs / characters / relations / timeline / foreshadow / style / glossary）；character_upsert(name, role, age, gender, occupation, appearance, personality, background, abilities, relationships, notes) 管理结构化人物卡；memory_set 沉淀作者长期偏好。\n" +
        "5. 调用本软件 AI（ai_write）：自动注入五项设定 + 结构化人物卡 + 设定集 + 章节梗概链 + 上章末尾 + 记忆 + 文献库。task 支持 continue（续写，默认 maxTokens=16384）/ polish（润色）/ expand（扩写，默认 16384）/ review（一致性审稿）/ name（起名）/ chat（自由问答，默认 16384）/ outline / chapter_outline / character / background / write_style / setting_book。默认 writeBack=false 只返回文本；显式传 writeBack=true 才落盘。报失败时先调 ai_config_check 诊断。\n" +
        "6. 安全与回滚：所有写操作均串行加锁、写前自动存快照（高频轻量设定/人物卡修改自动合并防刷屏）并检查文件冲突；改错可随时用 snapshot_list + snapshot_restore 回滚；完稿用 project_export(format, output/path) 导出 docx / pdf / txt。\n" +
        "7. 资源（tdx://）：打开项目后可通过 list_resources / read_resource 直接读取 tdx://chapter/{number}、tdx://settings/{key}、tdx://settings-book/{key}。";

    // ==================================================================
    // 工具清单
    // ==================================================================

    private static List<ToolDef> ToolList() => new()
    {
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

        // 客户端在 params._meta.progressToken 里给了令牌，就表示它愿意收进度通知
        var progressToken = parameters.TryGetProperty("_meta", out var metaEl)
                            && metaEl.ValueKind == JsonValueKind.Object
                            && metaEl.TryGetProperty("progressToken", out var ptEl)
            ? ptEl
            : (JsonElement?)null;

        try
        {
            PublishToolStart(name, args);
            ToolResult r;

            // 不需要项目的工具（只调一次——Open 有副作用，重复调用会白白刷新文件指纹）
            switch (name)
            {
                case "project_list":
                {
                    var r0 = NovelTools.ListProjects(args);
                    PublishToolDone(name, args, r0, projectModified: false);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "project_open":
                {
                    var r0 = _session.Open(NovelTools.Str(args, "path"));
                    PublishToolDone(name, args, r0, projectModified: !r0.IsError);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "project_create":
                {
                    var r0 = NovelTools.CreateProject(_session, args);
                    PublishToolDone(name, args, r0, projectModified: !r0.IsError);
                    return ToolText(r0.Text, r0.IsError);
                }
                case "ai_config_check":
                {
                    var r0 = await AiTools.ConfigCheckAsync(args);
                    PublishToolDone(name, args, r0, projectModified: false);
                    return ToolText(r0.Text, r0.IsError);
                }
            }

            // 需要项目的工具
            if (!_session.TryGet(out var p, out var err))
                return ToolText(err.Text, true);

            bool mutatesProject = false;
            switch (name)
            {
                case "project_status": r = NovelTools.ProjectStatus(_session, p); break;
                case "chapters_list": r = NovelTools.ListChapters(p); break;
                case "chapter_read": r = NovelTools.ReadChapter(p, args); break;
                case "chapter_search": r = NovelTools.SearchChapters(p, args); break;
                case "settings_get": r = NovelTools.GetSettings(p); break;
                case "settings_book_get": r = NovelTools.GetSettingsBook(p, args); break;
                case "characters_stats": r = NovelTools.CharacterStats(p); break;

                case "chapter_write": r = NovelTools.WriteChapter(_session, p, args); mutatesProject = true; break;
                case "chapter_create": r = NovelTools.CreateChapter(_session, p, args); mutatesProject = true; break;
                case "chapter_rename": r = NovelTools.RenameChapter(_session, p, args); mutatesProject = true; break;
                case "chapter_delete": r = NovelTools.DeleteChapter(_session, p, args); mutatesProject = true; break;
                case "chapter_reorder": r = NovelTools.ReorderChapter(_session, p, args); mutatesProject = true; break;
                case "chapter_summary_set": r = NovelTools.SetChapterSummary(_session, p, args); mutatesProject = true; break;

                case "settings_set": r = NovelTools.SetSetting(_session, p, args); mutatesProject = true; break;
                case "settings_book_set": r = NovelTools.SetSettingsBook(_session, p, args); mutatesProject = true; break;

                case "snapshot_list": r = NovelTools.ListSnapshots(_session); break;
                case "snapshot_restore": r = NovelTools.RestoreSnapshot(_session, args); mutatesProject = true; break;

                case "characters_list": r = NovelTools.ListCharacters(p); break;
                case "character_upsert": r = NovelTools.UpsertCharacter(_session, p, args); mutatesProject = true; break;
                case "character_delete": r = NovelTools.DeleteCharacter(_session, p, args); mutatesProject = true; break;

                case "memory_get": r = NovelTools.GetMemory(p); break;
                case "memory_set": r = NovelTools.SetMemory(_session, p, args); mutatesProject = true; break;

                case "project_export": r = NovelTools.Export(p, args); break;

                case "ai_write":
                    r = await AiTools.WriteAsync(_session, p, args, progressToken);
                    mutatesProject = NovelTools.Bool(args, "writeBack", false);
                    break;

                default:
                    return ToolText($"未知工具「{name}」。用 tools/list 看有哪些工具。", true);
            }

            PublishToolDone(name, args, r, projectModified: mutatesProject && !r.IsError);
            return ToolText(r.Text, r.IsError);
        }
        catch (Exception ex)
        {
            Log($"工具 {name} 抛异常：{ex}");
            McpLiveBridge.Publish(new McpLiveEvent
            {
                EventType = "error",
                ToolName = name,
                ProjectPath = _session.Path,
                Summary = $"🤖 MCP 工具 {name} 出错：{ex.Message}",
            });
            return ToolText($"工具执行出错：{ex.Message}", true);
        }
    }

    private static void PublishToolStart(string name, JsonElement args)
    {
        int chNum = ResolveTargetChapterNumber(name, args, beforeExec: true);
        var task = name == "ai_write" ? NovelTools.Str(args, "task", "continue").Trim().ToLowerInvariant() : null;
        var summary = name switch
        {
            "project_create" => $"🤖 MCP 正在新建项目「{NovelTools.Str(args, "name", "新项目")}」…",
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
            ProjectPath = _session.Path,
            ChapterNumber = chNum,
            SettingField = ResolveSettingField(name, args, task),
            WriteBack = name == "ai_write" && NovelTools.Bool(args, "writeBack", false),
            Summary = summary,
        });
    }

    private static void PublishToolDone(string name, JsonElement args, ToolResult result, bool projectModified)
    {
        int chNum = ResolveTargetChapterNumber(name, args, beforeExec: false);
        var task = name == "ai_write" ? NovelTools.Str(args, "task", "continue").Trim().ToLowerInvariant() : null;
        var p = _session.Project;
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
            ProjectPath = _session.Path,
            ChapterNumber = chNum,
            SettingField = ResolveSettingField(name, args, task),
            ProjectModified = projectModified,
            WriteBack = name == "ai_write" && NovelTools.Bool(args, "writeBack", false),
            PreviewText = name == "ai_write" ? result.Text : null,
            Summary = summary,
        });
    }

    private static int ResolveTargetChapterNumber(string name, JsonElement args, bool beforeExec)
    {
        if (name == "chapter_reorder")
            return NovelTools.Int(args, "toNumber", NovelTools.Int(args, "fromNumber", 0));

        var n = NovelTools.Int(args, "number", 0);
        if (n > 0) return n;

        if (name == "chapter_create" && !beforeExec && _session.Project != null && _session.Project.Chapters.Count > 0)
            return _session.Project.Chapters.Max(c => c.ChapterNumber);

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
