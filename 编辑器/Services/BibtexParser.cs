using System;
using System.Collections.Generic;
using System.Text;

namespace 编辑器
{
    /// <summary>
    /// 最小可用的 BibTeX 解析器：够把常见 .bib 文件的条目抽出来入库，
    /// 不追求覆盖全部 BibTeX 方言。花括号嵌套（{...{...}...}）按深度配对；
    /// @comment / @preamble / @string 跳过；解析失败的条目跳过而不是让整个文件失败。
    /// </summary>
    public static class BibtexParser
    {
        /// <summary>从 .bib 文本解析出文献条目（保序）。解析不出任何条目返回空列表。</summary>
        public static List<LiteratureEntry> Parse(string bibText)
        {
            var result = new List<LiteratureEntry>();
            if (string.IsNullOrWhiteSpace(bibText)) return result;

            int i = 0;
            while ((i = bibText.IndexOf('@', i)) >= 0)
            {
                int typeEnd = i + 1;
                while (typeEnd < bibText.Length && char.IsLetter(bibText[typeEnd])) typeEnd++;
                var type = bibText.Substring(i + 1, typeEnd - i - 1).ToLowerInvariant();

                // 跳过非条目指令
                if (type == "comment" || type == "preamble" || type == "string")
                {
                    i = typeEnd;
                    continue;
                }

                // 找到条目体的起始 '{'
                int open = bibText.IndexOf('{', typeEnd);
                if (open < 0) break;
                int close = MatchBrace(bibText, open);
                if (close < 0) close = bibText.Length;   // 容错：未闭合就取到结尾

                var body = bibText.Substring(open + 1, close - open - 1);
                var entry = ParseBody(type, body);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Title))
                    result.Add(entry);

                i = close > open ? close : typeEnd;
            }
            return result;
        }

        /// <summary>返回与 open 位置 '{' 配对的 '}' 下标；找不到返回 -1。</summary>
        private static int MatchBrace(string text, int open)
        {
            int depth = 0;
            for (int k = open; k < text.Length; k++)
            {
                if (text[k] == '{') depth++;
                else if (text[k] == '}')
                {
                    depth--;
                    if (depth == 0) return k;
                }
            }
            return -1;
        }

        private static LiteratureEntry? ParseBody(string type, string body)
        {
            // 第一段到第一个逗号是 citation key
            int comma = body.IndexOf(',');
            var key = (comma >= 0 ? body.Substring(0, comma) : body).Trim();
            var fields = comma >= 0 ? SplitFields(body.Substring(comma + 1)) : new List<(string, string)>();

            var entry = new LiteratureEntry { CitationKey = key };

            foreach (var (name, rawValue) in fields)
            {
                // 清掉值两边的 { } 或 " 以及内部残留的连字括号 {\"o} 之类不处理，保原样
                var value = CleanValue(rawValue);

                switch (name.ToLowerInvariant())
                {
                    case "title": entry.Title = value; break;
                    case "author" or "authors" or "editor":
                        // BibTeX 用 " and " 分隔作者 → 统一成 ", "（GB/T 7714 风格）
                        entry.Authors = value.Replace(" and ", ", ").Replace(" AND ", ", ");
                        break;
                    case "year" or "date": entry.Year = value; break;
                    case "journal" or "booktitle" or "publisher" or "howpublished" or "school":
                        entry.Venue = string.IsNullOrEmpty(entry.Venue) ? value : entry.Venue; break;
                    case "doi": entry.Doi = value; break;
                    case "url": entry.Url = value; break;
                    case "abstract" or "annote": entry.Abstract = value; break;
                    case "note": entry.Note = value; break;
                }
            }

            // 标题兜底：有的条目没 title 但有 note，不收（避免入库一堆空壳）
            if (string.IsNullOrWhiteSpace(entry.Title)) return null;

            // venue 兜底：article 用 journal，inproceedings 用 booktitle，上面已合并；
            // 类型作为补充信息追加到备注
            if (type != "misc" && !string.IsNullOrEmpty(type))
                entry.Note = string.IsNullOrEmpty(entry.Note) ? $"@{type}" : $"@{type} | " + entry.Note;
            return entry;
        }

        /// <summary>按顶层逗号切字段（花括号/引号内的逗号不算）。</summary>
        private static List<(string Name, string Value)> SplitFields(string body)
        {
            var fields = new List<(string, string)>();
            int depth = 0; bool inQuote = false;
            int segStart = 0;
            for (int k = 0; k <= body.Length; k++)
            {
                char c = k < body.Length ? body[k] : ',';
                if (c == '{') depth++;
                else if (c == '}') depth = Math.Max(0, depth - 1);
                else if (c == '"' && depth == 0) inQuote = !inQuote;

                if (c == ',' && depth == 0 && !inQuote)
                {
                    var seg = body.Substring(segStart, k - segStart);
                    var eq = seg.IndexOf('=');
                    if (eq > 0)
                    {
                        var name = seg.Substring(0, eq).Trim();
                        var value = seg.Substring(eq + 1).Trim();
                        fields.Add((name, value));
                    }
                    segStart = k + 1;
                }
            }

            // 收尾段：正常输入靠末尾的人为 ',' 触发；未闭合花括号会把深度卡在 >0，
            // 这里兜底把剩余内容也切出来（宁可多解析一条，也不让整条目丢失）
            if (segStart < body.Length)
            {
                var seg = body.Substring(segStart);
                var eq = seg.IndexOf('=');
                if (eq > 0)
                    fields.Add((seg.Substring(0, eq).Trim(), seg.Substring(eq + 1).Trim()));
            }
            return fields;
        }

        /// <summary>去掉值两侧的 { } 包裹与引号，压掉多余空白。</summary>
        private static string CleanValue(string raw)
        {
            var v = raw.Trim();
            // 可能有 {title = {xxx}} 多层包裹，逐层剥
            while (v.Length >= 2 && v[0] == '{' && v[^1] == '}') v = v[1..^1].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();
            // BibTeX 连字符大写 {D}eep Learning 之类：去掉值内的成对花括号
            var sb = new StringBuilder(v.Length);
            for (int k = 0; k < v.Length; k++)
            {
                if (v[k] == '{' || v[k] == '}') continue;
                sb.Append(v[k]);
            }
            return sb.ToString().Trim();
        }
    }
}
