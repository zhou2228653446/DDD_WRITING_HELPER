using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Mcp;

/// <summary>工具返回值：一段文本 + 是否算错误。MCP 里错误也要把原因说清楚，否则 agent 只会盲目重试。</summary>
internal readonly record struct ToolResult(string Text, bool IsError = false)
{
    public static ToolResult Ok(string text) => new(text);
    public static ToolResult Fail(string text) => new(text, true);
}

/// <summary>
/// MCP 进程是长驻的，得记住「当前打开的是哪个项目」。
///
/// ★ 为什么要记文件指纹（_stamp）：
/// 这是个**独立进程**，和桌面软件界面是两套内存。用户一边开着编辑器改稿、一边让 agent 写，
/// 两边各存各的，后存的那份会静默覆盖掉先存的那份——丢的是真稿子。
/// 所以打开时记下「最后修改时间 + 文件长度」，每次写盘前重新比对：
/// 磁盘上的项目已经不是我打开的那一份了，就拒绝写入，让 agent 重新 open。
/// 这不能防止所有冲突，但能把最致命的「静默覆盖」变成「明确报错」。
/// </summary>
internal sealed class Session
{
    public NovelProject? Project { get; private set; }
    public string Path { get; private set; } = "";

    private string _stamp = "";

    public ToolResult Open(string path)
    {
        if (!File.Exists(path))
            return ToolResult.Fail($"项目文件不存在：{path}");

        NovelProject project;
        try
        {
            project = NovelProject.Load(path);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"读取项目失败（不是有效的 .tdxproj？）：{ex.Message}");
        }

        Project = project;
        Path = path;
        Restamp();

        var filled = new List<string>();
        if (!string.IsNullOrWhiteSpace(project.FullOutline)) filled.Add("全文大纲");
        if (!string.IsNullOrWhiteSpace(project.ChapterOutline)) filled.Add("章节大纲");
        if (!string.IsNullOrWhiteSpace(project.CharacterSettings)) filled.Add("人物设定");
        if (!string.IsNullOrWhiteSpace(project.BackgroundSettings)) filled.Add("背景设定");
        if (!string.IsNullOrWhiteSpace(project.WritingStyle)) filled.Add("文风设定");
        if (!string.IsNullOrWhiteSpace(project.NarrativeViewpoint)) filled.Add("叙事视角");
        if (project.SettingsBook?.Chapters.Count > 0) filled.Add($"设定集（{project.SettingsBook.Chapters.Count} 章）");

        var words = project.Chapters.Sum(c => c.WordCount);
        return ToolResult.Ok(
            $"已打开项目「{project.ProjectName}」\n" +
            $"路径：{path}\n" +
            $"简介：{Truncate(project.Description, 200)}\n" +
            $"章节：{project.Chapters.Count} 章 / 共 {words} 字\n" +
            $"已填设定：{(filled.Count == 0 ? "（无）" : string.Join("、", filled))}\n\n" +
            "下一步：chapters_list 看章节表，chapter_read 读正文，settings_get 看设定。");
    }

    /// <summary>取当前项目；没打开就给出可操作的错误。</summary>
    public bool TryGet(out NovelProject project, out ToolResult error)
    {
        if (Project == null)
        {
            project = null!;
            error = ToolResult.Fail("还没有打开项目。先用 project_list 找 .tdxproj，再 project_open 打开。");
            return false;
        }
        project = Project;
        error = default;
        return true;
    }

    /// <summary>写盘前检查：磁盘上的项目是否已被别的实例改写。是则拒绝。</summary>
    public bool IsStale(out ToolResult error)
    {
        error = default;
        if (Project == null) return false;

        try
        {
            var now = StampOf(Path);
            if (now == _stamp) return false;

            error = ToolResult.Fail(
                "项目文件已被其他程序修改（很可能是编辑器界面还开着这个项目）。\n" +
                "为避免覆盖你的稿子，本次写入已取消。\n" +
                "处理办法：在编辑器里关掉这个项目（或重新保存一次），然后重新调用 project_open 再写。");
            return true;
        }
        catch
        {
            return false; // 文件被临时锁住等情况，交给 Save 自己去报
        }
    }

    public void Save()
    {
        Project!.Save();
        Restamp();
    }

    /// <summary>写完、重新打开后都要刷新指纹——自己写的改动不算「被别人改」。</summary>
    public void Restamp() => _stamp = StampOf(Path);

    private static string StampOf(string path)
    {
        var fi = new FileInfo(path);
        return $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
    }

    internal static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// 小说项目的读写工具。全部直接调主程序的服务层，不重写逻辑——
