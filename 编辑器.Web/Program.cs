using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using 编辑器;
using 编辑器.Mcp;
using 编辑器.Services;
using 编辑器.Web;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<WorkspaceService>();

// 提示词方案/技能配置与界面共用同一份文件。⚠ Store 必须在这里赋值——
// 之前只调了 Load，Store 本身是 null，用户在桌面版改过的提示词覆写
// 在网页版从来没生效过（AiPrompts.Resolve 全部回落到内置默认）。
var promptStore = new SystemPromptStore(ConfigDir());
try
{
    promptStore.Load();
    AiPrompts.Store = promptStore;
}
catch (Exception ex) { Console.Error.WriteLine($"[web] 载入提示词配置失败（将用内置默认）：{ex.Message}"); }

var app = builder.Build();

app.UseDefaultFiles();
// 前端无构建步骤、改完即刷新，所以静态文件必须每次向服务器校验新鲜度，
// 不许浏览器拿着旧 app.js 不放（刚在验证时踩过）。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache",
});

app.MapHub<LiveHub>("/hub/live");

// ══════════════════════════════════════════════════════════════════
// MCP over HTTP —— agent 走这里，与桌面端 stdio 共用同一份分发逻辑
// ══════════════════════════════════════════════════════════════════

// agent 的进度/阶段提示改成往网页广播（桌面端是写 stdout）
var hubProvider = app.Services.GetRequiredService<IHubContext<LiveHub>>();
McpServer.SetNotifier(o =>
{
    // 通知是 fire-and-forget，不能让它卡住生成流程
    _ = hubProvider.Clients.All.SendAsync("mcpProgress", o);
});

// ★ stdio 启动的 agent（Codex / Claude / Cursor…）事件**不走 HTTP**，而是走
//   McpLiveBridge 的命名管道——桌面端 MainWindow 一直在监听它，网页版此前完全
//   没接。后果就是：agent 用 stdio 写小说，网页界面上一片空白，用户"完全看不到
//   agent 的具体操作"。这里接上同一根管道（服务端允许多实例，可与桌面端并存），
//   把事件转成 SignalR 广播交给前端。
//   字段名在这里就定死成 camelCase，免得前端去猜 SignalR 的序列化策略。
_ = McpLiveBridge.StartServer(ev =>
{
    _ = hubProvider.Clients.All.SendAsync("mcpLive", new
    {
        eventType = ev.EventType,
        toolName = ev.ToolName,
        taskName = ev.TaskName,
        projectPath = ev.ProjectPath,
        chapterNumber = ev.ChapterNumber,
        settingField = ev.SettingField,
        summary = ev.Summary,
        previewText = ev.PreviewText,
        inputTokens = ev.InputTokens,
        outputTokens = ev.OutputTokens,
        projectModified = ev.ProjectModified,
        writeBack = ev.WriteBack,
        writeMode = ev.WriteMode,
    });
}, CancellationToken.None);

app.MapPost("/mcp", async (HttpContext ctx, WorkspaceService ws) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var line = await reader.ReadToEndAsync();

    // 网页上打开的书必须和 MCP 会话里的书是同一本，否则 agent 改 A、网页显示 B
    var current = ws.Current?.FilePath;
    if (current != null) McpServer.EnsureOpen(current);

    var before = ws.Stamp();
    var response = await McpServer.HandleRequestAsync(line);
    var after = ws.Stamp();

    // agent 改了书 → 让网页立刻同步
    if (before != after)
    {
        ws.Reload();
        await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(after) });
    }

    return response == null ? Results.NoContent() : Results.Json(response);
});

// ══════════════════════════════════════════════════════════════════
// 书目
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/books", (WorkspaceService ws) => Results.Ok(new { dir = ws.BooksDir, books = ws.ListBooks() }));

app.MapPost("/api/books", async (WorkspaceService ws, BookNameReq req) =>
{
    var meta = ws.CreateBook(req.Name ?? "未命名");
    await ws.BroadcastAsync("books-changed", new { });
    return Results.Ok(meta);
});

app.MapPost("/api/books/open", async (WorkspaceService ws, BookNameReq req) =>
{
    ws.OpenBook(req.Name ?? "");
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { name = ws.Current?.ProjectName, stamp = StampToken.From(ws.Stamp()) });
});

// ══════════════════════════════════════════════════════════════════
// 当前这本书
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/book", (WorkspaceService ws) =>
{
    var p = ws.Current;
    if (p == null) return Results.Ok(new { open = false });

    return Results.Ok(new
    {
        open = true,
        name = p.ProjectName,
        // 文件名要给出来：前端靠它判断「agent 正在操作的是不是另一本书」，
        // 是的话得跟着切过去（桌面端 SyncProjectFromDisk 干的就是这件事）
        fileName = Path.GetFileName(p.FilePath ?? ""),
        stamp = StampToken.From(ws.Stamp()),
        chapters = p.Chapters.OrderBy(c => c.ChapterNumber).Select(c => new
        {
            n = c.ChapterNumber,
            id = c.ChapterId,
            title = c.Title,
            words = c.WordCount,
            summary = c.Summary,
        }),
        outline = p.FullOutline,
        chapterOutline = p.ChapterOutline,
        characters = p.CharacterSettings,
        background = p.BackgroundSettings,
        style = p.WritingStyle,
        viewpoint = p.NarrativeViewpoint,
        world = p.WorldSetting == null ? null : new
        {
            worldName = p.WorldSetting.WorldName,
            timePeriod = p.WorldSetting.TimePeriod,
            location = p.WorldSetting.Location,
            background = p.WorldSetting.Background,
            magicSystem = p.WorldSetting.MagicSystem,
            technologyLevel = p.WorldSetting.TechnologyLevel,
        },
    });
});

app.MapGet("/api/chapter/{n:int}", (WorkspaceService ws, int n) =>
{
    var ch = ws.GetChapter(n);
    return ch == null
        ? Results.NotFound()
        : Results.Ok(new { n = ch.ChapterNumber, title = ch.Title, content = ch.Content, summary = ch.Summary });
});

app.MapPost("/api/chapter/{n:int}", async (WorkspaceService ws, int n, SaveChapterReq req) =>
{
    var ok = ws.SaveChapter(n, req.Content ?? "", StampToken.Parse(req.Stamp));
    if (!ok) return Results.Conflict(new { error = "这本书已经被别处改过了，请刷新后再保存" });

    await ws.BroadcastAsync("chapter-saved", new { n });
    return Results.Ok(new { ok = true, stamp = StampToken.From(ws.Stamp()) });
});

app.MapPost("/api/chapter", async (WorkspaceService ws, BookNameReq req) =>
{
    var ch = ws.AddChapter(req.Name);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { n = ch.ChapterNumber, title = ch.Title });
});

