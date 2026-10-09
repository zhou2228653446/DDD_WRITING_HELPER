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
              ?? new AiReq(null, null, 0, null);

    var cfg = LoadApiConfig();
    if (cfg == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("还没配置 AI 服务商。请在桌面版「AI 设置」里配好后重启本服务。");
        return;
    }

    var service = ApiProviders.CreateService(cfg);
    var p = ws.Current;

    // 与桌面版同源：同一个 PromptContextBuilder，同一套上下文规则
    var system = PromptContextBuilder.BuildSystemPrompt(p, req.Task ?? "continue", null, null, req.OutputContract);

    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    async Task Send(string type, object payload)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type, payload })}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    try
    {
        var result = await service.CompleteTextAsync(req.Prompt ?? "", system, new CompletionOptions
        {
            MaxTokens = req.MaxTokens > 0 ? req.MaxTokens : 4096,
            Temperature = 0.7,
            Model = cfg.Model,
            OnNotice = msg => { _ = Send("notice", msg); },
            OnProgress = (inTok, outTok) => { _ = Send("progress", new { inTok, outTok }); },
            OnStreamText = text => { _ = Send("text", text); },
        });

        await Send("done", new { text = result.Text, ok = result.IsUsable });
    }
    catch (Exception ex)
    {
        await Send("error", ex.Message);
    }
});

app.MapGet("/api/ai/profiles", () =>
{
    var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
    mgr.Load();
    return Results.Ok(new { active = mgr.ActiveProfileName, names = mgr.GetProfileNames() });
});

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
public record MoveReq([property: JsonPropertyName("delta")] int Delta);
public record SaveChapterReq(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("stamp")] long? Stamp);

public record AiReq(
    [property: JsonPropertyName("task")] string? Task,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("maxTokens")] int MaxTokens,
    [property: JsonPropertyName("outputContract")] string? OutputContract);

public record SettingsReq(
    [property: JsonPropertyName("outline")] string? Outline,
    [property: JsonPropertyName("chapterOutline")] string? ChapterOutline,
    [property: JsonPropertyName("characters")] string? Characters,
    [property: JsonPropertyName("background")] string? Background,
    [property: JsonPropertyName("style")] string? Style,
    [property: JsonPropertyName("viewpoint")] string? Viewpoint);
