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
        if (!string.IsNullOrWhiteSpace(project.CharacterSettings) || project.Characters.Count > 0)
            filled.Add(project.Characters.Count > 0 ? $"人物设定（含 {project.Characters.Count} 张人物卡）" : "人物设定");
        if (!string.IsNullOrWhiteSpace(project.BackgroundSettings)) filled.Add("背景设定");
        if (!string.IsNullOrWhiteSpace(project.WritingStyle)) filled.Add("文风设定");
        if (!string.IsNullOrWhiteSpace(project.NarrativeViewpoint)) filled.Add("叙事视角");
        if (project.SettingsBook?.Chapters.Count > 0) filled.Add($"设定集（{project.SettingsBook.Chapters.Count} 章）");

        _lastLightweightSnapshotUtc = DateTime.MinValue;
        _lastLightweightPath = "";

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

    /// <summary>
    /// 整体替换当前项目（恢复快照用）。换完立即落盘，和界面「恢复版本」的行为一致。
    /// </summary>
    public void Replace(NovelProject project)
    {
        Project = project;
        _lastLightweightSnapshotUtc = DateTime.MinValue;
        _lastLightweightPath = "";
        Save();
    }

    private DateTime _lastLightweightSnapshotUtc = DateTime.MinValue;
    private string _lastLightweightPath = "";

    /// <summary>
    /// 改稿前存一份快照——这是 MCP 侧唯一能撤销的手段。
    ///
    /// ★ 界面每次 AI 改稿前都会 TakeSnapshot（"续写前备份"之类），MCP 早期没有。
    /// 而 MCP 恰恰是 agent **自动**改稿的场景：一次 chapter_write 覆盖错章就是几千字
    /// 直接没了，比界面里手点更容易出事。所以写盘前一律先存一份。
    ///
    /// 当 <paramref name="lightweight"/> 为 true（设定、人物卡、章节梗概等高频轻量小改）时，
    /// 30 秒内的连续轻量修改合并共用批次前的第一份快照，避免 agent 连续建 5 张人物卡 + 6 项设定
    /// 瞬间刷满几十个快照；而章节正文/建删章节等重量级操作始终每次必存。
    /// </summary>
    public void Snapshot(string description, bool lightweight = false)
    {
        try
        {
            if (Project == null) return;
            var now = DateTime.UtcNow;
            if (lightweight)
            {
                if (_lastLightweightPath == Path && (now - _lastLightweightSnapshotUtc).TotalSeconds < 30)
                    return;
                _lastLightweightSnapshotUtc = now;
                _lastLightweightPath = Path;
            }
            else
            {
                _lastLightweightSnapshotUtc = DateTime.MinValue;
                _lastLightweightPath = "";
            }
            new ProjectSnapshotManager(Path).SaveSnapshot(Project, description);
        }
        catch (Exception ex)
        {
            // 快照只是保险，失败不能阻断写入——否则磁盘一满就什么都写不了了
            Console.Error.WriteLine($"[mcp] 快照失败（不阻断写入）：{ex.Message}");
        }
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

    internal static string StrAny(JsonElement a, string def, params string[] keys)
    {
        foreach (var k in keys)
        {
            var val = Str(a, k);
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return def;
    }

    internal static int Int(JsonElement a, string key, int def)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()?.Trim() ?? "";
                if (int.TryParse(s, out var parsed))
                    return parsed;
                // 兼容 agent 传入 "27岁"、"第3章" 等带单位的字符串数字
                var m = System.Text.RegularExpressions.Regex.Match(s, @"-?\d+");
                if (m.Success && int.TryParse(m.Value, out var extracted))
                    return extracted;
            }
        }
        return def;
    }

    internal static int IntAny(JsonElement a, int def, params string[] keys)
    {
        foreach (var k in keys)
        {
            var val = Int(a, k, int.MinValue);
            if (val != int.MinValue) return val;
        }
        return def;
    }

    internal static bool Bool(JsonElement a, string key, bool def)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()?.Trim() ?? "";
                if (bool.TryParse(s, out var b)) return b;
                if (s == "1") return true;
                if (s == "0") return false;
            }
        }
        return def;
    }

    /// <summary>
    /// 读取主程序保存的 paths.json，保证仅分发 exe 时 MCP 与主程序使用完全相同的项目目录和配置目录。
    /// </summary>
    internal static string ResolveProjectsDirectory()
    {
        try
        {
            var pathsFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw", "paths.json");
            if (File.Exists(pathsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pathsFile));
                if (doc.RootElement.TryGetProperty("ProjectsDirectory", out var p) &&
                    p.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(p.GetString()))
                {
                    return p.GetString()!;
                }
            }
        }
        catch { }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TdxClaw", "Projects");
    }

    internal static string ResolveConfigDirectory()
    {
        try
        {
            var pathsFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw", "paths.json");
            if (File.Exists(pathsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pathsFile));
                if (doc.RootElement.TryGetProperty("ConfigDirectory", out var c) &&
                    c.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(c.GetString()))
                {
                    return c.GetString()!;
                }
            }
        }
        catch { }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");
    }

    private static readonly EnumerationOptions SafeRecurseOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MaxRecursionDepth = 4,
    };

    // ==================================================================
    // 读
    // ==================================================================

    public static ToolResult ListProjects(JsonElement args)
    {
        var dir = StrAny(args, "", "directory", "path", "dir");
        if (string.IsNullOrWhiteSpace(dir))
        {
            // 没给目录就扫几个常见位置：软件默认项目目录 + 工作目录 + 仓库目录（由 exe 位置上推） + 我的文档 + 桌面
            var repoRoot = "";
            try { repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")); }
            catch { }

            var candidates = new[]
            {
                ResolveProjectsDirectory(),
                Directory.GetCurrentDirectory(),
                repoRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            var found = new List<string>();
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
                try
                {
                    found.AddRange(Directory.GetFiles(c, "*.tdxproj", SafeRecurseOptions));
                }
                catch { /* 权限不足的目录跳过 */ }
            }
            if (found.Count == 0)
                return ToolResult.Ok("常见位置没找到 .tdxproj。请显式传 directory 参数指定项目所在目录。");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("找到项目：");
            foreach (var f in found.Distinct(StringComparer.OrdinalIgnoreCase).Take(50))
                sb.AppendLine("  " + f);
            return ToolResult.Ok(sb.ToString());
        }

        if (!Directory.Exists(dir))
            return ToolResult.Fail($"目录不存在：{dir}");

        var files = Directory.GetFiles(dir, "*.tdxproj", SafeRecurseOptions);
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
        var charEffective = p.BuildEffectiveCharacterSettings();
        sb.AppendLine($"【人物设定】{Show(charEffective)}");
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
    /// 兼容 path 别名以及直接传完整 .tdxproj 文件路径，并支持建书时顺带传入初始贯穿设定。
    /// </summary>
    public static ToolResult CreateProject(Session s, JsonElement args)
    {
        var dir = StrAny(args, "", "directory", "path", "dir").Trim();
        var name = Str(args, "name").Trim();

        // 若 dir 或 name 传入了完整 .tdxproj 文件路径，自动拆分出父目录与书名
        if (dir.EndsWith(".tdxproj", StringComparison.OrdinalIgnoreCase))
        {
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(dir);
            var parentDir = Path.GetDirectoryName(dir);
            if (string.IsNullOrWhiteSpace(name))
                name = fileNameWithoutExt;
            dir = string.IsNullOrWhiteSpace(parentDir) ? Directory.GetCurrentDirectory() : parentDir;
        }
        if (name.EndsWith(".tdxproj", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                var parentDir = Path.GetDirectoryName(name);
                if (!string.IsNullOrWhiteSpace(parentDir))
                    dir = parentDir;
            }
            name = Path.GetFileNameWithoutExtension(name);
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = ResolveProjectsDirectory();
            try { Directory.CreateDirectory(dir); } catch { }
        }
        if (string.IsNullOrWhiteSpace(name))
            name = "新建小说";

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

        // 支持建项目时顺手传入初始贯穿设定
        foreach (var key in BatchSettingKeys)
        {
            if (Has(args, key))
                ApplySettingField(p, key, Str(args, key));
        }

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

        var content = StrAny(args, "", "content", "text");
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Fail("content 不能为空。");

        var append = Str(args, "mode", "replace").Equals("append", StringComparison.OrdinalIgnoreCase);
        int before = c.WordCount;

        // ⚠ 快照必须存**在改内存之前**，不是"在写盘之前"。
        // Save() 序列化的就是 Project 这个对象：一旦先改了 c.Content，快照里存的
        // 就已经是新内容了——真覆盖错章时这份快照救不回任何东西（自检里那条
        // "快照记的是改写之前的内容"就是专门盯这个的）。
        s.Snapshot($"MCP {(append ? "追加" : "覆盖")}第{c.ChapterNumber}章前");

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
        var content = StrAny(args, "", "content", "text");

        s.Snapshot("MCP 新建章节前");   // 只在加进列表之前存一次，避免新建后重复存一份覆盖态快照
        p.Chapters.Add(new Chapter
        {
            ChapterNumber = next,
            Title = title,
            Content = content,
            Summary = Str(args, "summary").Trim(),
            CreatedDate = DateTime.Now,
            ModifiedDate = DateTime.Now,
            LastModified = DateTime.Now,
        });
        s.Save();

        return ToolResult.Ok($"已新建第{next}章「{title}」（{content.Length} 字）。");
    }

    private static readonly string[] BatchSettingKeys =
    {
        "full_outline", "outline",
        "chapter_outline",
        "characters", "character",
        "background",
        "writing_style", "style",
        "narrative_viewpoint", "viewpoint",
        "description",
    };

    public static ToolResult SetSetting(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var updates = new List<(string Field, string Text)>();

        var field = Str(args, "field").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(field))
        {
            if (!IsKnownSettingField(field))
                return ToolResult.Fail(
                    $"未知字段「{field}」。可选：full_outline / chapter_outline / characters / " +
                    "background / writing_style / narrative_viewpoint / description");
            updates.Add((field, StrAny(args, "", "text", "content", "value")));
        }

        // 支持批量一次性写入多项设定（例如同时传 full_outline、writing_style、narrative_viewpoint）
        foreach (var key in BatchSettingKeys)
        {
            if (Has(args, key))
            {
                updates.Add((key, Str(args, key)));
            }
        }

        if (updates.Count == 0)
        {
            return ToolResult.Fail(
                "未指定要修改的设定。可传 field + text，或直接传属性名（full_outline / chapter_outline / " +
                "characters / background / writing_style / narrative_viewpoint / description）。");
        }

        var label = updates.Count == 1 ? updates[0].Field : $"{updates.Count}项设定";
        s.Snapshot($"MCP 改写设定「{label}」前", lightweight: true);
        foreach (var (k, v) in updates)
            ApplySettingField(p, k, v);
        s.Save();

        if (updates.Count == 1)
            return ToolResult.Ok($"已更新「{updates[0].Field}」（{updates[0].Text.Length} 字）。");

        var summary = string.Join("、", updates.Select(u => $"{u.Field}({u.Text.Length}字)"));
        return ToolResult.Ok($"已批量更新 {updates.Count} 项设定：{summary}。");
    }

    private static bool IsKnownSettingField(string field) => field switch
    {
        "outline" or "full_outline" or "chapter_outline" or "characters" or "character"
            or "background" or "style" or "writing_style" or "viewpoint"
            or "narrative_viewpoint" or "description" => true,
        _ => false,
    };

    /// <summary>
    /// 把文本写进某项贯穿设定。抽出来是为了让 settings_set 和 ai_write 的写回走同一条路径——
    /// 两条路径各写一份 switch，迟早会漏掉一个字段。
    /// </summary>
    internal static bool ApplySettingField(NovelProject p, string field, string text)
    {
        switch (field.Trim().ToLowerInvariant())
        {
            case "outline":
            case "full_outline": p.FullOutline = text; return true;
            case "chapter_outline": p.ChapterOutline = text; return true;
            case "characters":
            case "character": p.CharacterSettings = text; return true;
            case "background": p.BackgroundSettings = text; return true;
            case "style":
            case "writing_style": p.WritingStyle = text; return true;
            case "viewpoint":
            case "narrative_viewpoint": p.NarrativeViewpoint = text; return true;
            case "description": p.Description = text; return true;
            default: return false;
        }
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

        var content = StrAny(args, "", "content", "text", "value");
        var append = Str(args, "mode", "replace").Equals("append", StringComparison.OrdinalIgnoreCase);
        s.Snapshot($"MCP 改写设定集「{ch.Title}」前", lightweight: true);   // 赋值之前存
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
        var output = StrAny(args, "", "output", "path", "filePath");
        if (string.IsNullOrWhiteSpace(output))
            return ToolResult.Fail("output（或 path）不能为空（导出文件的完整路径）。");

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

    // ==================================================================
    // 章节结构管理（改名 / 删除 / 重排）
    //
    // ★ 为什么必须给：这个场景里 agent 是操作员，用户只说一句话。
    // 「这两章合并一下」「第 5 章挪到前面」「这章删了重写」——agent 手上没有工具
    // 就只能回一句"我做不了"，协作当场断掉。
    // 删除是危险操作，所以删之前一律存快照（也正因为有 snapshot_restore 兜底，
    // 才敢把删除开放给 agent）。
    // ==================================================================

    public static ToolResult RenameChapter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var n = Int(args, "number", -1);
        var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
        if (c == null) return ToolResult.Fail($"没有第 {n} 章。用 chapters_list 看现有章节号。");

        var title = Str(args, "title").Trim();
        if (string.IsNullOrWhiteSpace(title)) return ToolResult.Fail("title 不能为空。");

        var old = c.Title;
        s.Snapshot($"MCP 重命名第{n}章前");
        c.Title = title;
        s.Save();

        return ToolResult.Ok($"第{n}章已改名：「{old}」→「{title}」");
    }

    public static ToolResult DeleteChapter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var n = Int(args, "number", -1);
        var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
        if (c == null) return ToolResult.Fail($"没有第 {n} 章。用 chapters_list 看现有章节号。");

        var title = c.Title;
        var words = c.WordCount;

        s.Snapshot($"MCP 删除第{n}章「{title}」前");
        p.Chapters.Remove(c);
        Renumber(p);
        s.Save();

        return ToolResult.Ok(
            $"已删除第{n}章「{title}」（{words} 字），其余章节已重排编号。\n" +
            "删之前存了快照，用 snapshot_list 找到它、snapshot_restore 可以撤回来。");
    }

    /// <summary>把某章移到新位置，其余顺移后重新编号。</summary>
    public static ToolResult ReorderChapter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var from = IntAny(args, -1, "number", "fromNumber");
        var to = Int(args, "toNumber", -1);
        if (from <= 0 || to <= 0)
            return ToolResult.Fail("number 和 toNumber 都要给（把第 number 章移到第 toNumber 个位置）。");

        var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == from);
        if (c == null) return ToolResult.Fail($"没有第 {from} 章。用 chapters_list 看现有章节号。");

        s.Snapshot($"MCP 调整第{from}章顺序前");
        var ordered = p.Chapters.OrderBy(x => x.ChapterNumber).ToList();
        ordered.Remove(c);
        ordered.Insert(Math.Clamp(to - 1, 0, ordered.Count), c);
        p.Chapters = ordered;
        Renumber(p);

        s.Save();

        return ToolResult.Ok($"「{c.Title}」已从 {from} 移到第 {c.ChapterNumber} 章，其余章节已重排。");
    }

    /// <summary>
    /// 章号重排成 1..n 连续。
    /// 实现收敛在 NovelProject.RenumberChapters 里：大纲窗口也要用同一套规则，
    /// 两处各写一份迟早会不一致（而"按章号排序再编号"的坑已经踩过一次）。
    /// </summary>
    private static void Renumber(NovelProject p) => p.RenumberChapters();

    // ==================================================================
    // 章节梗概
    // ==================================================================

    /// <summary>
    /// 写某一章的梗概。
    ///
    /// ★ 长篇能不能写完，几乎全看这一条。ai_write 给续写/审稿带的前情只取前 1~3 章正文
    /// 各 800 字——写到三四十章时，AI 对前面发生了什么基本是瞎的，人物性格、埋过的伏笔
    /// 全靠猜。梗概链才是把几十章串起来的东西：每章写完顺手存一句，后面任何一章都能
    /// 低成本拿到全书脉络。
    /// </summary>
    public static ToolResult SetChapterSummary(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var n = Int(args, "number", -1);
        var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
        if (c == null) return ToolResult.Fail($"没有第 {n} 章。用 chapters_list 看现有章节号。");

        var summary = StrAny(args, "", "summary", "content", "text").Trim();
        s.Snapshot($"MCP 改写第{n}章梗概前", lightweight: true);
        c.Summary = summary;
        s.Save();

        return ToolResult.Ok(string.IsNullOrWhiteSpace(summary)
            ? $"已清空第{n}章的梗概。"
            : $"已写入第{n}章梗概（{summary.Length} 字）。");
    }

    // ==================================================================
    // 版本快照（回滚）
    // ==================================================================

    public static ToolResult ListSnapshots(Session s)
    {
        if (string.IsNullOrEmpty(s.Path)) return ToolResult.Fail("还没有打开项目。");

        var entries = new ProjectSnapshotManager(s.Path).LoadIndex();
        if (entries.Count == 0)
            return ToolResult.Ok("这个项目还没有快照。写入正文、改写设定、AI 生成时都会自动存。");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"共 {entries.Count} 个快照（时间正序，最新在最后）：");
        foreach (var e in entries)
            sb.AppendLine($"  {e.Id}   {e.Timestamp:MM-dd HH:mm:ss}   {e.Description}");
        sb.AppendLine("\n用 snapshot_restore 传 id 恢复。");
        return ToolResult.Ok(sb.ToString());
    }

    public static ToolResult RestoreSnapshot(Session s, JsonElement args)
    {
        if (string.IsNullOrEmpty(s.Path)) return ToolResult.Fail("还没有打开项目。");

        var id = Str(args, "id").Trim();
        if (id.Length == 0) return ToolResult.Fail("id 不能为空。先用 snapshot_list 看有哪些版本。");

        var mgr = new ProjectSnapshotManager(s.Path);
        var entry = mgr.LoadIndex()
            .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return ToolResult.Fail($"没有 id 为「{id}」的快照。用 snapshot_list 看列表。");

        var snapshot = mgr.LoadSnapshot(entry);
        if (snapshot == null) return ToolResult.Fail($"快照 {id} 读取失败（文件可能已损坏）。");

        // 恢复之前先给"当前状态"留一份——否则这一步自己就成了不可撤销的操作
        s.Snapshot($"恢复到快照 {id} 之前");

        // ⚠ 快照里存的 FilePath 是**存快照那一刻**的路径，过期了就会写到别处
        // （同一个坑在打开副本时踩过），必须覆盖成当前实际路径。
        snapshot.FilePath = s.Path;
        s.Replace(snapshot);

        return ToolResult.Ok(
            $"已恢复到快照 {id}（{entry.Timestamp:MM-dd HH:mm:ss} · {entry.Description}）。\n" +
            "恢复前的状态也存成了新快照，反悔就再恢复回去。");
    }

    // ==================================================================
    // 结构化人物卡
    // ==================================================================

    /// <summary>
    /// 人物档案（NovelProject.Characters）。
    /// 与「人物设定」自由文本是两回事：这里是一张张带字段的卡，
    /// 用户说"给主角加个口头禅""配角年龄改小两岁"，改的是具体的字段，
    /// 不是把几千字的设定文本重新生成一遍。
    /// </summary>
    public static ToolResult ListCharacters(NovelProject p)
    {
        if (p.Characters.Count == 0)
            return ToolResult.Ok(
                "还没有结构化人物卡。（settings_get 里那份「人物设定」是自由文本，和这里是两回事）\n" +
                "用 character_upsert 建一张，例如 name=林寒 role=主角。");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"「{p.ProjectName}」共 {p.Characters.Count} 张人物卡：");
        foreach (var c in p.Characters)
        {
            var bits = new List<string>();
            if (c.Age > 0) bits.Add($"{c.Age}岁");
            if (!string.IsNullOrWhiteSpace(c.Gender)) bits.Add(c.Gender);
            if (!string.IsNullOrWhiteSpace(c.Role)) bits.Add(c.Role);
            if (!string.IsNullOrWhiteSpace(c.Occupation)) bits.Add(c.Occupation);
            sb.AppendLine($"  · {c.Name}{(bits.Count > 0 ? "（" + string.Join(" · ", bits) + "）" : "")}   id={ShortId(c.CharacterId)}");
            if (!string.IsNullOrWhiteSpace(c.Appearance))
                sb.AppendLine($"      外貌：{Session.Truncate(c.Appearance, 60)}");
            if (!string.IsNullOrWhiteSpace(c.Personality))
                sb.AppendLine($"      性格：{Session.Truncate(c.Personality, 60)}");
            if (!string.IsNullOrWhiteSpace(c.Abilities))
                sb.AppendLine($"      能力：{Session.Truncate(c.Abilities, 60)}");
            if (!string.IsNullOrWhiteSpace(c.Relationships))
                sb.AppendLine($"      关系：{Session.Truncate(c.Relationships, 60)}");
            if (!string.IsNullOrWhiteSpace(c.Background))
                sb.AppendLine($"      背景：{Session.Truncate(c.Background, 60)}");
            if (!string.IsNullOrWhiteSpace(c.Notes))
                sb.AppendLine($"      备注：{Session.Truncate(c.Notes, 60)}");
        }
        sb.AppendLine("\ncharacter_upsert 给 id 就是改，不给则按姓名匹配，都没有就新建。");
        return ToolResult.Ok(sb.ToString());
    }

    public static ToolResult UpsertCharacter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var name = Str(args, "name").Trim();
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 不能为空。");

        var id = Str(args, "id").Trim();
        var c = id.Length > 0
            ? p.Characters.FirstOrDefault(x => x.CharacterId.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            : null;
        c ??= p.Characters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        var isNew = c == null;
        s.Snapshot($"MCP {(isNew ? "新建" : "修改")}人物「{name}」前", lightweight: true);

        c ??= new Character { Name = name };
        if (isNew)
            p.Characters.Add(c);
        else
            c.Name = name;

        // ★ 只覆盖**传了的**字段。agent 常常只想改一条性格，
        // 无脑整体赋值会把没传的字段清空——那种静默丢数据最难查。
        if (Has(args, "role")) c.Role = Str(args, "role");
        if (Has(args, "age")) c.Age = Int(args, "age", c.Age);
        if (Has(args, "gender")) c.Gender = Str(args, "gender");
        if (Has(args, "occupation")) c.Occupation = Str(args, "occupation");
        if (Has(args, "appearance")) c.Appearance = Str(args, "appearance");
        if (Has(args, "personality")) c.Personality = Str(args, "personality");
        if (Has(args, "background")) c.Background = Str(args, "background");
        if (Has(args, "abilities")) c.Abilities = Str(args, "abilities");
        if (Has(args, "relationships")) c.Relationships = Str(args, "relationships");
        else if (Has(args, "relations")) c.Relationships = Str(args, "relations");
        if (Has(args, "notes")) c.Notes = Str(args, "notes");
        else if (Has(args, "note")) c.Notes = Str(args, "note");

        s.Save();

        return ToolResult.Ok($"已{(isNew ? "新建" : "更新")}人物卡「{c.Name}」 id={ShortId(c.CharacterId)}" +
                             (isNew ? "（没传的字段留空，之后可再改）" : "（只改了本次传入的字段）"));
    }

    public static ToolResult DeleteCharacter(Session s, NovelProject p, JsonElement args)
    {
        if (s.IsStale(out var stale)) return stale;

        var id = Str(args, "id").Trim();
        var name = Str(args, "name").Trim();
        if (id.Length == 0 && name.Length == 0)
            return ToolResult.Fail("给 id 或 name 其中一个。");

        var c = id.Length > 0
            ? p.Characters.FirstOrDefault(x => x.CharacterId.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            : p.Characters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (c == null) return ToolResult.Fail($"没有匹配的人物。用 characters_list 看现有卡片。");

        s.Snapshot($"MCP 删除人物「{c.Name}」前");
        p.Characters.Remove(c);
        s.Save();

        return ToolResult.Ok($"已删除人物卡「{c.Name}」（删前已存快照）。");
    }

    private static bool Has(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out _);

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];

    // ==================================================================
    // 项目进度画像
    // ==================================================================

    /// <summary>
    /// 一次调用拿到"这本书现在什么状态"。
    /// 用户最常问的就是「写到哪了」「还差多少」「接下来该干嘛」——
    /// 没有这个工具，agent 得自己 chapters_list + settings_get 各调一次再拼，
    /// 而且它拼不出"下一步该做什么"这种判断。
    /// </summary>
    public static ToolResult ProjectStatus(Session s, NovelProject p)
    {
        var chapters = p.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        var total = chapters.Sum(c => c.WordCount);
        var empty = chapters.Count(c => c.WordCount == 0);
        var withSummary = chapters.Count(c => !string.IsNullOrWhiteSpace(c.Summary));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【{p.ProjectName}】");
        sb.AppendLine($"章节：{chapters.Count} 章 · 共 {total} 字" +
                      (empty > 0 ? $" · {empty} 章还是空的" : ""));

        if (chapters.Count > 0)
        {
            var longest = chapters.OrderByDescending(c => c.WordCount).First();
            sb.AppendLine($"最长：第{longest.ChapterNumber}章「{longest.Title}」{longest.WordCount} 字" +
                          $" · 平均 {total / chapters.Count} 字/章");
        }

        sb.AppendLine($"梗概：{withSummary}/{chapters.Count} 章有梗概");

        var filled = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.FullOutline)) filled.Add("全文大纲");
        if (!string.IsNullOrWhiteSpace(p.ChapterOutline)) filled.Add("章节大纲");
        if (!string.IsNullOrWhiteSpace(p.CharacterSettings) || p.Characters.Count > 0)
            filled.Add(p.Characters.Count > 0 && string.IsNullOrWhiteSpace(p.CharacterSettings)
                ? $"人物设定（人物卡 {p.Characters.Count} 张）"
                : "人物设定");
        if (!string.IsNullOrWhiteSpace(p.BackgroundSettings)) filled.Add("背景设定");
        if (!string.IsNullOrWhiteSpace(p.WritingStyle)) filled.Add("文风设定");
        if (!string.IsNullOrWhiteSpace(p.NarrativeViewpoint)) filled.Add("叙事视角");
        sb.AppendLine($"设定：{(filled.Count == 0 ? "（一项都没填）" : string.Join("、", filled))}");

        sb.AppendLine($"设定集：{p.SettingsBook?.Chapters.Count ?? 0} 章 · 人物卡：{p.Characters.Count} 张 · " +
                      $"快照：{new ProjectSnapshotManager(s.Path).LoadIndex().Count} 个");

        // 下一步建议——用户问"接下来干嘛"时，agent 不该自己瞎猜顺序
        sb.AppendLine();
        sb.AppendLine("下一步建议：" + SuggestNext(p, chapters, empty, filled.Count));

        return ToolResult.Ok(sb.ToString());
    }

    private static string SuggestNext(NovelProject p, List<Chapter> chapters, int empty, int filledSettings)
    {
        if (!string.IsNullOrWhiteSpace(p.FullOutline) == false)
            return "先定全文大纲：ai_write task=outline";
        if (chapters.Count == 0)
            return "大纲有了，开始建章节：chapter_create（或 ai_write task=chapter_outline 先出章节大纲）";
        if (empty > 0)
        {
            var first = chapters.First(c => c.WordCount == 0);
            return $"写第{first.ChapterNumber}章「{first.Title}」：ai_write task=continue number={first.ChapterNumber}";
        }
        if (chapters.Count(c => string.IsNullOrWhiteSpace(c.Summary)) > 0)
            return "给写完的章补梗概（chapter_summary_set）——长篇跨章连贯靠它";
        return "全部章节都有内容了，可以 ai_write task=review 做一轮一致性审稿，再 project_export 导出";
    }

    // ==================================================================
    // AI 写作记忆
    // ==================================================================

    /// <summary>
    /// 读写 AI 写作记忆（&lt;项目目录&gt;/.ai_memory/memory.md）。
    /// 这个场景里用户一直在纠正和指导——"别写死主角""对话别太长""这段太啰嗦"。
    /// 这些话如果只停留在对话里，下一轮就忘了。让 agent 把它们沉淀进记忆，
    /// 后面每次生成都会自动带上（ai_write 内部已注入）。
    /// </summary>
    public static ToolResult GetMemory(NovelProject p)
    {
        try
        {
            var mem = new AiMemoryManager(p.FilePath);
            mem.Load();
            var text = mem.GetRawMemory();
            return ToolResult.Ok(string.IsNullOrWhiteSpace(text)
                ? "还没有 AI 写作记忆。"
                : $"AI 写作记忆（{text.Length} 字）：\n\n{text}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"读取记忆失败：{ex.Message}");
        }
    }

    public static ToolResult SetMemory(Session s, NovelProject p, JsonElement args)
    {
        var text = StrAny(args, "", "text", "content", "value");
        var append = Str(args, "mode", "replace").Equals("append", StringComparison.OrdinalIgnoreCase);

        try
        {
            var mem = new AiMemoryManager(p.FilePath);
            mem.Load();
            var old = mem.GetRawMemory();
            var final = append && !string.IsNullOrWhiteSpace(old)
                ? old.TrimEnd() + "\n" + text
                : text;
            mem.SetMemory(final);

            return ToolResult.Ok($"已{(append ? "追加到" : "覆盖")}AI 写作记忆（{final.Length} 字）。" +
                                 "之后每次生成都会自动带上它。");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"写入记忆失败：{ex.Message}");
        }
    }
}
