using System.Text.Json;
using 编辑器;
using 编辑器.Services;
using Microsoft.AspNetCore.SignalR;

namespace 编辑器.Web;

/// <summary>
/// 服务器上的工作区：当前打开的那本书，以及围绕它的全部读写。
///
/// 并发模型：
/// - **人与人 / 人与网页**：单人分时，一把锁（<see cref="_gate"/>）保证"人写"和
///   "agent 写"不会同时落盘。
/// - **agent 与 agent**：互不干扰。每个 agent 有自己的 MCP 会话（按令牌分，
///   见 <see cref="编辑器.Mcp.McpServer.SessionFor"/>），各开各的书，本类只负责
///   "网页上正在看的那一本"。
///
/// 多账户：每个「主体」（自用 / 试用+个人码）一个实例（见 <see cref="WorkspaceRegistry"/>），
/// 作品库、聊天记忆、快照、AI API 配置随主体隔离，互不可见。
/// 自用主体的书库与 MCP / 桌面端**同一个**（见 <see cref="ResolveBooksDir"/>）。
/// </summary>
public sealed class WorkspaceService
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly object _gate = new();

    private NovelProject? _project;

    public WorkspaceService(IHubContext<LiveHub> hub, string subject)
    {
        _hub = hub;
        Subject = subject;
        BooksDir = ResolveBooksDir(subject);
        Directory.CreateDirectory(BooksDir);
    }

    /// <summary>主体标识："self" 或 "trial:&lt;四位码&gt;"。</summary>
    public string Subject { get; }

    public bool IsSelf => Subject == "self";

    public string BooksDir { get; }

    /// <summary>
    /// 主体的工作区根目录（书库在其下 books\）。API 配置等按主体隔离的文件也放这里。
    /// 自用：D:\TdxClaw\self（书库即 MCP / 桌面端的 ProjectsDirectory，paths.json 指向它）；
    /// 试用：D:\TdxClaw\trial-&lt;码&gt;（agent 看不到）。
    /// </summary>
    public static string RootDirOf(string subject)
    {
        var baseDir = Path.Combine("D:\\", "TdxClaw");
        // "trial:1234" → 目录 "trial-1234"（码外层已校验为四位数字，无路径注入）
        var dir = subject == "self"
            ? Path.Combine(baseDir, "self")
            : Path.Combine(baseDir, "trial-" + subject.Split(':').Last());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 书库目录。**自用主体必须与 MCP / 桌面端解析出同一个目录**——否则网页版的书在
    /// 一处，agent 的书在另一处，两边各写各的，「你在网页上写、agent 通过 MCP 改
    /// 同一本书」这个核心场景根本无从谈起：界面永远看不到 agent 动的是哪本书。
    ///
    /// 自用走 MCP 侧同一份实现（paths.json 的 ProjectsDirectory 指到 D:\TdxClaw\self\books）；
    /// 试用主体是自己的独立目录，agent 本来就不该看到。
    /// </summary>
    private static string ResolveBooksDir(string subject)
    {
        var dir = subject == "self"
            ? 编辑器.Mcp.NovelTools.ResolveProjectsDirectory()
            : Path.Combine(RootDirOf(subject), "books");
        Directory.CreateDirectory(dir);   // 首次运行时目录可能还不存在
        return dir;
    }

    public NovelProject? Current => _project;

    // ==================================================================
    // 万能聊天的对话记忆
    // ==================================================================

    private ChatSessionStore? _chat;

    /// <summary>
    /// 当前书的对话记忆。与桌面端**共用同一个 ChatSessionStore 与同一份落盘文件**
    /// （&lt;项目目录&gt;/.chat/session.json），所以两边看到的是同一段对话。
    /// </summary>
    public ChatSessionStore? Chat => _chat;

    /// <summary>
    /// 重挂对话记忆。跟着 _project 一起换 —— 换了书还留着上一本的对话，
    /// AI 会把上一本的情节当成本书的上下文，这是最糟的一种"记忆出错"。
    /// </summary>
    private void AttachChat(NovelProject p)
    {
        try { _chat = new ChatSessionStore(p.FilePath); }
        catch { _chat = null; }   // 记忆文件读坏了不该让整个工作区打不开
    }

    public bool ClearChat()
    {
        if (_chat == null) return false;
        _chat.Clear();
        return true;
    }

    /// <summary>
    /// 对话记忆的规模提示：轮数 / 被摘要掉的轮数 / 估算占用 / 该模型的窗口。
    /// 桌面端面板上那行「聊天记忆：N 轮…」读的就是同一批数字。
    /// </summary>
    public object ChatInfo(string model)
    {
        var config = new CompactPolicyConfig
        {
            MaxOutputTokens = ChatContextCompactor.DefaultMaxOutputTokens,
        };
        int window = CompactPolicy.ResolveContextWindow(config, model);
        var summary = _chat?.BuildSummaryBlock() ?? "";
        int used = (_chat?.EstimatedTokens ?? 0) + TokenEstimator.Estimate(summary);
        return new
        {
            rounds = _chat?.RoundCount ?? 0,
            summarizedRounds = _chat?.SummarizedRounds ?? 0,
            used,
            window,
            summary,
            empty = _chat?.IsEmpty ?? true,
            lastActivity = _chat?.LastActivityUtc,
        };
    }

    // ==================================================================
    // 书目
    // ==================================================================

    public sealed record BookMeta(string FileName, string Name, int Chapters, int Words, DateTime Modified);

    public List<BookMeta> ListBooks()
    {
        var list = new List<BookMeta>();
        foreach (var file in Directory.GetFiles(BooksDir, "*.tdxproj").OrderByDescending(f => File.GetLastWriteTime(f)))
        {
            try
            {
                var p = NovelProject.Load(file);
                list.Add(new BookMeta(
                    Path.GetFileName(file),
                    p.ProjectName,
                    p.Chapters.Count,
                    p.Chapters.Sum(c => c.WordCount),
                    File.GetLastWriteTime(file)));
            }
            catch
            {
                // 单个项目读坏了不该让整个列表打不开
            }
        }
        return list;
    }

    public BookMeta CreateBook(string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "未命名";

        var path = Path.Combine(BooksDir, safe + ".tdxproj");
        int n = 2;
        while (File.Exists(path)) path = Path.Combine(BooksDir, $"{safe}（{n++}）.tdxproj");

        var p = new NovelProject { ProjectName = safe, FilePath = path };
        p.Chapters.Add(new Chapter
        {
            ChapterNumber = 1,
            Title = "第一章",
            Content = "",
            CreatedDate = DateTime.Now,
            ModifiedDate = DateTime.Now,
            LastModified = DateTime.Now,
        });
        p.Save();
        return new BookMeta(Path.GetFileName(path), safe, 1, 0, DateTime.Now);
    }

    public void OpenBook(string fileName)
    {
        // 只接受文件名，不接受路径——防止 ../../ 这类遍历读到服务器上的任意文件
        if (fileName.Contains("..") || Path.IsPathRooted(fileName))
            throw new ArgumentException("非法的项目名");

        var path = Path.Combine(BooksDir, fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("没有这本书", fileName);

        lock (_gate)
        {
            var loaded = NovelProject.Load(path);
            _project = loaded;
            AttachChat(loaded);
        }
    }

    // ==================================================================
    // 章节读写
    // ==================================================================

    public Chapter? GetChapter(int number)
    {
        lock (_gate)
            return _project?.Chapters.FirstOrDefault(c => c.ChapterNumber == number);
    }

    /// <summary>
    /// 保存一章正文。
    /// <paramref name="stamp"/> 是前端拿到的"打开时"版本号：如果书已经被 agent 改过，
    /// 前端那一版就是旧的，此时拒绝覆盖并让前端重新拉——避免把 agent 刚写的内容冲掉。
    /// </summary>
    public bool SaveChapter(int number, string content, long? stamp = null)
    {
        lock (_gate)
        {
            if (_project == null) return false;
            var ch = _project.Chapters.FirstOrDefault(c => c.ChapterNumber == number);
            if (ch == null) return false;

            if (stamp.HasValue && Stamp() != stamp.Value) return false;

            ch.Content = content;
            ch.ModifiedDate = DateTime.Now;
            ch.LastModified = DateTime.Now;
            _project.Save();
            return true;
        }
    }

    /// <summary>重新从磁盘载入（agent 或别的页面改了书之后用）。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            var path = _project?.FilePath;
            if (path != null && File.Exists(path))
            {
                var loaded = NovelProject.Load(path);
                _project = loaded;
                AttachChat(loaded);
            }
        }
    }

    /// <summary>
    /// 当前打开的那本书还在不在？不在就把状态清掉，返回被清掉的书名（没有则 null）。
    ///
    /// ★ 为什么需要它：网页端的「正打开的书不删」检查只看**自己**的当前书，
    ///   而 agent 走 MCP 删书时查的是**agent 那边的会话**——它不知道网页上正
    ///   看着哪一本。于是完全可能出现"agent 删了用户正在看的那本"，
    ///   _project 抱着一个已删除的文件对象；用户下一次点保存，Save() 会
    ///   把这个文件**重新写出来**，等于删了个寂寞。
    ///
    ///   做成惰性自愈：每次读当前状态时顺手看一眼磁盘，谁先发现谁纠正。
    /// </summary>
    public string? DropIfMissing()
    {
        lock (_gate)
        {
            var path = _project?.FilePath;
            if (path == null || File.Exists(path)) return null;

            var name = _project!.ProjectName;
            _project = null;
            _chat = null;
            return name;
        }
    }

    public long Stamp()
    {
        lock (_gate)
            return _project == null ? 0 : new FileInfo(_project.FilePath).LastWriteTimeUtc.Ticks;
    }

    public Chapter AddChapter(string? title = null)
    {
        lock (_gate)
        {
            if (_project == null) throw new InvalidOperationException("还没打开项目");
            var next = _project.Chapters.Count == 0 ? 1 : _project.Chapters.Max(c => c.ChapterNumber) + 1;
            var ch = new Chapter
            {
                ChapterNumber = next,
                Title = string.IsNullOrWhiteSpace(title) ? $"第{next}章" : title!,
                Content = "",
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now,
                LastModified = DateTime.Now,
            };
            _project.Chapters.Add(ch);
            _project.Save();
            return ch;
        }
    }

    public void RenameChapter(int number, string title)
    {
        lock (_gate)
        {
            var ch = _project?.Chapters.FirstOrDefault(c => c.ChapterNumber == number);
            if (ch == null) return;
            ch.Title = title;
            _project!.Save();
        }
    }

    public void DeleteChapter(int number)
    {
        lock (_gate)
        {
            var ch = _project?.Chapters.FirstOrDefault(c => c.ChapterNumber == number);
            if (ch == null) return;
            _project!.Chapters.Remove(ch);
            _project.RenumberChapters();      // 与桌面版/MCP 同一套规则
            _project.Save();
        }
    }

    public void MoveChapter(int number, int delta)
    {
        lock (_gate)
        {
            var ch = _project?.Chapters.FirstOrDefault(c => c.ChapterNumber == number);
            if (ch == null) return;
            _project!.MoveChapter(ch, delta);
            _project.Save();
        }
    }

    // ==================================================================
    // 统计与快照（直接复用桌面版的服务类）
    // ==================================================================

    public object? Stats()
    {
        var path = _project?.FilePath;
        if (path == null) return null;

        var s = new WritingStatsService(path);
        s.Load();
        return new
        {
            today = s.TodayWords,
            streak = s.StreakDays,
            activeDays = s.ActiveDays,
            recent = s.RecentDays(14).Select(d => new { date = d.Date, words = d.Words }),
        };
    }

    /// <summary>
    /// 恢复到某个快照。
    /// ⚠ 必须把快照里的 FilePath 换成当前的：快照存的是"存快照那一刻"的路径，
    /// 项目被移动过就是过期值，不覆盖的话后续 Save 会写到别处去
    /// （桌面版打开副本时踩过同一个坑）。
    /// </summary>
    public bool RestoreSnapshot(string id)
    {
        lock (_gate)
        {
            var path = _project?.FilePath;
            if (path == null) return false;

            var mgr = new ProjectSnapshotManager(path);
            var entry = mgr.LoadIndex().FirstOrDefault(e => e.Id == id);
            if (entry == null) return false;

            var snap = mgr.LoadSnapshot(entry);
            if (snap == null) return false;

            snap.FilePath = path;
            snap.Save();
            _project = snap;
            AttachChat(snap);
            return true;
        }
    }

    public List<object> Snapshots()
    {
        var path = _project?.FilePath;
        if (path == null) return new();

        return new ProjectSnapshotManager(path).LoadIndex()
            .Select(e => new { id = e.Id, description = e.Description, timestamp = e.Timestamp })
            .Cast<object>()
            .ToList();
    }

    /// <summary>
    /// 手动拍一张快照。桌面端在"续写前 / 润色前 / 人名生成前 / 万能写作前"都会调，
    /// 网页端的 AI 生成走的是同一个入口——AI 失败或写坏了能回滚。
    /// </summary>
    public void TakeSnapshot(string description)
    {
        lock (_gate)
        {
            var path = _project?.FilePath;
            if (path == null) return;
            try { new ProjectSnapshotManager(path).SaveSnapshot(_project!, description); }
            catch { /* 快照失败不该阻断生成 */ }
        }
    }

    /// <summary>
    /// 给定章节之前的前情梗概 + 上一章末尾（续写衔接用）。与桌面版
    /// NovelProject.BuildPriorChapterBrief 同源，网页端不另写一套摘要逻辑。
    /// </summary>
    public string PriorBrief(int beforeChapterNumber)
    {
        var p = _project;
        if (p == null) return "";
        try { return p.BuildPriorChapterBrief(beforeChapterNumber, includePrevTail: true); }
        catch { return ""; }
    }

    // ==================================================================
    // 人物卡
    // ==================================================================

    public List<Character> Characters()
    {
        lock (_gate)
            return _project?.Characters ?? new List<Character>();
    }

    public void UpsertCharacter(Character c)
    {
        lock (_gate)
        {
            if (_project == null) return;

            var exist = _project.Characters.FirstOrDefault(x => x.CharacterId == c.CharacterId);
            if (exist != null)
                _project.Characters[_project.Characters.IndexOf(exist)] = c;
            else
                _project.Characters.Add(c);

            _project.Save();
        }
    }

    public void DeleteCharacter(string id)
    {
        lock (_gate)
        {
            var c = _project?.Characters.FirstOrDefault(x => x.CharacterId == id);
            if (c == null) return;
            _project!.Characters.Remove(c);
            _project.Save();
        }
    }

    // ==================================================================
    // AI 写作记忆（复用桌面版 AiMemoryManager，与界面共用同一份文件）
    // ==================================================================

    public string Memory()
    {
        var path = _project?.FilePath;
        if (path == null) return "";

        var m = new AiMemoryManager(path);
        m.Load();
        return m.GetMemoryContext();
    }

    public void SetMemory(string content)
    {
        var path = _project?.FilePath;
        if (path == null) return;

        var m = new AiMemoryManager(path);
        m.Load();
        m.SetMemory(content ?? "");
    }

    // ==================================================================
    // 单章梗概 / 世界设定 / 角色出场
    // ==================================================================

    /// <summary>改单章梗概。大纲卡片上直接编辑的那一块。</summary>
    public bool SetChapterSummary(int number, string summary)
    {
        lock (_gate)
        {
            var ch = GetChapter(number);
            if (ch == null) return false;
            ch.Summary = summary ?? "";
            _project!.Save();
            return true;
        }
    }

    public object? WorldSetting()
    {
        var w = _project?.WorldSetting;
        if (w == null) return null;
        return new
        {
            worldName = w.WorldName, timePeriod = w.TimePeriod, location = w.Location,
            background = w.Background, magicSystem = w.MagicSystem, technologyLevel = w.TechnologyLevel,
        };
    }

    public void UpdateWorldSetting(JsonElement w)
    {
        lock (_gate)
        {
            if (_project == null) return;
            var world = _project.WorldSetting ??= new WorldSetting();
            world.WorldName = TryGet(w, "worldName") ?? world.WorldName;
            world.TimePeriod = TryGet(w, "timePeriod") ?? world.TimePeriod;
            world.Location = TryGet(w, "location") ?? world.Location;
            world.Background = TryGet(w, "background") ?? world.Background;
            world.MagicSystem = TryGet(w, "magicSystem") ?? world.MagicSystem;
            world.TechnologyLevel = TryGet(w, "technologyLevel") ?? world.TechnologyLevel;
            _project.Save();
        }
    }

    private static string? TryGet(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>角色出场统计：谁在哪几章露过面、连续缺席几章（对照设定集查漏洞用）。</summary>
    public List<object> CharacterAppearances()
    {
        lock (_gate)
        {
            if (_project == null) return new();
            return CharacterAppearanceService.Analyze(_project)
                .Select(s => (object)new
                {
                    name = s.Name,
                    chapters = s.Chapters,
                    totalHits = s.TotalHits,
                    lastChapter = s.LastChapter,
                    absentStreak = s.LastChapter == null ? (int?)null : s.AbsentStreak(s.LastChapter.Value),
                })
                .ToList();
        }
    }

    /// <summary>
    /// 同一份出场统计的**文本报告**形式，与桌面端「角色出场」按钮弹窗里的内容同源
    /// （CharacterAppearanceService.FormatReport）。列表给界面看，报告给「复制带走 /
    /// 贴进设定集」用——一个数据源两种形态，不另算一套。
    /// </summary>
    public (string Title, string Report, int Count) CharacterAppearanceReport()
    {
        lock (_gate)
        {
            if (_project == null) return ("", "", 0);
            var stats = CharacterAppearanceService.Analyze(_project);
            var title = _project.Chapters.Count > 0
                ? $"{_project.ProjectName}（共 {_project.Chapters.Count} 章）"
                : _project.ProjectName;
            return (title, CharacterAppearanceService.FormatReport(_project, stats), stats.Count);
        }
    }

    // ==================================================================
    // 文献库（论文场景；与桌面版共用同一份 LiteratureLibrary 数据）
    // ==================================================================

    public List<LiteratureEntry> Literature()
    {
        lock (_gate)
            return _project?.LiteratureLibrary ?? new List<LiteratureEntry>();
    }

    public void UpsertLiterature(LiteratureEntry entry)
    {
        lock (_gate)
        {
            if (_project == null) return;
            var lib = _project.LiteratureLibrary;

            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = Guid.NewGuid().ToString("N");

            var idx = lib.FindIndex(x => x.Id == entry.Id);
            if (idx >= 0) lib[idx] = entry;
            else
            {
                // CitationKey 是 AI 引用 [n] 的锚点，为空时给个兜底
                if (string.IsNullOrWhiteSpace(entry.CitationKey))
                    entry.CitationKey = "ref" + (lib.Count + 1);
                lib.Add(entry);
            }
            _project.Save();
        }
    }

    public bool DeleteLiterature(string id)
    {
        lock (_gate)
        {
            if (_project == null) return false;
            var lib = _project.LiteratureLibrary;
            var removed = lib.RemoveAll(x => x.Id == id);
            if (removed > 0) _project.Save();
            return removed > 0;
        }
    }

    /// <summary>导入 BibTeX 文本（Zotero / 知网导出的 .bib）。返回导入条数。</summary>
    public int ImportBibtex(string bibText)
    {
        lock (_gate)
        {
            if (_project == null) return 0;
            var parsed = BibtexParser.Parse(bibText ?? "");
            if (parsed.Count == 0) return 0;

            var lib = _project.LiteratureLibrary;
            foreach (var e in parsed)
            {
                // 同 DOI / 同 CitationKey 视为重复，不重复入库
                var dup = lib.Any(x =>
                    (!string.IsNullOrEmpty(e.Doi) && x.Doi == e.Doi) ||
                    (!string.IsNullOrEmpty(e.CitationKey) && x.CitationKey == e.CitationKey));
                if (!dup) lib.Add(e);
            }
            _project.Save();
            return parsed.Count;
        }
    }

    /// <summary>GB/T 7714-2015 顺序编码制参考文献表（复制到论文里直接用）。</summary>
    public string GbtReferenceList()
    {
        lock (_gate)
        {
            if (_project == null) return "";
            return LiteratureFormatter.FormatReferenceList(_project.LiteratureLibrary);
        }
    }

    // ==================================================================
    // 设定集（与桌面版同一本 SettingsBook，模板补建逻辑共用）
    // ==================================================================

    public object? SettingsBook()
    {
        lock (_gate)
        {
            var p = _project;
            if (p == null) return null;

            int before = p.SettingsBook?.Chapters.Count ?? -1;
            SettingsBookTemplates.EnsureBook(p);
            if (p.SettingsBook!.Chapters.Count != before) p.Save();   // 模板补了章才落盘

            return new
            {
                title = p.SettingsBook.Title,
                subtitle = p.SettingsBook.Subtitle,
                chapters = p.SettingsBook.Chapters.Select(c => new
                {
                    chapterId = c.ChapterId,
                    sourceKey = c.SourceKey,
                    sourceLabel = SettingsBookTemplates.SourceLabel(c.SourceKey),
                    title = c.Title,
                    content = c.Content,
                    includeInExport = c.IncludeInExport,
                    isAiGenerated = c.IsAiGenerated,
                    modified = c.ModifiedDate,
                }),
            };
        }
    }

    /// <summary>改设定集某章：内容或"是否入导出"。全 null 时视为没改动。</summary>
    public bool UpdateSettingsBookChapter(string chapterId, string? content, bool? includeInExport)
    {
        lock (_gate)
        {
            var c = _project?.SettingsBook?.Chapters.FirstOrDefault(x => x.ChapterId == chapterId);
            if (c == null) return false;

            if (content != null) { c.Content = content; c.ModifiedDate = DateTime.Now; }
            if (includeInExport.HasValue) c.IncludeInExport = includeInExport.Value;
            _project!.Save();
            return true;
        }
    }

    /// <summary>AI 生成设定集某章后的写回（桌面版 GenerateSettingsBookChapterAsync 的收尾同款）。</summary>
    public void SaveSettingsBookChapterContent(string chapterId, string content)
    {
        lock (_gate)
        {
            var c = _project?.SettingsBook?.Chapters.FirstOrDefault(x => x.ChapterId == chapterId);
            if (c == null) return;
            c.Content = content;
            c.IsAiGenerated = true;
            c.ModifiedDate = DateTime.Now;
            _project!.Save();
        }
    }

    // ==================================================================
    // 全书查找 / 替换（桌面版 FindReplaceWindow 的"全书"范围）
    // ==================================================================

    /// <summary>全书查找：每章命中数 + 少量上下文片段（供结果列表展示）。</summary>
    public List<object> FindAll(string query, bool caseSensitive)
    {
        lock (_gate)
        {
            if (_project == null || string.IsNullOrEmpty(query)) return new List<object>();
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var result = new List<object>();

            foreach (var c in _project.Chapters.OrderBy(c => c.ChapterNumber))
            {
                var text = c.Content ?? "";
                var hits = new List<string>();
                int i = 0, count = 0;
                while ((i = text.IndexOf(query, i, cmp)) >= 0)
                {
                    count++;
                    if (hits.Count < 5)
                    {
                        var s = Math.Max(0, i - 18);
                        var e = Math.Min(text.Length, i + query.Length + 18);
                        hits.Add((s > 0 ? "…" : "")
                                 + text[s..e].Replace('\n', ' ').Replace('\r', ' ')
                                 + (e < text.Length ? "…" : ""));
                    }
                    i += query.Length;
                }
                if (count > 0)
                    result.Add(new { n = c.ChapterNumber, title = c.Title, count, hits });
            }
            return result;
        }
    }

    /// <summary>
    /// 全书替换。替换前自动拍快照——跨章替换靠 Ctrl+Z 救不回来，
    /// 桌面版同样在替换前 TakeSnapshot（FastReplaceAll 路径）。
    /// </summary>
    public int ReplaceAll(string query, string replacement, bool caseSensitive)
    {
        lock (_gate)
        {
            var path = _project?.FilePath;
            if (_project == null || path == null || string.IsNullOrEmpty(query)) return 0;

            try { new ProjectSnapshotManager(path).SaveSnapshot(_project, "全书替换前备份"); }
            catch { /* 快照失败也要让替换继续 */ }

            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var total = 0;
            foreach (var c in _project.Chapters)
            {
                var text = c.Content ?? "";
                if (text.Length == 0) continue;
                var n = CountOccurrences(text, query, cmp);
                if (n == 0) continue;
                c.Content = text.Replace(query, replacement, cmp);
                total += n;
            }
            if (total > 0) _project.Save();
            return total;
        }
    }

    private static int CountOccurrences(string text, string q, StringComparison cmp)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(q, i, cmp)) >= 0) { n++; i += q.Length; }
        return n;
    }

    // ==================================================================
    // 广播：让网页立刻看到改动（人自己改的，或 agent 改的）
    // ==================================================================

    public async Task BroadcastAsync(string kind, object payload)
    {
        await _hub.Clients.All.SendAsync("live", new { kind, payload, at = DateTime.Now });
    }
}
