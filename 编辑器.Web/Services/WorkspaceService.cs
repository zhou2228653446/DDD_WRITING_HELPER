using System.Text.Json;
using 编辑器;
using 编辑器.Services;
using Microsoft.AspNetCore.SignalR;

namespace 编辑器.Web;

/// <summary>
/// 服务器上的工作区：当前打开的那本书，以及围绕它的全部读写。
///
/// 设计前提（用户明确）：**单人分时**——同一时间只有一边在写。
/// 所以不做多人权限、不做实时合并，只用一把锁保证"人写"和"agent 写"不会同时落盘。
///
/// 数据一律存服务器（默认 %LOCALAPPDATA%\TdxClaw.Web\books），不存在本地/服务器两份，
/// 因此也就没有同步分叉的问题。
/// </summary>
public sealed class WorkspaceService
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly object _gate = new();

    private NovelProject? _project;

    public WorkspaceService(IHubContext<LiveHub> hub)
    {
        _hub = hub;
        BooksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TdxClaw.Web", "books");
        Directory.CreateDirectory(BooksDir);
    }

    public string BooksDir { get; }

    public NovelProject? Current => _project;

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
            _project = NovelProject.Load(path);
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
                _project = NovelProject.Load(path);
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

    public List<object> Snapshots()
    {
        var path = _project?.FilePath;
        if (path == null) return new();

        return new ProjectSnapshotManager(path).LoadIndex()
            .Select(e => new { id = e.Id, text = e.DisplayText })
            .Cast<object>()
            .ToList();
    }

    // ==================================================================
    // 广播：让网页立刻看到改动（人自己改的，或 agent 改的）
    // ==================================================================

    public async Task BroadcastAsync(string kind, object payload)
    {
        await _hub.Clients.All.SendAsync("live", new { kind, payload, at = DateTime.Now });
    }
}
