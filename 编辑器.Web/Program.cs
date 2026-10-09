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

// 提示词方案/技能配置与界面共用同一份文件，进程启动时先载入，
// 否则 AiPrompts.Task.* 取不到用户改过的覆写文本（与 MCP 侧同样的处理）。
try { AiPrompts.Store?.Load(); }
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
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { skills = Array.Empty<object>(), error = ex.Message });
    }
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

app.Run();

// ══════════════════════════════════════════════════════════════════

static string ConfigDir() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");

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