app.MapPost("/api/chapter/{n:int}/rename", async (WorkspaceService ws, int n, BookNameReq req) =>
{
    ws.RenameChapter(n, req.Name ?? "");
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/chapter/{n:int}/move", async (WorkspaceService ws, int n, MoveReq req) =>
{
    ws.MoveChapter(n, req.Delta);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/chapter/{n:int}", async (WorkspaceService ws, int n) =>
{
    ws.DeleteChapter(n);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

// ══════════════════════════════════════════════════════════════════
// 设定 / 统计 / 快照
// ══════════════════════════════════════════════════════════════════

app.MapPost("/api/settings", async (WorkspaceService ws, SettingsReq req) =>
{
    var p = ws.Current;
    if (p == null) return Results.BadRequest(new { error = "还没打开项目" });

    p.FullOutline = req.Outline ?? p.FullOutline;
    p.ChapterOutline = req.ChapterOutline ?? p.ChapterOutline;
    p.CharacterSettings = req.Characters ?? p.CharacterSettings;
    p.BackgroundSettings = req.Background ?? p.BackgroundSettings;
    p.WritingStyle = req.Style ?? p.WritingStyle;
    p.NarrativeViewpoint = req.Viewpoint ?? p.NarrativeViewpoint;
    p.Save();

    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/stats", (WorkspaceService ws) => Results.Ok(ws.Stats() ?? new { }));
app.MapGet("/api/snapshots", (WorkspaceService ws) => Results.Ok(ws.Snapshots()));

app.MapPost("/api/snapshot/restore", async (WorkspaceService ws, BookNameReq req) =>
{
    var ok = ws.RestoreSnapshot(req.Name ?? "");
    if (!ok) return Results.NotFound(new { error = "找不到这个快照" });

    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

// 全书查找 / 替换（桌面版 FindReplaceWindow 的"全书"范围）
app.MapGet("/api/find", (WorkspaceService ws, string? q, bool? caseSensitive) =>
    Results.Ok(new { chapters = ws.FindAll(q ?? "", caseSensitive == true) }));

app.MapPost("/api/replace-all", async (WorkspaceService ws, ReplaceAllReq req) =>
{
    var n = ws.ReplaceAll(req.Q ?? "", req.R ?? "", req.CaseSensitive == true);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true, replaced = n });
});

// 导出：直接复用桌面版那三个服务（Word / PDF / TXT），排版规则只有一份
app.MapGet("/api/export", (WorkspaceService ws, string? format) =>
{
    var p = ws.Current;
    if (p == null) return Results.BadRequest("还没打开项目");

    var f = (format ?? "docx").ToLowerInvariant();
    var (ext, mime) = f switch
    {
        "pdf" => (".pdf", "application/pdf"),
        "txt" => (".txt", "text/plain; charset=utf-8"),
        _ => (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
    };

    var safe = string.Concat(p.ProjectName.Split(Path.GetInvalidFileNameChars()));
    if (string.IsNullOrWhiteSpace(safe)) safe = "书稿";
    var tmp = Path.Combine(Path.GetTempPath(), $"{safe}-{Guid.NewGuid():N}{ext}");

    try
    {
        if (f == "pdf") PdfExportService.Export(tmp, p);
        else if (f == "txt") TxtExportService.Export(tmp, p);
        else WordExportService.Export(tmp, p);

        var bytes = File.ReadAllBytes(tmp);
        return Results.File(bytes, mime, safe + ext);
    }
    finally
    {
        try { File.Delete(tmp); } catch { }
    }
});

// ══════════════════════════════════════════════════════════════════
// 人物卡 / AI 写作记忆
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/characters", (WorkspaceService ws) => Results.Ok(new { characters = ws.Characters() }));

app.MapPost("/api/characters", async (WorkspaceService ws, Character c) =>
{
    ws.UpsertCharacter(c);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/characters/{id}", async (WorkspaceService ws, string id) =>
{
    ws.DeleteCharacter(id);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/memory", (WorkspaceService ws) => Results.Ok(new { text = ws.Memory() }));

app.MapPost("/api/memory", async (WorkspaceService ws, BookNameReq req) =>
{
    ws.SetMemory(req.Name ?? "");
    await ws.BroadcastAsync("memory-changed", new { });
    return Results.Ok(new { ok = true });
});

// ══════════════════════════════════════════════════════════════════
// 万能聊天的对话记忆（与桌面端同一份 <项目目录>/.chat/session.json）
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/chat", (WorkspaceService ws) =>
    Results.Ok(ws.ChatInfo(LoadApiConfig()?.Model ?? "")));

app.MapPost("/api/chat/clear", (WorkspaceService ws) =>
    Results.Ok(new { ok = ws.ClearChat() }));

// ══════════════════════════════════════════════════════════════════
// AI 生成（SSE 流式，前端能看着字一个一个出来）
// ══════════════════════════════════════════════════════════════════

app.MapPost("/api/ai", async (HttpContext ctx, WorkspaceService ws) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AiReq>()
              ?? new AiReq(null, null, 0, null, null, null, null, null, null, null);

    var cfg = LoadApiConfig();
    if (cfg == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("还没配置 AI 服务商。点右上角「AI 设置」配好 Key 再来。");
        return;
    }

    var service = ApiProviders.CreateService(cfg);
    var p = ws.Current;
    var task = req.Task ?? "continue";

    // 生成前先拍快照——与桌面端同一批时机（续写前/润色前/人名生成前/万能写作前）。
    // 前端在调用前会 flushSave，所以快照里包含用户刚敲的字。
    if (task is "continue" or "polish" or "name" or "chat")
        ws.TakeSnapshot(task switch
        {
            "polish" => "润色前备份",
            "name" => "人名生成前备份",
            "chat" => "万能写作前备份",
            _ => "续写前备份",
        });

    // user prompt 在服务器侧构造，与桌面端 MainWindow 的各生成入口逐条同源
    var built = BuildAiPrompt(task, p, req.TargetChapter, req.Prompt ?? "",
                              req.PolishStyle, req.ScaleHint);

    var skill = ResolveSkill(req.SkillId);
    var (taskText, contract) = ResolveTask(built.TaskKey, skill, req.OutputContract);

    // 与桌面版同源：同一个 PromptContextBuilder（设定集/文献库/参考章节全部生效）
    //
    // ★ 审稿是唯一把设定集挪到 **user 侧** 的任务（桌面端 ReviewChapter_Click
    //   传 includeSettingsBook:false）：要让「比对基准」紧挨着「待审正文」，
    //   模型照着逐条核对；system 里再放一份只是重复占位。
    var system = PromptContextBuilder.BuildSystemPrompt(
        p, taskText, ws.Memory(), req.RelatedChapterIds, contract,
        includeSettingsBook: task != "review");

    // 素材构造全部在 BuildAiPrompt 里、与桌面端逐条同源，这里不再另拼一份
    var prompt = built.UserPrompt;

    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    async Task Send(string type, object payload)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type, payload })}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    try
    {
        var (maxTokens, temperature) = TuningFor(task, p, req.TargetChapter);

        // ── 万能聊天的对话记忆放在服务器侧 ──
        // 与桌面端共用同一个 ChatSessionStore 和同一份 <项目目录>/.chat/session.json：
        // 刷新页面不丢、换个设备打开还在，长对话也有压缩兜底。
        // （此前是前端自己扛一个数组，刷新即失忆、也没有任何预算控制。）
        IReadOnlyList<ChatMessage>? history;
        if (task == "chat" && ws.Chat != null)
        {
            var compactor = new ChatContextCompactor(
                ws.Chat,
                () => cfg.Model,
                new CompactPolicyConfig { MaxOutputTokens = ChatContextCompactor.DefaultMaxOutputTokens })
            {
                OnStatus = msg => { _ = Send("notice", msg); },
            };

            // 发之前过一遍上下文预算：装不下就先本地省略旧回复（免费），
            // 再把更早的对话交给模型写成摘要。摘要写回了 store，
            // 所以摘要块必须在**这一步之后**才拼进 system，否则用的是旧摘要。
            var outcome = await compactor.EnsureBudgetAsync(
                system.Flatten(), prompt,
                (pr, sy, op, _) => service.CompleteTextAsync(pr, sy, op),
                ctx.RequestAborted);

            history = outcome.History;

            var summaryBlock = ws.Chat.BuildSummaryBlock();
            if (!string.IsNullOrWhiteSpace(summaryBlock))
                system = system.Append(summaryBlock, cacheable: false);
        }
        else
        {
            history = req.History?.Select(m => new ChatMessage(
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                m.Content ?? "")).ToList();
        }

        var result = await service.CompleteTextAsync(prompt, system, new CompletionOptions
        {
            MaxTokens = req.MaxTokens > 0 ? req.MaxTokens : maxTokens,
            Temperature = temperature,
            Model = cfg.Model,
            CancellationToken = ctx.RequestAborted,   // 用户点「停止」→ 前端 abort → 这里断
            OnNotice = msg => { _ = Send("notice", msg); },
            OnProgress = (inTok, outTok) => { _ = Send("progress", new { inTok, outTok }); },
            OnStreamText = text => { _ = Send("text", text); },
        }, history);

        // 记进历史的必须是**用户的原始输入**，不是包了当前章正文的 prompt：
        // 正文每轮都会重新带一份，历史里再存一份等于白烧一遍 token，
        // 而且章节改过之后，历史里那份旧正文还会跟新正文打架（桌面版同样的取舍）。
        if (task == "chat" && result.IsUsable && ws.Chat != null)
        {
            ws.Chat.Add(req.Prompt ?? "", result.Text);
            await Send("chatinfo", ws.ChatInfo(cfg.Model));
        }

        // 上下文类生成（大纲/人物/背景/文风/视角）在桌面端是**自动写回设定**的，
        // 这里保持一致；续写/润色改的是正文缓冲区，由用户在网页上确认后追加。
        string? applied = null;
        if (result.IsUsable && built.ApplyField != null && p != null)
        {
            ApplySettingField(p, built.ApplyField, result.Text);
            p.Save();
            applied = built.ApplyLabel;
            await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
        }

        await Send("done", new { text = result.Text, ok = result.IsUsable, applied });
    }
    catch (OperationCanceledException)
    {
        // 用户主动停止：已经流出去的部分留在前端，不再补发
        try { await Send("stopped", new { }); } catch { /* 连接已断，忽略 */ }
    }
    catch (Exception ex)
    {
        try { await Send("error", ex.Message); } catch { /* 连接已断，忽略 */ }
    }
});

// ── 生成参数（与桌面端各生成方法的 MaxTokens / Temperature 对齐）──

static (int MaxTokens, double Temperature) TuningFor(string task, NovelProject? p, int? targetChapter)
{
    var content = targetChapter == null
        ? ""
        : p?.Chapters.FirstOrDefault(c => c.ChapterNumber == targetChapter.Value)?.Content ?? "";
    return task switch
    {
        "continue" => (16384, 0.7),
        "polish" => (CompletionOptions.BudgetForRewrite(content), 0.7),
        "outline" => (8192, 0.5),
        "chapterOutline" => (8192, 0.5),
        "character" => (8192, 0.5),
        "background" => (8192, 0.4),
        "style" => (8192, 0.5),
        "viewpoint" => (4096, 0.4),
        "chat" => (16384, 0.7),
        "name" => (8192, 0.7),
        // 审稿要稳、不许发挥：与桌面端 ReviewChapter_Click 同一组数值。
        // 少给 token 会把问题清单截在半句上——那是这份报告唯一的价值所在。
        "review" => (16384, 0.3),
        _ => (4096, 0.7),
    };
}

/// <summary>
/// 与桌面端同源的 user prompt 构造。返回要用的任务键（上下文类在"已有内容非空"时
/// 改用 Expand 提示词）、拼好的 user prompt，以及生成成功后的自动落点。
/// </summary>
/// <summary>
/// 审稿基准：设定集里描述「事实」的章 + 五项贯穿设定。
/// 与桌面端 <c>ReviewSourceKeys</c> 同一份名单——审的是「和设定对不对得上」，
/// 所以只有描写既有事实的章才算基准，剧情梗概那类章不该拿来当尺子。
/// </summary>
static string BuildReviewFacts(NovelProject p)
{
    SettingsBookTemplates.EnsureBook(p);
    var book = p.SettingsBook;
    var sb = new StringBuilder();
    string[] keys = { "characters", "relations", "glossary", "history", "world", "power", "foreshadow" };
    foreach (var key in keys)
    {
        var c = book?.Chapters.FirstOrDefault(x => x.SourceKey == key && x.IncludeInExport);
        if (c == null || string.IsNullOrWhiteSpace(c.Content)) continue;
        sb.AppendLine("### " + c.Title).AppendLine(c.Content.Trim()).AppendLine();
    }
    if (!string.IsNullOrWhiteSpace(p.BackgroundSettings))
        sb.AppendLine("### 背景设定").AppendLine(p.BackgroundSettings.Trim());
    var chars = p.BuildEffectiveCharacterSettings();
    if (!string.IsNullOrWhiteSpace(chars))
        sb.AppendLine("### 人物设定").AppendLine(chars);
    return sb.ToString();
}

static (string TaskKey, string UserPrompt, string? ApplyField, string? ApplyLabel) BuildAiPrompt(
    string task, NovelProject? p, int? targetChapter, string requirement, string? polishStyle, string? scaleHint)
{
    var chapter = targetChapter == null
        ? null
        : p?.Chapters.FirstOrDefault(c => c.ChapterNumber == targetChapter.Value);
    var chapterLabel = chapter == null ? "" : $"（第{chapter.ChapterNumber}章 {chapter.Title}）";
    var chapterContent = chapter?.Content ?? "";
    var input = requirement.Trim();

    switch (task)
    {
        case "continue":
        {
            var user = AiPrompts.Section($"待续写的正文{chapterLabel}", chapterContent);
            var prior = p == null || chapter == null ? "" : p.BuildPriorChapterBrief(chapter.ChapterNumber, true);
            if (!string.IsNullOrWhiteSpace(prior))
                user += "\n" + AiPrompts.Section("前情梗概与上章末尾（供衔接）", prior);
            if (input.Length > 0) user += $"\n{input}";
            return ("continue", user, null, null);
        }
        case "review":
        {
            // 一致性审稿：与桌面端 ReviewChapter_Click 同源——
            // 素材 = 待审章节 + 设定集里描述「事实」的章 + 五项贯穿设定 + 前情梗概。
            // 只读分析：报告不写回正文，只把矛盾点摆出来给人自己改。
            if (p == null || chapter == null) return ("review", "", null, null);
            var user = AiPrompts.Section("待审章节",
                $"第{chapter.ChapterNumber}章 {chapter.Title}\n{chapter.Content}");
            var facts = BuildReviewFacts(p);
            user += "\n" + AiPrompts.Section("设定集与项目设定（比对基准）", facts.Length > 0
                ? facts
                : "（作者还没有维护设定集。只做前后文与常识层面的检查，涉及设定一致性的结论一律标「存疑」。）");
            var prior = p.BuildPriorChapterBrief(chapter.ChapterNumber, true);
            if (!string.IsNullOrWhiteSpace(prior))
                user += "\n" + AiPrompts.Section("前情梗概与上章末尾（供跨章比对）", prior);
            return ("review", user, null, null);
        }

        case "polish":
        {
            var style = string.IsNullOrWhiteSpace(polishStyle) ? null : polishStyle!.Trim();
            string? combined = style != null && input.Length > 0 ? $"{style}，{input}"
                            : style ?? (input.Length > 0 ? input : null);
            var user = AiPrompts.Section($"待润色的正文{chapterLabel}", chapterContent);
            user += combined == null ? "\n请润色这段正文。" : $"\n请按以下要求润色：{combined}";
            return ("polish", user, null, null);
        }

        case "outline":
        {
            var existing = (p?.FullOutline ?? "").Trim();
            if (existing.Length > 0)
            {
                var u = AiPrompts.Section("已有的全文大纲", existing);
                if (input.Length > 0) u += $"\n修改要求：{input}";
                return ("expand", u, "FullOutline", "全文大纲");
            }
            var s = "";
            if (chapter != null && !string.IsNullOrWhiteSpace(chapterContent))
                s += AiPrompts.Section("当前章节内容", chapterContent) + "\n";
            if (!string.IsNullOrWhiteSpace(scaleHint))
                s += $"小说规模：{scaleHint}\n请据此合理规划章节数量、情节复杂度与人物数量。\n\n";
            s += "请为这部小说生成一份详细的全文大纲。";
            if (input.Length > 0) s += $"\n额外要求：{input}";
            return ("outline", s, "FullOutline", "全文大纲");
        }

        case "chapterOutline":
        {
            var existing = (p?.ChapterOutline ?? "").Trim();
            if (existing.Length > 0)
            {
                var u = AiPrompts.Section("已有的章节大纲", existing);
                if (input.Length > 0) u += $"\n修改要求：{input}";
                return ("expand", u, "ChapterOutline", "章节大纲");
            }
            var s = "";
            if (chapter != null && !string.IsNullOrWhiteSpace(chapterContent))
                s += AiPrompts.Section($"当前章节{chapterLabel}", chapterContent) + "\n";
            s += "请为这部小说生成详细的章节大纲。";
            if (input.Length > 0) s += $"\n额外要求：{input}";
            return ("chapterOutline", s, "ChapterOutline", "章节大纲");
        }

        case "character":
        {
            var existing = (p?.CharacterSettings ?? "").Trim();
            if (existing.Length > 0)
            {
                var u = AiPrompts.Section("已有的人物设定", existing);
                if (input.Length > 0) u += $"\n修改要求：{input}";
                return ("expand", u, "CharacterSettings", "人物设定");
            }
            var s = "请为这部小说生成主要角色设定。";
            if (input.Length > 0) s += $"\n额外要求：{input}";
            return ("character", s, "CharacterSettings", "人物设定");
        }

        case "background":
        {
            var existing = (p?.BackgroundSettings ?? "").Trim();
            if (existing.Length > 0)
            {
                var u = AiPrompts.Section("已有的背景设定", existing);
                if (input.Length > 0) u += $"\n修改要求：{input}";
                return ("expand", u, "BackgroundSettings", "背景设定");
            }
            var s = "请为这部小说生成世界观和背景设定。";
            if (input.Length > 0) s += $"\n额外要求：{input}";
            return ("background", s, "BackgroundSettings", "背景设定");
        }

        case "style":
        {
            var existing = (p?.WritingStyle ?? "").Trim();
            if (existing.Length > 0)
            {
                var u = AiPrompts.Section("已有的文风设定", existing);
                if (input.Length > 0) u += $"\n修改要求：{input}";
                return ("expand", u, "WritingStyle", "文风设定");
            }
            var s = "";
            if (chapter != null && !string.IsNullOrWhiteSpace(chapterContent))
                s += AiPrompts.Section("作者已有的文字（风格样本）", chapterContent) + "\n";
            s += "请据此提炼这部小说的文风设定。";
            if (input.Length > 0) s += $"\n额外要求：{input}";
            return ("style", s, "WritingStyle", "文风设定");
        }

        case "viewpoint":
        {
            var existing = (p?.NarrativeViewpoint ?? "").Trim();
            if (existing.Length > 0)
            {
                var u = AiPrompts.Section("已有的叙事视角声明", existing);
                if (input.Length > 0) u += $"\n修改要求：{input}";
                return ("expand", u, "NarrativeViewpoint", "叙事视角");
            }
            var s = "请为这部作品拟定「叙事视角」声明，格式如："
                + "「第三人称限知·跟随主角〈姓名〉（个别回忆段切主角视角）」。"
                + "要有明确的：人称、限知/全知、跟随对象；有例外场景就括注说明。";
            if (chapter != null && !string.IsNullOrWhiteSpace(chapterContent))
                s += "\n" + AiPrompts.Section("作者已有的文字（判断实际视角的依据）", chapterContent);
            var style = (p?.WritingStyle ?? "").Trim();
            if (style.Length > 0)
                s += "\n" + AiPrompts.Section("文风设定（其中的视角描述优先采纳）", style);
            if (input.Length > 0) s += $"\n额外要求：{input}";
            return ("viewpoint", s, "NarrativeViewpoint", "叙事视角");
        }

        case "name":
        {
            var s = input.Length == 0
                ? "请为这部小说生成一批角色名字。"
                : $"请为这部小说生成一批角色名字。要求：{input}";
            return ("name", s, null, null);
        }

        case "chat":
        {
            var s = chapter != null && !string.IsNullOrWhiteSpace(chapterContent)
                ? AiPrompts.Section($"当前章节{chapterLabel}", chapterContent) + "\n" + input
                : input;
            return ("chat", s, null, null);
        }

        default:
            return (task, input, null, null);   // expand / review …
    }
}

/// <summary>把生成结果写回某一项设定（与桌面版 SetSettingValue 同一份字段表）。</summary>
static void ApplySettingField(NovelProject p, string field, string text)
{
    switch (field)
    {
        case "FullOutline": p.FullOutline = text; break;
        case "ChapterOutline": p.ChapterOutline = text; break;
        case "CharacterSettings": p.CharacterSettings = text; break;
        case "BackgroundSettings": p.BackgroundSettings = text; break;
        case "WritingStyle": p.WritingStyle = text; break;
        case "NarrativeViewpoint": p.NarrativeViewpoint = text; break;
    }
}

// ── 任务解析：与桌面版 MainWindow.ResolveTaskText / ResolveContractText 同一套规则 ──

static NovelSkill? ResolveSkill(string? skillId)
{
    if (string.IsNullOrEmpty(skillId)) return null;
    try
    {
        return NovelSkillStore.Load(ConfigDir()).FirstOrDefault(s => s.Id == skillId);
    }
    catch { return null; }
}

static (string task, string? contract) ResolveTask(string? key, NovelSkill? skill, string? overrideContract)
{
    var k = key ?? "continue";
    return k switch
    {
        "polish" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Polish, AiPrompts.Task.Polish),
                     overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Polish, null)),
        "expand" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Expand, AiPrompts.Task.Expand),
                     overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Expand, null)),
        "name" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Name, AiPrompts.Task.Name),
                   overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Name, AiPrompts.StructuredOutput)),
        "character" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Character, AiPrompts.Task.Character),
                        overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Character, AiPrompts.StructuredOutput)),
        "outline" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Outline, AiPrompts.Task.Outline),
                      overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Outline, AiPrompts.StructuredOutput)),
        "background" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Background, AiPrompts.Task.Background),
                         overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Background, AiPrompts.StructuredOutput)),
        "chapterOutline" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.ChapterOutline, AiPrompts.Task.ChapterOutline),
                             overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.ChapterOutline, AiPrompts.StructuredOutput)),
        "style" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.WriteStyle, AiPrompts.Task.WriteStyle),
                    overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.WriteStyle, AiPrompts.StructuredOutput)),
        "review" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Review, AiPrompts.Task.Review),
                     overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Review, AiPrompts.StructuredOutput)),
        "chat" => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Chat, AiPrompts.Task.Chat),
                   overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Chat, AiPrompts.StructuredOutput)),
        // 默认按续写处理（与桌面版一致：续写/润色用创作类契约，BuildSections 里兜底）
        _ => (NovelSkillStore.ResolveTaskPrompt(skill, AiPrompts.Keys.Continue, AiPrompts.Task.Continue),
              overrideContract ?? NovelSkillStore.ResolveContract(skill, AiPrompts.Keys.Continue, null)),
    };
}

