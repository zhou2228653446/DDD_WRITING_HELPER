using System;
using System.IO;
using System.Linq;
using System.Text;

namespace 编辑器.Services;

/// <summary>
/// 作品（一整本书）的删除。
///
/// ★ 核心原则：**永不硬删**。一本书的全部关联文件整体移入书库旁边的
/// <c>.trash/&lt;UTC 时间戳&gt;__&lt;书名&gt;/</c>，并附一份 <c>_删除信息.txt</c>
/// 记录原路径——误删了照着它原样搬回来即可。
/// 放在书库**外面**（父目录）而不是里面：MCP 的 project_list 会递归扫
/// *.tdxproj，回收站若在书库内，删掉的书还会被列出来，看着像没删成功。
///
/// 一本书的组成部分（都在书库目录下）：
///   &lt;书名&gt;.tdxproj                    书本身（整本书就这一个文件）
///   &lt;书名&gt;.tdxproj.bak                原子写盘留下的备份
///   .chat/&lt;书名&gt;.tdxproj.session.json  万能聊天的对话记忆
///   .snapshots/&lt;书名&gt;.tdxproj/         快照目录
/// 导出文件（&lt;书名&gt;.txt / .docx）是另存的交付物，**不随书删**——
/// 用户可能是为了分享才导出的，悄悄删掉是超出"删书"范围的数据丢失，
/// 只在报告里说明剩了几个，让调用方自己决定。
/// .ai_memory/memory.md 是**全库共用**的一份（不分书），更不能动。
/// </summary>
public static class BookDeleter
{
    /// <summary>删除结果。<see cref="Moved"/> 是相对书库的路径列表（给人看的清单）。</summary>
    public sealed record Result(
        bool Ok,
        string Message,
        string? TrashDir,
        IReadOnlyList<string> Moved,
        long Bytes,
        int LeftoverExports);

