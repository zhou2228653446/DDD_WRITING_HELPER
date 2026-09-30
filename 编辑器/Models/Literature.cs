using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace 编辑器
{
    /// <summary>
    /// 一条参考文献。用户手动维护（或从 BibTeX 导入），
    /// AI 生成正文时把整个文献库当上下文，引用处标 [n]；AI 不负责决定引用什么。
    /// </summary>
    public class LiteratureEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string CitationKey { get; set; } = "";     // BibTeX key（导入时保留，便于追溯）
        public string Title { get; set; } = "";
        public string Authors { get; set; } = "";          // 原样字符串，用户可写 "Zhang S, Li M" 或中文
        public string Year { get; set; } = "";
        public string Venue { get; set; } = "";            // 期刊 / 会议 / 出版社
        public string Doi { get; set; } = "";
        public string Url { get; set; } = "";
        public string Abstract { get; set; } = "";
        public string Note { get; set; } = "";
        public string Pages { get; set; } = "";            // 页码范围，如 "268-277"（GB/T 著录用）
        /// <summary>
        /// 文献类型标识（GB/T 7714-2015）：[J] 期刊 / [C] 会议论文集 / [M] 专著 /
        /// [D] 学位论文 / [R] 报告 / [S] 标准 / [EB/OL] 电子资源。
        /// 空串时由 Venue/Note 自动推断。
        /// </summary>
        public string EntryType { get; set; } = "";

        /// <summary>「作者, 年份」短标（列表展示用）。</summary>
        public string ShortLabel
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Authors)) parts.Add(Authors.Split(new[] { ',', '，', ';' }, 2)[0].Trim());
                if (!string.IsNullOrWhiteSpace(Year)) parts.Add(Year);
                return parts.Count > 0 ? string.Join(", ", parts) : "(未署名)";
            }
        }

        public LiteratureEntry Clone() => new()
        {
            Id = Id, CitationKey = CitationKey, Title = Title, Authors = Authors,
            Year = Year, Venue = Venue, Doi = Doi, Url = Url, Abstract = Abstract, Note = Note,
            Pages = Pages, EntryType = EntryType
        };
    }

    /// <summary>
    /// 参考文献表的格式化与上下文块构造（纯逻辑，可离线测）。
    /// 文献表格式遵循 GB/T 7714-2015（顺序编码制），规则参考
    /// github.com/Coekjan/gb7714-bilingual 与 RainbowC0/gbt7714-2015-wordbibstyle。
    /// </summary>
    public static class LiteratureFormatter
    {
        /// <summary>
        /// 生成 GB/T 7714-2015 顺序编码制参考文献表（编号 1..n，与正文 [n] 对应）。
        /// 著录格式：作者. 题名[类型标识]. 来源, 年, 卷(期): 页码.（DOI/URL 可选）
        /// 作者 &gt;3 人截断加「等」（中文）或「et al.」（英文，按首作者字符判定）。
        /// </summary>
        public static string FormatReferenceList(IReadOnlyList<LiteratureEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("参考文献");
            for (int i = 0; i < entries.Count; i++)
                sb.AppendLine(FormatEntry(entries[i], i + 1));
            return sb.ToString().TrimEnd();
        }

        /// <summary>按 GB/T 7714-2015 格式化单条文献。</summary>
        public static string FormatEntry(LiteratureEntry e, int number)
        {
            var seg = new StringBuilder($"[{number}] ");
            var isCjk = IsCjkTitle(e);

            // 作者：>3 人截断 + 等 / et al.（GB/T 7714 规则：3 人以内全列，超过列前 3 个）
            var authors = FormatAuthors(e.Authors, isCjk);
            if (authors.Length > 0)
            {
                seg.Append(authors);
                // 「et al.」自带句点，别再加一个变成双句点
                if (!authors.EndsWith(".")) seg.Append('.');
                seg.Append(' ');
            }

            // 题名 + 类型标识
            seg.Append(string.IsNullOrWhiteSpace(e.Title) ? "(无标题)" : e.Title.Trim());
            seg.Append('[').Append(ResolveEntryType(e)).Append(']');

            // 来源 + 年 + 卷(期): 页码 —— 有来源才是完整著录；没有就尽量给 DOI/URL
            var venue = e.Venue.Trim();
            var year = e.Year.Trim();
            if (venue.Length > 0)
            {
                seg.Append(". ").Append(venue);
                if (year.Length > 0) seg.Append(", ").Append(year);
                if (!string.IsNullOrWhiteSpace(e.Pages)) seg.Append(": ").Append(e.Pages.Trim());
                seg.Append('.');
            }
            else if (year.Length > 0)
            {
                seg.Append(", ").Append(year).Append('.');
            }

            if (!string.IsNullOrWhiteSpace(e.Doi)) seg.Append(" DOI: ").Append(e.Doi.Trim()).Append('.');
            else if (!string.IsNullOrWhiteSpace(e.Url)) seg.Append(' ').Append(e.Url.Trim()).Append('.');
            return seg.ToString();
        }

        /// <summary>作者串格式化：>3 人截断加「等」/「et al.」。</summary>
        public static string FormatAuthors(string authors, bool isCjk)
        {
            var a = (authors ?? "").Trim();
            if (a.Length == 0) return "";
            var names = a.Split(new[] { ",", "，", ";", "；", " and ", " AND " },
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.Length <= 3) return string.Join(", ", names);
            return string.Join(", ", names.Take(3)) + (isCjk ? ", 等" : ", et al.");
        }

        /// <summary>按标题字符判断中英文（决定「等」还是「et al.」）。</summary>
        public static bool IsCjkTitle(LiteratureEntry e)
        {
            var sample = (e.Title ?? "") + (e.Authors ?? "");
            foreach (var ch in sample)
                if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
            return false;
        }

        /// <summary>
        /// 解析文献类型标识：优先用户指定的 EntryType；
        /// 否则从 Note（BibTeX 导入时写的 @article 等）与 Venue 推断。
        /// </summary>
        public static string ResolveEntryType(LiteratureEntry e)
        {
            var t = (e.EntryType ?? "").Trim();
            if (t.Length > 0) return t;

            var note = (e.Note ?? "").ToLowerInvariant();
            if (note.Contains("@article")) return "J";
            if (note.Contains("@inproceedings") || note.Contains("@conference")) return "C";
            if (note.Contains("@phdthesis")) return "D";
            if (note.Contains("@mastersthesis")) return "D";
            if (note.Contains("@book")) return "M";
            if (note.Contains("@techreport")) return "R";
            if (note.Contains("@misc") || note.Contains("@online") || note.Contains("@electronic")) return "EB/OL";

            // 无 BibTeX 线索：有 DOI/URL 且无来源 → 电子资源；否则当期刊
            if (e.Venue.Trim().Length == 0 && (!string.IsNullOrWhiteSpace(e.Url) || !string.IsNullOrWhiteSpace(e.Doi)))
                return "EB/OL";
            return "J";
        }

        /// <summary>
        /// 把文献库导出为 BibTeX 文本（可被 Zotero / JabRef 直接导入）。
        /// 类型由 ResolveEntryType 反推；引用键缺省用「首作者姓 + 年份」生成并去重。
        /// </summary>
        public static string ExportBibtex(IReadOnlyList<LiteratureEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("% 由 DDD_WRITING_HELPER 参考文献库导出");
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                var type = ResolveEntryType(e) switch
                {
                    "J" => "article",
                    "C" => "inproceedings",
                    "D" => "phdthesis",
                    "M" => "book",
                    "R" => "techreport",
                    _ => "misc"
                };

                var key = MakeCitationKey(e, usedKeys);
                sb.AppendLine($"@{type}{{{key},");
                if (!string.IsNullOrWhiteSpace(e.Authors)) sb.AppendLine($"  author = {{{e.Authors.Trim().Replace(", ", " and ")}}},");
                sb.AppendLine($"  title = {{{e.Title.Trim()}}},");
                if (!string.IsNullOrWhiteSpace(e.Venue))
                    sb.AppendLine(type == "article"
                        ? $"  journal = {{{e.Venue.Trim()}}},"
                        : $"  booktitle = {{{e.Venue.Trim()}}},");
                if (!string.IsNullOrWhiteSpace(e.Year)) sb.AppendLine($"  year = {{{e.Year.Trim()}}},");
                if (!string.IsNullOrWhiteSpace(e.Pages)) sb.AppendLine($"  pages = {{{e.Pages.Trim()}}},");
                if (!string.IsNullOrWhiteSpace(e.Doi)) sb.AppendLine($"  doi = {{{e.Doi.Trim()}}},");
                if (!string.IsNullOrWhiteSpace(e.Url)) sb.AppendLine($"  url = {{{e.Url.Trim()}}},");
                if (!string.IsNullOrWhiteSpace(e.Abstract)) sb.AppendLine($"  abstract = {{{e.Abstract.Trim()}}},");
                sb.AppendLine("}");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        /// <summary>生成引用键：首作者首词 + 年份；冲突时追加字母。</summary>
        private static string MakeCitationKey(LiteratureEntry e, HashSet<string> used)
        {
            var baseKey = (e.CitationKey ?? "").Trim();
            if (baseKey.Length == 0)
            {
                var firstAuthor = e.Authors.Split(new[] { ',', '，', ';', '；' },
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var authorWord = new string(firstAuthor.TakeWhile(c => c != ' ' && char.IsLetter(c)).ToArray());
                if (authorWord.Length == 0) authorWord = "ref";
                baseKey = authorWord + (string.IsNullOrWhiteSpace(e.Year) ? "" : e.Year.Trim());
            }
            // 键里只留字母数字下划线连字符（BibTeX 键不能有逗号/空格/花括号）
            baseKey = new string(baseKey.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            if (baseKey.Length == 0) baseKey = "ref";

            var key = baseKey; int n = 2;
            while (!used.Add(key))
                key = baseKey + n++;
            return key;
        }

        /// <summary>
        /// 拼进系统提示词的「参考文献块」。规则写得死：
        /// 只许引用列表内的文献；列表没有的继续用【需引文献】占位，禁止编造。
        /// </summary>
        public static string BuildContextBlock(IReadOnlyList<LiteratureEntry>? entries)
        {
            if (entries == null || entries.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("## 作者的参考文献库（引用必须出自这里）");
            sb.AppendLine("正文需要引用时用 [编号] 标注（如 [1]、[2]），编号对应下面这份列表：");
            sb.AppendLine();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var line = new StringBuilder($"[{i + 1}] ");
                line.Append(string.IsNullOrWhiteSpace(e.Authors) ? "" : e.Authors.Trim() + " ");
                line.Append("(" + (string.IsNullOrWhiteSpace(e.Year) ? "年份不详" : e.Year.Trim()) + ") ");
                line.Append(string.IsNullOrWhiteSpace(e.Title) ? "(无标题)" : e.Title.Trim());
                if (!string.IsNullOrWhiteSpace(e.Venue)) line.Append(". " + e.Venue.Trim());
                sb.AppendLine(line.ToString());
                if (!string.IsNullOrWhiteSpace(e.Abstract))
                {
                    // 摘要给模型当内容依据，但截断防止一条摘要吃掉半个上下文
                    var abs = e.Abstract.Trim();
                    if (abs.Length > 500) abs = abs.Substring(0, 500) + "…";
                    sb.AppendLine("    摘要：" + abs);
                }
            }
            sb.AppendLine();
            sb.AppendLine("引用规则：");
            sb.AppendLine("- 只能引用上面列表里的文献，编号必须与列表一致；不得虚构列表外的任何文献。");
            sb.AppendLine("- 列表里没有合适文献时，照旧写占位标记【需引文献】，由作者自己补。");
            sb.AppendLine("- 不得编造 DOI、页码、卷期号等列表里没有给出的信息。");
            return sb.ToString().TrimEnd();
        }
    }
}