// ── API 方案管理（与桌面版共用 api_profiles.json）──

app.MapGet("/api/ai/profiles", () =>
{
    var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
    mgr.Load();
    return Results.Ok(new
    {
        active = mgr.ActiveProfileName,
        names = mgr.GetProfileNames(),
        profiles = mgr.Profiles.Select(kv => new
        {
            name = kv.Key,
            provider = kv.Value.Provider,
            apiUrl = kv.Value.ApiUrl,
            model = kv.Value.Model,
            // 认证方式要回显：不然用户改过一次后界面永远显示「自动」，
            // 看着像没保存住
            authOverride = kv.Value.AuthOverride,
            hasKey = !string.IsNullOrWhiteSpace(kv.Value.ApiKey),
        }),
    });
});

// 新建方案。可以给一个「蓝本」from——新建多半是"同一家换个模型"，
// 所以连 Key 一起复制过去，不让人重粘一遍。
app.MapPost("/api/ai/profiles/new", (ProfileNewReq req) =>
{
    var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
    mgr.Load();

    var name = (req.Name ?? "").Trim();
    if (name.Length == 0) return Results.BadRequest(new { error = "方案名不能为空" });
    if (mgr.Profiles.ContainsKey(name))
        return Results.BadRequest(new { error = $"方案「{name}」已经存在了" });

    // 蓝本只作兜底：请求里带了哪个字段就用哪个，没带的才从蓝本继承。
    // 这样同一个端点既能做「+ 新建」（只给 from，整份复制），
    // 也能做「另存为」（from 提供 Key，其余字段按表单里改过的走）。
    var from = (req.From ?? "").Trim();
    var basis = from.Length > 0 && mgr.Profiles.TryGetValue(from, out var b) ? b : null;

    var cfg = new ApiConfig
    {
        Provider = req.Provider ?? basis?.Provider ?? ApiProviders.IdOpenAi,
        ApiUrl = req.ApiUrl ?? basis?.ApiUrl ?? "",
        Model = req.Model ?? basis?.Model ?? "",
        AuthOverride = req.AuthOverride ?? basis?.AuthOverride ?? "",
        // ★ Key 只能从蓝本带过来。表单里从来不回显明文 Key（输入框留空即表示
        //   「保留原来的」），所以新建/另存为不走这一步就会把 Key 悄悄弄丢，
        //   表现为「方案建出来了，一调用就 401」。
        ApiKey = req.ApiKey ?? basis?.ApiKey ?? "",
    };

    mgr.AddOrUpdate(name, cfg);
    if (req.Activate == true) mgr.SetActive(name);
    mgr.Save();
    return Results.Ok(new { ok = true, name });
});

