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
            Year = Year, Venue = Venue, Doi = Doi, Url = Url, Abstract = Abstract, Note = Note
        };
    }

    /// <summary>
    /// 参考文献表的格式化与上下文块构造（纯逻辑，可离线测）。
    /// </summary>
    public static class LiteratureFormatter
    {
        /// <summary>
        /// 生成 GB/T 7714 简化版参考文献表（按库里顺序编号 1..n，与正文 [n] 对应）。
        /// </summary>
        public static string FormatReferenceList(IReadOnlyList<LiteratureEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("参考文献");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var seg = new StringBuilder($"[{i + 1}] ");
                seg.Append(string.IsNullOrWhiteSpace(e.Authors) ? "" : e.Authors.Trim() + ". ");
                seg.Append(string.IsNullOrWhiteSpace(e.Title) ? "(无标题)" : e.Title.Trim());
                if (!string.IsNullOrWhiteSpace(e.Venue)) seg.Append(". " + e.Venue.Trim());
                if (!string.IsNullOrWhiteSpace(e.Year)) seg.Append(", " + e.Year.Trim());
                if (!string.IsNullOrWhiteSpace(e.Doi)) seg.Append(". DOI: " + e.Doi.Trim());
                else if (!string.IsNullOrWhiteSpace(e.Url)) seg.Append(". " + e.Url.Trim());
                sb.AppendLine(seg.ToString());
            }
            return sb.ToString().TrimEnd();
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