/// 保证 agent 看到的章节、设定集、导出结果和界面里的完全一致。
/// </summary>
internal static class NovelTools
{
    // ---- 参数读取：agent 传来的 JSON 缺字段、类型不对是常态，一律给默认值而不是抛异常 ----
    internal static string Str(JsonElement a, string key, string def = "")
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? def;
            if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        }
        return def;
    }

    internal static int Int(JsonElement a, string key, int def)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i;
        return def;
    }

    internal static bool Bool(JsonElement a, string key, bool def)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return def;
    }

    // ==================================================================
    // 读
    // ==================================================================

    public static ToolResult ListProjects(JsonElement args)
    {
        var dir = Str(args, "directory");
        if (string.IsNullOrWhiteSpace(dir))
        {
            // 没给目录就扫几个常见位置：工作目录 + 我的文档 + 桌面
            var candidates = new[]
            {
                Directory.GetCurrentDirectory(),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            var found = new List<string>();
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
                try
                {
                    found.AddRange(Directory.GetFiles(c, "*.tdxproj", SearchOption.AllDirectories));
                }
                catch { /* 权限不足的目录跳过 */ }
            }
            if (found.Count == 0)
                return ToolResult.Ok("常见位置没找到 .tdxproj。请显式传 directory 参数指定项目所在目录。");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("找到项目：");
            foreach (var f in found.Distinct().Take(50))
                sb.AppendLine("  " + f);
            return ToolResult.Ok(sb.ToString());
        }

        if (!Directory.Exists(dir))
            return ToolResult.Fail($"目录不存在：{dir}");

        var files = Directory.GetFiles(dir, "*.tdxproj", SearchOption.AllDirectories);
        if (files.Length == 0)
            return ToolResult.Ok($"目录 {dir} 下没有 .tdxproj 项目文件。");

        var list = new System.Text.StringBuilder();
        list.AppendLine($"目录 {dir} 下共 {files.Length} 个项目：");
        foreach (var f in files.Take(100))
        {
            string name = Path.GetFileName(f);
            try
            {
                var p = NovelProject.Load(f);
                list.AppendLine($"  {f}  ——「{p.ProjectName}」{p.Chapters.Count} 章");
            }
            catch
            {
                list.AppendLine($"  {f}  ——（读取失败，可能不是本项目格式）");
            }
        }
        return ToolResult.Ok(list.ToString());
    }

    public static ToolResult ListChapters(NovelProject p)
    {
        if (p.Chapters.Count == 0)
            return ToolResult.Ok("这个项目还没有章节。可以用 chapter_create 新建。");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"「{p.ProjectName}」共 {p.Chapters.Count} 章：");
        foreach (var c in p.Chapters.OrderBy(c => c.ChapterNumber))
        {
            var sum = string.IsNullOrWhiteSpace(c.Summary) ? "" : "  ｜梗概：" + Session.Truncate(c.Summary, 30);
            sb.AppendLine($"  第{c.ChapterNumber}章 {c.Title}（{c.WordCount} 字）{sum}");
        }
        return ToolResult.Ok(sb.ToString());
    }

    public static ToolResult ReadChapter(NovelProject p, JsonElement args)
    {
        var n = Int(args, "number", -1);
        var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
        if (c == null)
            return ToolResult.Fail($"没有第 {n} 章。用 chapters_list 看现有章节号。");

        var head = $"=== 第{c.ChapterNumber}章 {c.Title}（{c.WordCount} 字）===\n\n";
        var max = Int(args, "maxChars", 0);
        var body = c.Content;
        if (max > 0 && body.Length > max)
            body = body[..max] + $"\n…（已截断，共 {c.WordCount} 字，可调大 maxChars 继续读）";
        return ToolResult.Ok(head + body);
    }

    public static ToolResult SearchChapters(NovelProject p, JsonElement args)
    {
        var kw = Str(args, "keyword");
        if (string.IsNullOrWhiteSpace(kw))
            return ToolResult.Fail("keyword 不能为空。");

        var ctx = Int(args, "contextChars", 80);
        var hits = new System.Text.StringBuilder();
        int count = 0;

        foreach (var c in p.Chapters.OrderBy(c => c.ChapterNumber))
        {
            if (string.IsNullOrEmpty(c.Content)) continue;
            int idx = 0;
            while ((idx = c.Content.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                int from = Math.Max(0, idx - ctx);
                int to = Math.Min(c.Content.Length, idx + kw.Length + ctx);
                var snippet = c.Content[from..to].Replace("\n", " ");
                hits.AppendLine($"第{c.ChapterNumber}章「{c.Title}」@{idx}：…{snippet}…");
                idx += kw.Length;
                if (count >= 40) break;
            }
            if (count >= 40) break;
        }

        return count == 0
            ? ToolResult.Ok($"全文没有找到「{kw}」。")
            : ToolResult.Ok($"命中 {count} 处（最多列 40）：\n{hits}");
    }

    public static ToolResult GetSettings(NovelProject p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"「{p.ProjectName}」的贯穿设定：");
        sb.AppendLine($"【叙事视角】{Show(p.NarrativeViewpoint)}");
        sb.AppendLine($"【全文大纲】{Show(p.FullOutline)}");
        sb.AppendLine($"【章节大纲】{Show(p.ChapterOutline)}");
        sb.AppendLine($"【人物设定】{Show(p.CharacterSettings)}");
        sb.AppendLine($"【背景设定】{Show(p.BackgroundSettings)}");
        sb.AppendLine($"【文风设定】{Show(p.WritingStyle)}");
        sb.AppendLine($"【作品简介】{Show(p.Description)}");
        return ToolResult.Ok(sb.ToString());

        static string Show(string s) => string.IsNullOrWhiteSpace(s) ? "（未填写）" : s;
    }

    public static ToolResult GetSettingsBook(NovelProject p, JsonElement args)
    {
        var book = p.SettingsBook;
        if (book == null || book.Chapters.Count == 0)
            return ToolResult.Ok("这个项目还没有设定集（在编辑器里打开一次「设定集」窗口会自动创建 12 章骨架）。");

        var key = Str(args, "key");
        if (!string.IsNullOrWhiteSpace(key))
        {
            var one = book.Chapters.FirstOrDefault(c =>
                string.Equals(c.SourceKey, key, StringComparison.OrdinalIgnoreCase) ||
                c.Title.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (one == null)
                return ToolResult.Fail($"设定集里没有 key/title 匹配「{key}」的章。不带 key 调用可看全部章清单。");

            var flag = one.IsAiGenerated ? "（AI 生成）" : "";
            return ToolResult.Ok($"=== 设定集 · {one.Title}（key={one.SourceKey}）{flag} ===\n\n" +
                                 (string.IsNullOrWhiteSpace(one.Content) ? "（本章还是空的）" : one.Content));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"设定集「{book.Title}」共 {book.Chapters.Count} 章（带 key 的就是可精确读写的标识）：");
        foreach (var c in book.Chapters)
        {
            var state = string.IsNullOrWhiteSpace(c.Content) ? "空" : $"{c.Content.Length} 字";
            sb.AppendLine($"  {c.SourceKey,-14} {c.Title}（{state}）{(c.IncludeInExport ? "" : " [不导出]")}");
        }
        sb.AppendLine("\n用 settings_book_get 传 key 读某一章全文。");
        return ToolResult.Ok(sb.ToString());
    }

    public static ToolResult CharacterStats(NovelProject p)
    {
        var stats = CharacterAppearanceService.Analyze(p);
        if (stats.Count == 0)
            return ToolResult.Ok("没能识别出角色名。先在「人物设定」里按「姓名：xxx」的格式登记，或到设定集人物章补档案。");
        return ToolResult.Ok(CharacterAppearanceService.FormatReport(p, stats));
    }

    // ==================================================================
    // 建项目
    // ==================================================================

    /// <summary>
    /// project_create：agent 自己开一本新书。
    /// 建好立刻 open（省一轮往返），并像界面新建项目那样把设定集 12 章骨架补上。
    /// </summary>
    public static ToolResult CreateProject(Session s, JsonElement args)
    {
        var dir = Str(args, "directory");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var name = Str(args, "name");
        if (string.IsNullOrWhiteSpace(name)) name = "新建小说";

        if (!Directory.Exists(dir))
            return ToolResult.Fail($"目录不存在：{dir}");

        var file = Path.Combine(dir, SafeFileName(name) + ".tdxproj");
        if (File.Exists(file))
            return ToolResult.Fail(
                $"文件已存在：{file}\n如果是想打开这个项目，请用 project_open（直接覆盖别人的稿子是灾难）。");

        var p = new NovelProject
        {
            ProjectName = name,
            Description = Str(args, "description"),
            FilePath = file,
            CreatedDate = DateTime.Now,
            ModifiedDate = DateTime.Now,
            Chapters = new List<Chapter>(),
        };

        try
        {
            // 与界面新建项目一致：立刻补设定集骨架，agent 后面可以直接往里写
            SettingsBookTemplates.EnsureBook(p);
        }
        catch (Exception ex)
        {
            // 骨架失败不影响建项目本身
            Console.Error.WriteLine($"[mcp] 设定集骨架创建失败：{ex.Message}");
        }

        try
        {
            p.Save();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"创建项目失败（写入 {file} 出错）：{ex.Message}");
        }

        var opened = s.Open(file);
        return ToolResult.Ok($"✅ 已创建并打开项目「{name}」\n文件：{file}\n\n" + opened.Text);
    }

    /// <summary>文件名安全化（去掉 Windows 非法字符， Trim 掉末尾的点与空格）。</summary>
    internal static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s2 = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
        s2 = s2.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(s2) ? "新建小说" : s2;
    }

    // ==================================================================
    // 写
    // ==================================================================

    public static ToolResult WriteChapter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var n = Int(args, "number", -1);
        var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
        if (c == null)
            return ToolResult.Fail($"没有第 {n} 章。用 chapters_list 看现有章节号，或 chapter_create 新建。");

        var content = Str(args, "content");
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Fail("content 不能为空。");

        var append = Str(args, "mode", "replace").Equals("append", StringComparison.OrdinalIgnoreCase);
        int before = c.WordCount;

        if (append)
        {
            var sep = c.Content.EndsWith('\n') || c.Content.Length == 0 ? "" : "\n";
            c.Content = c.Content + sep + content;
        }
        else
        {
            c.Content = content;
        }

        c.ModifiedDate = DateTime.Now;
        c.LastModified = DateTime.Now;
        s.Save();

        return ToolResult.Ok(
            $"已{(append ? "追加" : "覆盖")}第{c.ChapterNumber}章「{c.Title}」：" +
            $"{before} 字 → {c.WordCount} 字。\n" +
            (append ? "" : "⚠ 覆盖模式会丢弃原正文，如需保留请用 mode=append。"));
    }

    public static ToolResult CreateChapter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var title = Str(args, "title");
        if (string.IsNullOrWhiteSpace(title)) title = "新建章节";

        int next = p.Chapters.Count == 0 ? 1 : p.Chapters.Max(c => c.ChapterNumber) + 1;
        var content = Str(args, "content");

        p.Chapters.Add(new Chapter
        {
            ChapterNumber = next,
            Title = title,
            Content = content,
            CreatedDate = DateTime.Now,
            ModifiedDate = DateTime.Now,
            LastModified = DateTime.Now,
        });
        s.Save();

        return ToolResult.Ok($"已新建第{next}章「{title}」（{content.Length} 字）。");
    }

    public static ToolResult SetSetting(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var field = Str(args, "field").Trim().ToLowerInvariant();
        var text = Str(args, "text");

        switch (field)
        {
            case "outline":
            case "full_outline": p.FullOutline = text; break;
            case "chapter_outline": p.ChapterOutline = text; break;
            case "characters":
            case "character": p.CharacterSettings = text; break;
            case "background": p.BackgroundSettings = text; break;
            case "style":
            case "writing_style": p.WritingStyle = text; break;
            case "viewpoint":
            case "narrative_viewpoint": p.NarrativeViewpoint = text; break;
            case "description": p.Description = text; break;
            default:
                return ToolResult.Fail(
                    $"未知字段「{field}」。可选：full_outline / chapter_outline / characters / " +
                    "background / writing_style / narrative_viewpoint / description");
        }

        s.Save();
        return ToolResult.Ok($"已更新「{field}」（{text.Length} 字）。");
    }

    public static ToolResult SetSettingsBook(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var book = p.SettingsBook;
        if (book == null || book.Chapters.Count == 0)
            return ToolResult.Fail("这个项目还没有设定集。在编辑器里打开一次「设定集」窗口会自动创建骨架。");

        var key = Str(args, "key");
        var ch = book.Chapters.FirstOrDefault(c =>
            string.Equals(c.SourceKey, key, StringComparison.OrdinalIgnoreCase) ||
            c.Title.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (ch == null)
            return ToolResult.Fail($"设定集里没有匹配「{key}」的章。不带参数调 settings_book_get 看清单。");

        var content = Str(args, "content");
        var append = Str(args, "mode", "replace").Equals("append", StringComparison.OrdinalIgnoreCase);
        ch.Content = append && !string.IsNullOrWhiteSpace(ch.Content)
            ? ch.Content.TrimEnd() + "\n" + content
            : content;
        s.Save();

        return ToolResult.Ok($"已{(append ? "追加" : "覆盖")}设定集章「{ch.Title}」（{ch.Content.Length} 字）。");
    }

    // ==================================================================
    // 导出
    // ==================================================================

    public static ToolResult Export(NovelProject p, JsonElement args)
    {
        var format = Str(args, "format", "docx").Trim().ToLowerInvariant();
        var output = Str(args, "output");
        if (string.IsNullOrWhiteSpace(output))
            return ToolResult.Fail("output 不能为空（导出文件的完整路径）。");

        var paper = Bool(args, "paperMode", false);

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            switch (format)
            {
                case "docx":
                case "word":
                    if (!output.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) output += ".docx";
                    WordExportService.Export(output, p, paper);
                    break;
                case "pdf":
                    if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
                    PdfExportService.Export(output, p, paper);
                    break;
                case "txt":
                    if (!output.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) output += ".txt";
                    TxtExportService.Export(output, p, paper);
                    break;
                default:
                    return ToolResult.Fail($"未知格式「{format}」。可选：docx / pdf / txt");
            }

            var size = new FileInfo(output).Length;
            return ToolResult.Ok($"已导出：{output}（{size / 1024} KB，{p.Chapters.Count} 章" +
                                 (paper ? "，论文版式" : "") + "）");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"导出失败：{ex.Message}");
        }
    }
}