// 保存（新增或覆盖）一个方案。Key 传空字符串表示清空；不传（null）表示保留原 Key。
app.MapPost("/api/ai/profiles/save", async (WorkspaceService ws, ProfileSaveReq req) =>
{
    var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
    mgr.Load();

    var name = (req.Name ?? "").Trim();
    if (name.Length == 0) return Results.BadRequest(new { error = "方案名不能为空" });

    var cfg = new ApiConfig
    {
        Provider = req.Provider ?? ApiProviders.IdOpenAi,
        ApiUrl = req.ApiUrl ?? "",
        Model = req.Model ?? "",
        AuthOverride = req.AuthOverride ?? "",
        ApiKey = req.ApiKey ?? "",
    };
    if (req.ApiKey == null)
    {
        // 不传 Key 就保留旧值——编辑方案时表单里没有明文 Key 回显
        var old = mgr.Profiles.TryGetValue(name, out var o) ? o : null;
        cfg.ApiKey = old?.ApiKey ?? "";
    }

    mgr.AddOrUpdate(name, cfg);
    if (req.Activate == true) mgr.SetActive(name);
    mgr.Save();   // 一次落盘同时覆盖"保存"与"激活"两个动作
    await Task.CompletedTask;
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/ai/profiles/active", (NameReq req) =>
{
    var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
    mgr.Load();
    if (!mgr.Profiles.ContainsKey(req.Name ?? "")) return Results.NotFound(new { error = "没有这个方案" });
    mgr.SetActive(req.Name!);
    mgr.Save();
    return Results.Ok(new { ok = true, active = mgr.ActiveProfileName });
});

app.MapPost("/api/ai/profiles/delete", (NameReq req) =>
{
    var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
    mgr.Load();

    // 与桌面端一致：至少留一个。删光之后界面上就再也没有可编辑的方案了，
    // 用户只能去改 JSON——那是把人往坑里推。
    if (mgr.Profiles.Count <= 1)
        return Results.BadRequest(new { error = "至少保留一个配置方案" });

    return mgr.Delete(req.Name ?? "")
        ? Results.Ok(new { ok = true })
        : Results.NotFound(new { error = "没有这个方案" });
});

// 服务商预置（协议/地址/常用模型一次填好）
app.MapGet("/api/ai/providers", () => Results.Ok(new
{
    providers = ApiProviders.All.Select(p => new
    {
        id = p.Id,
        name = p.Name,
        group = p.Group,
        endpoint = p.Endpoint,
        defaultModel = p.DefaultModel,
        models = p.Models,
        consoleUrl = p.ConsoleUrl,
        note = p.Note,
        // 这家服务商推荐哪种认证头。填错头不会报「认证方式不对」，
        // 只会返回 401/403，用户根本看不出是头的问题——所以要能选。
        auth = p.Auth.ToString(),
        authLabel = p.AuthLabel,
        wire = p.WireLabel,
        isCustom = p.IsCustom,
    }),
    // 「自动」永远排第一：绝大多数情况下不用管，只有接中转站时才要手选
    authChoices = new object[]
    {
        new { value = "", label = "自动（推荐）" },
        new { value = nameof(ApiAuth.Bearer), label = ApiProviders.AuthLabel(ApiAuth.Bearer) },
        new { value = nameof(ApiAuth.XApiKey), label = ApiProviders.AuthLabel(ApiAuth.XApiKey) },
        new { value = nameof(ApiAuth.ApiKeyHeader), label = ApiProviders.AuthLabel(ApiAuth.ApiKeyHeader) },
    },
}));

// 角色出场统计：与桌面端「角色出场」按钮同源（CharacterAppearanceService，
// 纯本地扫描，不花 token）。stats 给界面列表用，report 是可复制的文本报告——
// 同一个数据源两种形态，不再另开一个端点各算一遍。
app.MapGet("/api/character-appearances", (WorkspaceService ws) =>
{
    var (title, report, count) = ws.CharacterAppearanceReport();
    return Results.Ok(new { stats = ws.CharacterAppearances(), title, report, count });
});

// ── MCP 实时跟随（与桌面端 settings.json 同一个文件、同一个键）──
// 关掉之后，agent 通过 MCP 改稿时界面不再自动切章/刷新，避免正写着被打断。
app.MapGet("/api/settings/mcp-live-sync", () =>
{
    var path = Path.Combine(ConfigDir(), "settings.json");
    return Results.Ok(new { enabled = McpLiveBridge.LoadLiveSyncEnabled(path), path });
});

app.MapPost("/api/settings/mcp-live-sync", (EnabledReq req) =>
{
    var path = Path.Combine(ConfigDir(), "settings.json");
    McpLiveBridge.SaveLiveSyncEnabled(req.Enabled ?? true, path);
    return Results.Ok(new { ok = true, enabled = req.Enabled ?? true });
});

// 从服务商现拉真实模型名（与桌面版 ApiSettingsWindow.FetchModelsAsync 同一个 ModelCatalog）。
// 拉回来的清单只用于填下拉，不缓存——缓存文件在桌面端那边管。
app.MapPost("/api/ai/models", async (ProfileSaveReq req) =>
{
    var config = new ApiConfig
    {
        Provider = req.Provider ?? ApiProviders.IdOpenAi,
        ApiUrl = req.ApiUrl ?? "",
        Model = req.Model ?? "",
        AuthOverride = req.AuthOverride ?? "",
        ApiKey = req.ApiKey ?? "",
    };
    // 表单里没有明文 Key 时，借用已保存方案里的 Key（本地地址不鉴权，允许无 Key）
    if (string.IsNullOrWhiteSpace(config.ApiKey))
    {
        var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
        mgr.Load();
        config.ApiKey = mgr.Profiles.TryGetValue(req.Name ?? "", out var saved) ? saved.ApiKey : "";
    }

    try
    {
        var result = await ModelCatalog.FetchAsync(config);
        return Results.Ok(new
        {
            ok = result.Ok,
            url = result.Url,
            error = result.Error,
            nonChat = result.Ok ? ModelCatalog.NonChatCount(result.Models) : 0,
            models = ModelCatalog.SortForDisplay(result.Models)
                .Select(m => new { id = m.Id, displayName = m.DisplayName, isChatLike = m.IsChatLike }),
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message, models = Array.Empty<object>() });
    }
});

// 配置文件的原始 JSON（桌面版 API 设置里的「JSON 编辑」Tab）：直接读写 api_profiles.json，
// 保存前先试着 Load 一次，写坏了不让落盘。
app.MapGet("/api/ai/profiles/raw", () =>
{
    var path = Path.Combine(ConfigDir(), "api_profiles.json");
    string text;
    if (File.Exists(path))
    {
        text = File.ReadAllText(path);
    }
    else
    {
        // 文件还不存在（用户从未保存过方案）：给一份与 ApiProfileManager 首次保存
        // 形状一致的骨架，否则 JSON Tab 是一片空白、连"格式化"都点不动。
        text = JsonSerializer.Serialize(new
        {
            ActiveProfile = "默认配置",
            Profiles = new Dictionary<string, ApiConfig> { ["默认配置"] = new ApiConfig() },
        }, new JsonSerializerOptions { WriteIndented = true });
    }
    return Results.Ok(new { path, text, exists = File.Exists(path) });
});

app.MapPost("/api/ai/profiles/raw", (NameReq req) =>
{
    var path = Path.Combine(ConfigDir(), "api_profiles.json");
    var text = req.Name ?? "";

    // 严格校验：⚠ 不能拿 ApiProfileManager.Load() 当校验——它内部 try/catch 吞掉
    // 解析异常并回落到默认配置，坏 JSON 照样能"加载成功"（踩过）。
    try
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "JSON 根节点必须是对象" });

        if (doc.RootElement.TryGetProperty("Profiles", out var profiles)
            && profiles.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            return Results.BadRequest(new { error = "Profiles 必须是对象" });

        if (doc.RootElement.TryGetProperty("ActiveProfile", out var active)
            && active.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
            return Results.BadRequest(new { error = "ActiveProfile 必须是字符串" });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "JSON 不合法：" + ex.Message });
    }

    Directory.CreateDirectory(ConfigDir());
    File.WriteAllText(path, text);
    return Results.Ok(new { ok = true });
});

