using System;
using System.Collections.Generic;
using System.Text;

namespace 编辑器.Services
{
    /// <summary>
    /// 查找替换的核心字符串操作。
    ///
    /// 为什么从 FindReplaceWindow 里抽出来：那段代码和 WPF 绑死，没法测；
    /// 而最容易出错的恰恰是纯字符串的部分——命中边界、替换后索引失效、
    /// 上下文截取越界。这些都是"看起来对、跑起来在特定输入下崩"的类型，
    /// 光靠肉眼点几下测不出来。
    /// </summary>
    public static class TextSearch
    {
        /// <summary>
        /// 找出全部命中位置。
        /// 注意命中不重叠：匹配一次后索引直接跳过整个 pattern，
        /// 否则在 "aaa" 里找 "aa" 会报两处（0 和 1），而用户看到的明明只有一处。
        /// </summary>
        public static List<int> FindAll(string text, string pattern, StringComparison cmp, int max = int.MaxValue)
        {
            var hits = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return hits;

            int idx = 0;
            while ((idx = text.IndexOf(pattern, idx, cmp)) >= 0)
            {
                hits.Add(idx);
                idx += pattern.Length;
                if (hits.Count >= max) break;
            }
            return hits;
        }

        public static int Count(string text, string pattern, StringComparison cmp) =>
            FindAll(text, pattern, cmp).Count;

        /// <summary>
        /// 判断指定位置是否仍然是这个 pattern。
        /// 替换前必须用它复核一次：从列表里拿到索引到真正替换之间，
        /// 正文可能已经被别处改过，索引早就失效了——盲替换会改错地方。
        /// </summary>
        public static bool MatchesAt(string text, int index, string pattern, StringComparison cmp)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (index < 0 || index + pattern.Length > text.Length) return false;
            return string.Compare(text, index, pattern, 0, pattern.Length, cmp) == 0;
        }

        /// <summary>替换指定位置的一处。越界时安全退化（返回原文），不抛异常。</summary>
        public static string ReplaceAt(string text, int index, int length, string replacement)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (index < 0 || index > text.Length) return text;

            int realLength = Math.Min(length, text.Length - index);
            return text.Remove(index, realLength).Insert(index, replacement);
        }

        /// <summary>
        /// 截取命中处前后各 <paramref name="pad"/> 字，命中词本身用【】括出来。
        /// 长片段里一眼能看到匹配在哪——只给行号的话用户还得自己找。
        /// </summary>
        public static string Snippet(string text, int index, int length, int pad = 45)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (index < 0) index = 0;
            if (index + length > text.Length) length = Math.Max(0, text.Length - index);

            int from = Math.Max(0, index - pad);
            int to = Math.Min(text.Length, index + length + pad);

            var sb = new StringBuilder();
            if (from > 0) sb.Append('…');
            sb.Append(Flatten(text[from..index]));
            sb.Append('【').Append(Flatten(text.Substring(index, length))).Append('】');
            sb.Append(Flatten(text[(index + length)..to]));
            if (to < text.Length) sb.Append('…');
            return sb.ToString();

            static string Flatten(string s) => s.Replace("\r", " ").Replace("\n", " ");
        }
    }
}
