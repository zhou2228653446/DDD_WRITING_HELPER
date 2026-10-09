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
        await ws.BroadcastAsync("project-changed", new { stamp = after });
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
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { name = ws.Current?.ProjectName, stamp = ws.Stamp() });
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
        stamp = ws.Stamp(),
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
    var ok = ws.SaveChapter(n, req.Content ?? "", req.Stamp);
    if (!ok) return Results.Conflict(new { error = "这本书已经被别处改过了，请刷新后再保存" });

    await ws.BroadcastAsync("chapter-saved", new { n });
    return Results.Ok(new { ok = true, stamp = ws.Stamp() });
});

app.MapPost("/api/chapter", async (WorkspaceService ws, BookNameReq req) =>
{
    var ch = ws.AddChapter(req.Name);
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { n = ch.ChapterNumber, title = ch.Title });
});

app.MapPost("/api/chapter/{n:int}/rename", async (WorkspaceService ws, int n, BookNameReq req) =>
{
    ws.RenameChapter(n, req.Name ?? "");
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/chapter/{n:int}/move", async (WorkspaceService ws, int n, MoveReq req) =>
{
    ws.MoveChapter(n, req.Delta);
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/chapter/{n:int}", async (WorkspaceService ws, int n) =>
{
    ws.DeleteChapter(n);
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
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

    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/stats", (WorkspaceService ws) => Results.Ok(ws.Stats() ?? new { }));
app.MapGet("/api/snapshots", (WorkspaceService ws) => Results.Ok(ws.Snapshots()));

app.MapPost("/api/snapshot/restore", async (WorkspaceService ws, BookNameReq req) =>
{
    var ok = ws.RestoreSnapshot(req.Name ?? "");
    if (!ok) return Results.NotFound(new { error = "找不到这个快照" });

    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
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
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/characters/{id}", async (WorkspaceService ws, string id) =>
{
    ws.DeleteCharacter(id);
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
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
// AI 生成（SSE 流式，前端能看着字一个一个出来）
// ══════════════════════════════════════════════════════════════════

app.MapPost("/api/ai", async (HttpContext ctx, WorkspaceService ws) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AiReq>()
              ?? new AiReq(null, null, 0, null, null, null, null);

    var cfg = LoadApiConfig();
    if (cfg == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("还没配置 AI 服务商。点右上角「AI 设置」配好 Key 再来。");
        return;
    }

    var service = ApiProviders.CreateService(cfg);
    var p = ws.Current;

    // 任务键 → 任务提示词文本（桌面版同源：先取默认，再让技能覆盖）
    var skill = ResolveSkill(req.SkillId);
    var (taskText, contract) = ResolveTask(req.Task, skill, req.OutputContract);

    // 与桌面版同源：同一个 PromptContextBuilder（设定集/文献库/参考章节全部生效）
    var system = PromptContextBuilder.BuildSystemPrompt(
        p, taskText, ws.Memory(), req.RelatedChapterIds, contract);

    // 审稿：把全书正文放进 user 侧（桌面版 RunAiReview 的做法）
    var prompt = req.Prompt ?? "";
    if ((req.Task ?? "") == "review" && p != null)
    {
        var book = string.Join("\n\n", p.Chapters.OrderBy(c => c.ChapterNumber)
            .Select(c => $"── 第{c.ChapterNumber}章 {c.Title} ──\n{c.Content}"));
        prompt = AiPrompts.Section("全书正文", book) + "\n" +
                 "请对照我的设定（见系统部分）审这本书的一致性问题。" + prompt;
    }

    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    async Task Send(string type, object payload)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type, payload })}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    try
    {
        var history = req.History?.Select(m => new ChatMessage(
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            m.Content ?? "")).ToList();

        var result = await service.CompleteTextAsync(prompt, system, new CompletionOptions
        {
            MaxTokens = req.MaxTokens > 0 ? req.MaxTokens : 4096,
            Temperature = 0.7,
            Model = cfg.Model,
            OnNotice = msg => { _ = Send("notice", msg); },
            OnProgress = (inTok, outTok) => { _ = Send("progress", new { inTok, outTok }); },
            OnStreamText = text => { _ = Send("text", text); },
        }, history);

        await Send("done", new { text = result.Text, ok = result.IsUsable });
    }
    catch (Exception ex)
    {
        await Send("error", ex.Message);
    }
});

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
            hasKey = !string.IsNullOrWhiteSpace(kv.Value.ApiKey),
        }),
    });
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
    }),
}));

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

    store.Set(req.PresetId!, req.Key!, req.Value, AiPrompts.DefaultFor(req.PresetId, req.Key));
    store.Save();
    return Results.Ok(new { ok = true, text = AiPrompts.TextFor(req.PresetId, req.Key) });
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
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/literature/{id}", async (WorkspaceService ws, string id) =>
{
    var ok = ws.DeleteLiterature(id);
    if (ok) await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "没有这条文献" });
});

app.MapPost("/api/literature/import-bibtex", async (WorkspaceService ws, BibReq req) =>
{
    var n = ws.ImportBibtex(req.Text ?? "");
    if (n == 0) return Results.BadRequest(new { error = "没解析出任何条目（检查 .bib 内容）" });
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
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
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/world", (WorkspaceService ws) => Results.Ok(ws.WorldSetting() ?? new { }));

app.MapPost("/api/world", async (WorkspaceService ws, JsonElement req) =>
{
    ws.UpdateWorldSetting(req);
    await ws.BroadcastAsync("project-changed", new { stamp = ws.Stamp() });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/character-appearances", (WorkspaceService ws) =>
    Results.Ok(new { stats = ws.CharacterAppearances() }));

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

app.Run();

// ══════════════════════════════════════════════════════════════════

static string ConfigDir() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");

/// <summary>网页版上传的背景图（服务器上只留一个）。没有则返回 null。</summary>
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
    [property: JsonPropertyName("stamp")] long? Stamp);
public record BibReq([property: JsonPropertyName("text")] string? Text);

public record ProfileSaveReq(
    [property: JsonPropertyName("name")] string? Name,
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
    [property: JsonPropertyName("history")] List<ChatTurn>? History);

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