// 测试连接：认证头走与真实调用同一份逻辑（防"测试成功、调用 401"的假阳性）
app.MapPost("/api/ai/profiles/test", async (ProfileSaveReq req) =>
{
    var config = new ApiConfig
    {
        Provider = req.Provider ?? ApiProviders.IdOpenAi,
        ApiUrl = req.ApiUrl ?? "",
        Model = req.Model ?? "",
        AuthOverride = req.AuthOverride ?? "",
        ApiKey = req.ApiKey ?? "",
    };
    if (string.IsNullOrWhiteSpace(config.ApiKey))
        return Results.Ok(new { ok = false, message = "请输入 API Key" });

    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        ApiProviders.ApplyHeaders(client.DefaultRequestHeaders, config);
        ApiProviders.ApplyExtraHeaders(client.DefaultRequestHeaders, config);

        object body = ApiProviders.ResolveWire(config) == ApiWire.AnthropicMessages
            ? new { model = config.Model, max_tokens = 10, messages = new[] { new { role = "user", content = "Hello" } } }
            : new { model = config.Model, messages = new[] { new { role = "user", content = "Hello" } }, max_tokens = 10 };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(ApiProviders.ResolveEndpoint(config), content);

        if (response.IsSuccessStatusCode)
            return Results.Ok(new { ok = true, message = $"连接成功（{response.StatusCode}）" });

        var err = await response.Content.ReadAsStringAsync();
        return Results.Ok(new { ok = false, message = $"HTTP {(int)response.StatusCode}：{Truncate(err, 200)}" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, message = ex.Message });
    }
});

static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

// ── 技能列表（内置 5 个 + 用户自定义，存 skills.json）──