    /// <summary>
    /// 把一本书移入回收站。
    /// </summary>
    /// <param name="booksDir">书库目录（如 D:\TdxClaw\self\books）。</param>
    /// <param name="bookFileName">书文件名，必须是纯文件名（如 "守灯人.tdxproj"），不接受路径。</param>
    /// <param name="isCurrentlyOpen">调用方传入"这本书是不是正打开着"；正打开时拒绝删——
    /// 删掉正在编辑的书会让界面/会话抱着一个不存在的文件继续写。</param>
    public static Result Delete(string booksDir, string bookFileName, Func<string, bool>? isCurrentlyOpen = null)
    {
        if (string.IsNullOrWhiteSpace(booksDir))
            return new Result(false, "书库目录为空。", null, Array.Empty<string>(), 0, 0);

        var name = (bookFileName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return new Result(false, "没给书名。", null, Array.Empty<string>(), 0, 0);

        // 路径穿越防护：只接受纯文件名。含分隔符、盘符、".." 一律拒绝——
        // 否则一个 "../../敏感目录/x.tdxproj" 就能把书库外的文件移走。
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0
            || name.Contains("..")
            || Path.IsPathRooted(name)
            || name != Path.GetFileName(name))
            return new Result(false, $"「{name}」不是合法的书文件名（只接受文件名，如 守灯人.tdxproj）。", null, Array.Empty<string>(), 0, 0);

        var bookPath = Path.Combine(booksDir, name);
        if (!File.Exists(bookPath))
            return new Result(false, $"书库里没有「{name}」。", null, Array.Empty<string>(), 0, 0);

        if (isCurrentlyOpen != null && isCurrentlyOpen(bookPath))
            return new Result(false,
                $"「{name}」正打开着，先切换到别的书再删——否则界面会抱着一个已经不存在的文件继续写。",
                null, Array.Empty<string>(), 0, 0);

        // 收集这本书的全部关联文件（相对书库的路径，去重后保持稳定顺序）
        var relatives = new List<string> { name };
        var bak = name + ".bak";
        if (File.Exists(Path.Combine(booksDir, bak))) relatives.Add(bak);
        var chat = Path.Combine(".chat", name + ".session.json");
        if (File.Exists(Path.Combine(booksDir, chat))) relatives.Add(chat);
        var snaps = Path.Combine(".snapshots", name);
        if (Directory.Exists(Path.Combine(booksDir, snaps))) relatives.Add(snaps);

        // 回收站：书库的父目录下（见类注释：放里面会被 project_list 递归扫到）
        var trashRoot = Path.GetFullPath(Path.Combine(booksDir, "..", ".trash"));
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var safeTag = Sanitize(Path.GetFileNameWithoutExtension(name));
        var trashDir = Path.Combine(trashRoot, $"{stamp}__{safeTag}");

        try
        {
            Directory.CreateDirectory(trashDir);
            var moved = new List<string>();
            long bytes = 0;

            foreach (var rel in relatives)
            {
                var src = Path.Combine(booksDir, rel);
                var dst = Path.Combine(trashDir, rel);
                var dstParent = Path.GetDirectoryName(dst)!;
                if (!string.IsNullOrEmpty(dstParent)) Directory.CreateDirectory(dstParent);

                bytes += SizeOf(src);
                if (File.Exists(src))
                    File.Move(src, dst, overwrite: false);
                else if (Directory.Exists(src))
                    Directory.Move(src, dst);
                moved.Add(rel);
            }

            // 还剩几个没动的导出文件（.txt/.docx 同名交付物）
            int exports = Directory.GetFiles(booksDir, Path.GetFileNameWithoutExtension(name) + ".*")
                .Count(f => !f.EndsWith(".tdxproj", StringComparison.OrdinalIgnoreCase)
                         && !f.EndsWith(".tdxproj.bak", StringComparison.OrdinalIgnoreCase));

            WriteManifest(trashDir, name, bookPath, moved, exports);

            var sb = new StringBuilder();
            sb.AppendLine($"已删除《{Path.GetFileNameWithoutExtension(name)}》——整本书移入回收站：");
            sb.AppendLine($"  {trashDir}");
            foreach (var m in moved) sb.AppendLine($"    · {m}");
            sb.AppendLine($"  合计 {bytes / 1024.0:N1} KB。要恢复：按上面的路径把文件搬回书库即可（.snapshots 目录名要保持原样）。");
            if (exports > 0)
                sb.AppendLine($"  注意：书库里还有 {exports} 个同名导出文件（.txt/.docx）未动——那是另存的交付物，需要的话自己删。");

            return new Result(true, sb.ToString(), trashDir, moved, bytes, exports);
        }
        catch (Exception ex)
        {
            // 移动中途失败（比如 .bak 正被占用）：已搬过去的部分留在回收站里，
            // 书的主体可能已经不在书库了——必须说清楚现在是什么状态，别装作没发生。
            return new Result(false,
                $"删除中断：{ex.Message}\n回收站位置：{trashDir}\n" +
                "请去回收站确认已搬过去的部分，并把书的主体搬回书库。",
                trashDir, Array.Empty<string>(), 0, 0);
        }
    }

    /// <summary>回收站清单：原路径 + 时间 + 内容，照着它能手工恢复。</summary>
    private static void WriteManifest(string trashDir, string bookFileName, string originalPath,
                                      List<string> moved, int exports)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"删除时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"原书路径：{originalPath}");
        sb.AppendLine();
        sb.AppendLine("移入本目录的内容（相对书库的路径）：");
        foreach (var m in moved) sb.AppendLine($"  {m}");
        if (exports > 0)
            sb.AppendLine($"\n未随书删除：书库里还有 {exports} 个同名导出文件（.txt/.docx）。");
        sb.AppendLine("\n恢复办法：把上面的文件按原相对路径搬回书库根目录即可。");
        File.WriteAllText(Path.Combine(trashDir, "_删除信息.txt"), sb.ToString(), new UTF8Encoding(true));
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    /// <summary>目录名里不能出现路径分隔符等字符（书名是从文件名的，理论上已干净，防御一下）。</summary>
    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "book" : cleaned;
    }
}