app.MapGet("/api/skills", () =>
{
    try
    {
        var skills = NovelSkillStore.Load(ConfigDir());
        return Results.Ok(new
        {
            skills = skills.Select(s => new
            {
                id = s.Id, name = s.Name, description = s.Description,
                isBuiltIn = s.IsBuiltIn, appliesTo = s.AppliesTo, inputHint = s.InputHint,
                presets = s.Presets, taskPrompt = s.TaskPrompt, outputContract = s.OutputContract,
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { skills = Array.Empty<object>(), error = ex.Message });
    }
});

// 保存自定义技能。内置 id 一律拒绝（与桌面版 AddOrUpdate 同一语义）。
app.MapPost("/api/skills/save", (SkillSaveReq req) =>
{
    try
    {
        var all = NovelSkillStore.Load(ConfigDir());
        var skill = new NovelSkill
        {
            Id = string.IsNullOrWhiteSpace(req.Id) ? "skill-" + Guid.NewGuid().ToString("N")[..8] : req.Id!,
            Name = req.Name ?? "",
            Description = req.Description ?? "",
            AppliesTo = req.AppliesTo ?? new List<string>(),
            Presets = req.Presets ?? new List<string>(),
            TaskPrompt = req.TaskPrompt ?? "",
            OutputContract = req.OutputContract,
            InputHint = req.InputHint ?? "",
        };
        if (string.IsNullOrWhiteSpace(skill.Name))
            return Results.BadRequest(new { error = "技能名不能为空" });

        if (!NovelSkillStore.AddOrUpdate(all, skill))
            return Results.BadRequest(new { error = "内置技能不允许修改" });

        NovelSkillStore.Save(ConfigDir(), all);
        return Results.Ok(new { ok = true, id = skill.Id });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/skills/{id}/delete", (string id) =>
{
    try
    {
        var all = NovelSkillStore.Load(ConfigDir());
        if (!NovelSkillStore.Delete(all, id))
            return Results.BadRequest(new { error = "删除失败（不存在或为内置技能）" });
        NovelSkillStore.Save(ConfigDir(), all);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 导入：JSON 文本（单技能或数组），内置 id 拒绝导入
app.MapPost("/api/skills/import", (SkillImportReq req) =>
{
    try
    {
        var incoming = NovelSkillStore.ParseImport(req.Json ?? "");
        if (incoming.Count == 0) return Results.BadRequest(new { error = "没有可导入的技能" });

        var all = NovelSkillStore.Load(ConfigDir());
        int added = 0;
        foreach (var s in incoming)
            if (NovelSkillStore.AddOrUpdate(all, s)) added++;

        NovelSkillStore.Save(ConfigDir(), all);
        return Results.Ok(new { ok = true, imported = added });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/skills/{id}/export", (string id) =>
{
    var all = NovelSkillStore.Load(ConfigDir());
    var skill = all.FirstOrDefault(s => s.Id == id);
    if (skill == null) return Results.NotFound(new { error = "技能不存在" });
    return Results.Text(NovelSkillStore.ExportJson(skill), "application/json");
});

// ── 润色风格预设（与桌面版共用 polish_presets.json，文件格式就是 JSON 字符串数组）──

string[] defaultPolishPresets =
    { "正式严谨", "简洁干练", "优美文学", "口语化", "古风雅韵", "幽默风趣" };

app.MapGet("/api/polish-presets", () =>
{
    var path = Path.Combine(ConfigDir(), "polish_presets.json");
    List<string> list;
    try
    {
        list = File.Exists(path)
            ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new()
            : new List<string>();
    }
    catch { list = new List<string>(); }

    if (list.Count == 0)
    {
        // 首次访问：落一份默认清单，与桌面版 LoadPolishPresets 的首次行为一致
        list = defaultPolishPresets.ToList();
        try { Directory.CreateDirectory(ConfigDir()); File.WriteAllText(path, JsonSerializer.Serialize(list)); }
        catch { }
    }
    return Results.Ok(new { presets = list, defaults = defaultPolishPresets });
});

// 新增一条（重名忽略）或删除一条（内置预设不可删，与桌面版 RemovePolishPreset_Click 一致）
app.MapPost("/api/polish-presets", (PolishPresetReq req) =>
{
    var path = Path.Combine(ConfigDir(), "polish_presets.json");
    var name = (req.Name ?? "").Trim();
    if (name.Length == 0) return Results.BadRequest(new { error = "名称不能为空" });

    List<string> list;
    try
    {
        list = File.Exists(path)
            ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new()
            : new List<string>();
    }
    catch { list = new List<string>(); }
    if (list.Count == 0) list = defaultPolishPresets.ToList();

    if (req.Delete == true)
    {
        if (defaultPolishPresets.Contains(name))
            return Results.BadRequest(new { error = "内置风格不可删除" });
        list.Remove(name);
    }
    else if (!list.Contains(name))
    {
        list.Add(name);
    }

    Directory.CreateDirectory(ConfigDir());
    File.WriteAllText(path, JsonSerializer.Serialize(list));
    return Results.Ok(new { ok = true, presets = list });
});

// ══════════════════════════════════════════════════════════════════
// 提示词方案与覆写（SystemPromptStore，与桌面版共用 system_prompts.json）
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/prompts", () =>
{
    var store = AiPrompts.Store;
    if (store == null) return Results.StatusCode(503);

    var presets = AiPrompts.PresetOptions(store)
        .Select(o => new { id = o.Id, name = o.Name, isBuiltIn = o.IsBuiltIn, description = o.Description })
        .ToList();

    // 每方案 × 每槽位：生效文本 + 是否被覆写。前端据此渲染编辑列表。
    var texts = new Dictionary<string, object>();
    foreach (var p in presets)
    {
        texts[p.id] = AiPrompts.Entries.ToDictionary(
            e => e.Key,
            e => new
            {
                text = AiPrompts.TextFor(p.id, e.Key),
                overridden = store.IsOverridden(p.id, e.Key),
            });
    }

    return Results.Ok(new
    {
        active = store.ActivePresetId,
        presets,
        entries = AiPrompts.Entries.Select(e => new
            { key = e.Key, group = e.Group, title = e.Title, description = e.Description }),
        texts,
        skillByPreset = store.Capture().SkillByPreset,
    });
});

// 写一条覆写。value 为空或与默认相同 → 视为恢复默认（与桌面版 Set 同语义）
app.MapPost("/api/prompts/set", (PromptSlotReq req) =>
{
    var store = AiPrompts.Store;
    if (store == null) return Results.StatusCode(503);
    if (!store.Exists(req.PresetId) || string.IsNullOrEmpty(req.Key))
        return Results.BadRequest(new { error = "方案或槽位不存在" });

    store.Set(req.PresetId!, req.Key!, req.Value, AiPrompts.DefaultFor(req.PresetId!, req.Key!));
    store.Save();
    return Results.Ok(new { ok = true, text = AiPrompts.TextFor(req.PresetId!, req.Key!) });
});

// 恢复默认：单条（key）或整个方案（all = true）
app.MapPost("/api/prompts/reset", (PromptResetReq req) =>
{
    var store = AiPrompts.Store;
    if (store == null) return Results.StatusCode(503);
    if (req.All == true) store.ResetAll(req.PresetId ?? store.ActivePresetId);
    else if (!string.IsNullOrEmpty(req.Key)) store.Reset(req.PresetId ?? store.ActivePresetId, req.Key);
    store.Save();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/prompts/active", (PromptActiveReq req) =>
{
    var store = AiPrompts.Store;
    if (store == null) return Results.StatusCode(503);
    if (!store.Exists(req.PresetId)) return Results.BadRequest(new { error = "方案不存在" });

    store.ActivePresetId = req.PresetId!;
    store.Save();
    return Results.Ok(new { ok = true, active = store.ActivePresetId });
});

app.MapPost("/api/prompts/custom", (PromptCustomReq req) =>
{
    var store = AiPrompts.Store;
    if (store == null) return Results.StatusCode(503);

    string id;
    if (req.RenameTo != null)
    {
        if (!store.RenameCustom(req.Id ?? "", req.RenameTo))
            return Results.BadRequest(new { error = "重命名失败（自定义方案不存在）" });
        id = req.Id ?? "";
    }
    else if (req.Delete == true)
    {
        if (!store.DeleteCustom(req.Id ?? ""))
            return Results.BadRequest(new { error = "删除失败（自定义方案不存在）" });
        id = "";
    }
    else
    {
        id = store.CreateCustom(req.BasedOn ?? PromptPresets.IdGeneral, req.Name ?? "");
    }
    store.Save();
    return Results.Ok(new { ok = true, id });
});

// ══════════════════════════════════════════════════════════════════
// 文献库（论文场景；AI 生成时自动作为引用上下文，正文引 [n]）
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/literature", (WorkspaceService ws) => Results.Ok(new { entries = ws.Literature() }));

app.MapPost("/api/literature", async (WorkspaceService ws, LiteratureEntry e) =>
{
    ws.UpsertLiterature(e);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/literature/{id}", async (WorkspaceService ws, string id) =>
{
    var ok = ws.DeleteLiterature(id);
    if (ok) await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "没有这条文献" });
});

app.MapPost("/api/literature/import-bibtex", async (WorkspaceService ws, BibReq req) =>
{
    var n = ws.ImportBibtex(req.Text ?? "");
    if (n == 0) return Results.BadRequest(new { error = "没解析出任何条目（检查 .bib 内容）" });
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true, imported = n });
});

app.MapGet("/api/literature/gbt", (WorkspaceService ws) => Results.Ok(new { text = ws.GbtReferenceList() }));

// 在线检索（OpenAlex / Semantic Scholar），勾选后走上面的 upsert 入库
app.MapGet("/api/literature/search", async (string? q, string? source) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { results = Array.Empty<object>() });
    var src = string.Equals(source, "semantic", StringComparison.OrdinalIgnoreCase)
        ? LiteratureSearchSource.SemanticScholar : LiteratureSearchSource.OpenAlex;
    try
    {
        var list = await LiteratureSearch.SearchAsync(q, src, 15);
        return Results.Ok(new
        {
            results = list.Select(r => new
            {
                title = r.Title, authors = r.Authors, year = r.Year, venue = r.Venue,
                doi = r.Doi, display = r.Display,
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ══════════════════════════════════════════════════════════════════
// 单章梗概 / 世界设定 / 角色出场
// ══════════════════════════════════════════════════════════════════

app.MapPost("/api/chapter/{n:int}/summary", async (WorkspaceService ws, int n, BookNameReq req) =>
{
    var ok = ws.SetChapterSummary(n, req.Name ?? "");
    if (!ok) return Results.NotFound(new { error = "没有这一章" });
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/world", (WorkspaceService ws) => Results.Ok(ws.WorldSetting() ?? new { }));

app.MapPost("/api/world", async (WorkspaceService ws, JsonElement req) =>
{
    ws.UpdateWorldSetting(req);
    await ws.BroadcastAsync("project-changed", new { stamp = StampToken.From(ws.Stamp()) });
    return Results.Ok(new { ok = true });
});

// ══════════════════════════════════════════════════════════════════
// 外观：配色 × 材质（与桌面版共用 appearance.json，48 种组合）
//
// 配色与材质的定义**不在这里重复一份**，直接从 AppearanceManager 取——
// 桌面端加了新配色/新材质，网页端自动就有，不会两边走偏。
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/appearance", () =>
{
    var cfg = new AppearanceManager(ConfigDir()).Load();
    var bg = WebBgFile();
    return Results.Ok(new
    {
        scheme = cfg.PresetName,
        material = cfg.MaterialName,
        intensity = cfg.MaterialIntensity,
        bgUrl = bg == null ? null : "/api/appearance/bg?v=" + File.GetLastWriteTimeUtc(bg).Ticks,
        schemes = AppearanceManager.ColorSchemes.Select(s => new
        {
            name = s.Name, displayName = s.DisplayName, description = s.Description,
            windowBg = s.WindowBg, panelBg = s.PanelBg, editorBg = s.EditorBg,
            menuBg = s.MenuBg, statusBarBg = s.StatusBarBg,
            textColor = s.TextColor, borderColor = s.BorderColor, splitterBg = s.SplitterBg,
            accent = s.Accent, textMuted = s.TextMuted, danger = s.Danger, isDark = s.IsDark,
        }),
        materials = AppearanceManager.BuiltInMaterials.Select(m => new
        {
            name = m.Name, displayName = m.DisplayName, description = m.Description,
            kind = m.Kind.ToString().ToLowerInvariant(),
            texture = m.Texture, sheen = m.Sheen, translucency = m.Translucency,
            stretched = m.Stretched, highGloss = m.HighGloss, edgeLight = m.EdgeLight,
        }),
    });
});

// 保存：只在点「确定」时调用；「取消」与实时预览全在浏览器侧，不落盘
app.MapPost("/api/appearance", (AppearanceReq req) =>
{
    var mgr = new AppearanceManager(ConfigDir());
    var cfg = mgr.Load();

    if (AppearanceManager.GetColorScheme(req.Scheme) != null) cfg.PresetName = req.Scheme!;
    if (AppearanceManager.GetMaterial(req.Material) != null) cfg.MaterialName = req.Material!;
    cfg.MaterialIntensity = Math.Clamp(req.Intensity ?? cfg.MaterialIntensity, 0.0, 2.0);

    mgr.Save(cfg);
    return Results.Ok(new { ok = true, scheme = cfg.PresetName, material = cfg.MaterialName, intensity = cfg.MaterialIntensity });
});

// 自定义背景图：桌面版存本机路径，网页版存服务器上的一个文件（任何设备打开都能看到）
app.MapGet("/api/appearance/bg", () =>
{
    var bg = WebBgFile();
    if (bg == null) return Results.NotFound();
    var ext = Path.GetExtension(bg).ToLowerInvariant();
    var mime = ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
    return Results.File(bg, mime);
});

app.MapPost("/api/appearance/bg", async (HttpContext ctx) =>
{
    var ext = (ctx.Request.Query["ext"].ToString() ?? "").ToLowerInvariant();
    if (ext.Length is < 2 or > 5 || !ext.All(char.IsLetterOrDigit))
        return Results.BadRequest(new { error = "扩展名不合法" });
    if (!new[] { "png", "jpg", "jpeg", "webp", "gif" }.Contains(ext))
        return Results.BadRequest(new { error = "只支持 PNG / JPG / WEBP / GIF" });

    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    if (ms.Length == 0) return Results.BadRequest(new { error = "空文件" });
    if (ms.Length > 8 * 1024 * 1024) return Results.BadRequest(new { error = "图片请控制在 8MB 以内" });

    foreach (var old in Directory.GetFiles(ConfigDir(), "web_bg.*")) File.Delete(old);
    Directory.CreateDirectory(ConfigDir());
    var path = Path.Combine(ConfigDir(), "web_bg." + ext);
    await File.WriteAllBytesAsync(path, ms.ToArray());

    return Results.Ok(new { ok = true, bgUrl = "/api/appearance/bg?v=" + File.GetLastWriteTimeUtc(path).Ticks });
});

app.MapDelete("/api/appearance/bg", () =>
{
    if (Directory.Exists(ConfigDir()))
        foreach (var old in Directory.GetFiles(ConfigDir(), "web_bg.*")) File.Delete(old);
    return Results.Ok(new { ok = true });
});

// ══════════════════════════════════════════════════════════════════
// 设定集（SettingsBook：把各类设定统合成一本可编辑、可导出的资料书）
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/settingsbook", (WorkspaceService ws) =>
{
    var book = ws.SettingsBook();
    return book == null ? Results.Ok(new { open = false }) : Results.Ok(book);
});

app.MapPost("/api/settingsbook/chapter", async (WorkspaceService ws, SbChapterReq req) =>
{
    if (!ws.UpdateSettingsBookChapter(req.ChapterId ?? "", req.Content, req.IncludeInExport))
        return Results.BadRequest(new { error = "章节不存在" });
    await ws.BroadcastAsync("settingsbook-changed", new { chapterId = req.ChapterId });
    return Results.Ok(new { ok = true });
});

// AI 生成/完善某章（与桌面版 GenerateSettingsBookChapterAsync 同源：
// 素材 = 项目设定块 + 作品简介 + 该章已有内容；系统提示词不带设定集本身）
app.MapPost("/api/settingsbook/generate", async (HttpContext ctx, WorkspaceService ws) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<SbGenerateReq>()
              ?? new SbGenerateReq(null);
    var p = ws.Current;
    if (p?.SettingsBook == null) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("先打开一本书"); return; }

    var chapter = p.SettingsBook.Chapters.FirstOrDefault(c => c.ChapterId == req.ChapterId);
    if (chapter == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("章节不存在"); return; }

    var cfg = LoadApiConfig();
    if (cfg == null) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("还没配置 AI 服务商。"); return; }

    var tpl = SettingsBookTemplates.Find(chapter.SourceKey);
    string taskText = tpl?.AiTask ?? "把与本章主题相关的已有设定整理成条目式内容，忠于已有事实，"
        + "需要补全而素材未明说的内容标注【推断】。";

    string userPrompt = AiPrompts.Section("目标章节", $"{chapter.Title}\n{taskText}");
    userPrompt += "\n" + AiPrompts.Section("作品简介", p.Description);
    userPrompt += "\n" + PromptContextBuilder.ProjectContext(p, ws.Memory());
    if (!string.IsNullOrWhiteSpace(chapter.Content))
        userPrompt += "\n" + AiPrompts.Section("本章已有内容（在此基础上完善，不要推翻）", chapter.Content);
    userPrompt += "\n请直接输出这一章的成稿内容。";

    var system = PromptContextBuilder.BuildSystemPrompt(
        p,
        NovelSkillStore.ResolveTaskPrompt(null, AiPrompts.Keys.SettingBook, AiPrompts.Task.SettingBook),
        null, null,
        NovelSkillStore.ResolveContract(null, AiPrompts.Keys.SettingBook, AiPrompts.StructuredOutput),
        includeSettingsBook: false);

    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    async Task Send(string type, object payload)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type, payload })}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    try
    {
        var service = ApiProviders.CreateService(cfg);
        var result = await service.CompleteTextAsync(userPrompt, system, new CompletionOptions
        {
            MaxTokens = 16384,
            Temperature = 0.5,
            Model = cfg.Model,
            OnNotice = msg => { _ = Send("notice", msg); },
            OnProgress = (inTok, outTok) => { _ = Send("progress", new { inTok, outTok }); },
            OnStreamText = text => { _ = Send("text", text); },
        });

        if (result.IsUsable)
        {
            ws.SaveSettingsBookChapterContent(chapter.ChapterId, result.Text);
            await ws.BroadcastAsync("settingsbook-changed", new { chapterId = chapter.ChapterId });
        }
        await Send("done", new { text = result.Text, ok = result.IsUsable, saved = result.IsUsable });
    }
    catch (Exception ex)
    {
        await Send("error", ex.Message);
    }
});

// 导出整本设定集（与桌面版同一个导出服务，排版规则一份）
app.MapGet("/api/settingsbook/export", (WorkspaceService ws, string? format) =>
{
    var p = ws.Current;
    if (p == null) return Results.BadRequest(new { error = "先打开一本书" });

    SettingsBookTemplates.EnsureBook(p);
    var ext = (format ?? "docx").ToLowerInvariant() switch
    {
        "pdf" => ".pdf", "txt" => ".txt", _ => ".docx",
    };
    var path = Path.Combine(Path.GetTempPath(), $"设定集-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
    try
    {
        switch (ext)
        {
            case ".pdf": SettingsBookExportService.ExportPdf(path, p); break;
            case ".txt": SettingsBookExportService.ExportTxt(path, p); break;
            default: SettingsBookExportService.ExportWord(path, p); break;
        }
        var bytes = File.ReadAllBytes(path);
        var mime = ext switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        };
        return Results.File(bytes, mime,
            fileDownloadName: $"{SettingsBookExportService.BookTitle(p.SettingsBook!, p)}{ext}");
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
});

// ══════════════════════════════════════════════════════════════════
// Agent 接入清单 —— 把编辑器接进 Codex / Claude / Cursor 等客户端
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// 客户端表只维护在 install-mcp.py 一处，这里跑它拿 JSON ——
/// 免得网页端再抄一份路径，两边早晚漂移（这正是本项目踩过的坑）。
/// 拿不到就诚实降级，让用户去跑 install-mcp.bat。
/// </summary>
app.MapGet("/api/mcp/clients", () =>
{
    var root = RepoRoot();
    if (root == null)
        return Results.Ok(new
        {
            available = false, reason = "no-repo",
            hint = "没找到 install-mcp.py（从仓库根启动服务器即可），也可以直接双击 install-mcp.bat。",
            clients = Array.Empty<object>(),
        });

    var py = FindPython();
    if (py == null)
        return Results.Ok(new
        {
            available = false, reason = "no-python",
            hint = "本机没找到 Python。装一个 Python 3，或直接双击仓库根的 install-mcp.bat。",
            clients = Array.Empty<object>(),
        });

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(py, "install-mcp.py --json")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(20000))
        {
            try { proc.Kill(true); } catch { }
            return Results.Ok(new
            {
                available = false, reason = "timeout",
                hint = "install-mcp.py 跑超时了，请在命令行手动执行。",
                clients = Array.Empty<object>(),
            });
        }
        if (proc.ExitCode != 0 || !stdout.TrimStart().StartsWith("{"))
            return Results.Ok(new
            {
                available = false, reason = "failed",
                hint = $"install-mcp.py 执行失败：{stderr.Trim().Split('\n').LastOrDefault()}",
                clients = Array.Empty<object>(),
            });

        using var doc = JsonDocument.Parse(stdout);
        var r = doc.RootElement;
        return Results.Ok(new
        {
            available = true,
            serverName = r.GetProperty("serverName").GetString(),
            exe = r.GetProperty("exe").GetString(),
            exeExists = r.GetProperty("exeExists").GetBoolean(),
            repoRoot = root,
            clients = r.GetProperty("clients").EnumerateArray().Select(c => new
            {
                id = c.GetProperty("id").GetString(),
                name = c.GetProperty("name").GetString(),
                kind = c.GetProperty("kind").GetString(),
                configPath = c.GetProperty("configPath").GetString(),
                detected = c.GetProperty("detected").GetBoolean(),
                status = c.GetProperty("status").GetString(),
                note = c.GetProperty("note").GetString(),
                snippet = c.GetProperty("snippet").GetString(),
                installCommand = c.GetProperty("installCommand").GetString(),
            }).ToArray(),
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            available = false, reason = "error", hint = ex.Message,
            clients = Array.Empty<object>(),
        });
    }
});

app.Run();

// ══════════════════════════════════════════════════════════════════

static string ConfigDir() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");

/// <summary>从运行目录往上找仓库根（认 install-mcp.py）。找不到返回 null。</summary>
static string? RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "install-mcp.py"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

/// <summary>找一个能跑的 Python：PATH 上的 py/python，或本机托管的那份。</summary>
static string? FindPython()
{
    var candidates = new List<string> { "py", "python" };
    var managed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy", "binaries", "python", "versions");
    try
    {
        if (Directory.Exists(managed))
            candidates.AddRange(Directory.GetDirectories(managed)
                .Select(d => Path.Combine(d, "python.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p));   // 版本号大的排前面
    }
    catch { /* 目录不可读就当没有 */ }

    foreach (var cand in candidates)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cand, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) continue;
            p.WaitForExit(4000);
            if (p.HasExited && p.ExitCode == 0) return cand;
        }
        catch { /* 不是有效解释器，试下一个 */ }
    }
    return null;
}

static string? WebBgFile()
{
    var dir = ConfigDir();
    if (!Directory.Exists(dir)) return null;
    return Directory.GetFiles(dir, "web_bg.*").FirstOrDefault();
}

static ApiConfig? LoadApiConfig()
{
    try
    {
        var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
        mgr.Load();
        return mgr.ActiveProfile;
    }
    catch
    {
        return null;
    }
}

public record BookNameReq([property: JsonPropertyName("name")] string? Name);
public record NameReq([property: JsonPropertyName("name")] string? Name);
public record MoveReq([property: JsonPropertyName("delta")] int Delta);
public record SaveChapterReq(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("stamp")] string? Stamp);
public record BibReq([property: JsonPropertyName("text")] string? Text);

public record EnabledReq([property: JsonPropertyName("enabled")] bool? Enabled);

public record ProfileSaveReq(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("apiUrl")] string? ApiUrl,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("authOverride")] string? AuthOverride,
    [property: JsonPropertyName("activate")] bool? Activate);

public record ProfileNewReq(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("apiUrl")] string? ApiUrl,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("authOverride")] string? AuthOverride,
    [property: JsonPropertyName("activate")] bool? Activate);

public record AiReq(
    [property: JsonPropertyName("task")] string? Task,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("maxTokens")] int MaxTokens,
    [property: JsonPropertyName("outputContract")] string? OutputContract,
    [property: JsonPropertyName("skillId")] string? SkillId,
    [property: JsonPropertyName("relatedChapterIds")] List<string>? RelatedChapterIds,
    [property: JsonPropertyName("history")] List<ChatTurn>? History,
    [property: JsonPropertyName("targetChapter")] int? TargetChapter,
    [property: JsonPropertyName("polishStyle")] string? PolishStyle,
    [property: JsonPropertyName("scaleHint")] string? ScaleHint);

public record PolishPresetReq(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("delete")] bool? Delete);

public record FindReq([property: JsonPropertyName("q")] string? Q);

public record ReplaceAllReq(
    [property: JsonPropertyName("q")] string? Q,
    [property: JsonPropertyName("r")] string? R,
    [property: JsonPropertyName("caseSensitive")] bool? CaseSensitive);

public record ChatTurn(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

public record SettingsReq(
    [property: JsonPropertyName("outline")] string? Outline,
    [property: JsonPropertyName("chapterOutline")] string? ChapterOutline,
    [property: JsonPropertyName("characters")] string? Characters,
    [property: JsonPropertyName("background")] string? Background,
    [property: JsonPropertyName("style")] string? Style,
    [property: JsonPropertyName("viewpoint")] string? Viewpoint);

public record SkillSaveReq(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("appliesTo")] List<string>? AppliesTo,
    [property: JsonPropertyName("presets")] List<string>? Presets,
    [property: JsonPropertyName("taskPrompt")] string? TaskPrompt,
    [property: JsonPropertyName("outputContract")] string? OutputContract,
    [property: JsonPropertyName("inputHint")] string? InputHint);

public record SkillImportReq([property: JsonPropertyName("json")] string? Json);

public record PromptSlotReq(
    [property: JsonPropertyName("presetId")] string? PresetId,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("value")] string? Value);

public record PromptResetReq(
    [property: JsonPropertyName("presetId")] string? PresetId,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("all")] bool? All);

public record PromptActiveReq([property: JsonPropertyName("presetId")] string? PresetId);

public record PromptCustomReq(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("basedOn")] string? BasedOn,
    [property: JsonPropertyName("renameTo")] string? RenameTo,
    [property: JsonPropertyName("delete")] bool? Delete);

public record SbChapterReq(
    [property: JsonPropertyName("chapterId")] string? ChapterId,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("includeInExport")] bool? IncludeInExport);

public record SbGenerateReq([property: JsonPropertyName("chapterId")] string? ChapterId);

public record AppearanceReq(
    [property: JsonPropertyName("scheme")] string? Scheme,
    [property: JsonPropertyName("material")] string? Material,
    [property: JsonPropertyName("intensity")] double? Intensity);
