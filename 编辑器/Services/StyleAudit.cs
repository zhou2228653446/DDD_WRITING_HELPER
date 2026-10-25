using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace 编辑器.Services
{
    /// <summary>一条命中：哪个指标、命中了什么、该怎么处理。</summary>
    public sealed record StyleHit(string Kind, string Excerpt, string Hint);

    /// <summary>
    /// 一次风格审计的结果。分数越高越像人写（100 = 完全不像 AI）。
    /// 分项分是"给人看的解释"，总分是五项之和（0~100）。
    /// </summary>
    public sealed record StyleReport(
        int Score,
        int CharCount,
        int SentenceCount,
        int ParagraphCount,
        double SentenceCv,
        double ParagraphCv,
        double DialogueRatio,
        double ClichePerK,
        double SameHeadRatio,
        double SubSentence,
        double SubParagraph,
        double SubDialogue,
        double SubCliche,
        double SubRepeat,
        IReadOnlyList<StyleHit> Hits,
        IReadOnlyList<KeyValuePair<string, int>> Clichés)
    {
        /// <summary>样本太短时分数不可信（统计量在几十个字上没有意义）。</summary>
        public bool TooShort => CharCount < 200;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【风格审计】AI 味指数 {Score}/100（越高越像人写，" +
                          $"样本 {CharCount} 字 / {SentenceCount} 句 / {ParagraphCount} 段）");

            if (SentenceCount == 0)
            {
                sb.Append("正文为空或没有可分析的句子。");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine($"· 句长节奏 {SubSentence:0.#}/30（变异系数 {SentenceCv:0.00}，人写通常 ≥0.75）");
            sb.AppendLine($"· 段长节奏 {SubParagraph:0.#}/15（变异系数 {ParagraphCv:0.00}，人写通常 ≥0.60）");
            sb.AppendLine($"· 对话占比 {SubDialogue:0.#}/15（{DialogueRatio:P0}，小说常见 15%~45%）");
            sb.AppendLine($"· 套话密度 {SubCliche:0.#}/25（每千字 {ClichePerK:0.0} 次）");
            sb.AppendLine($"· 重复与同构 {SubRepeat:0.#}/15（相邻句同头率 {SameHeadRatio:P0}）");

            if (TooShort)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 样本不足 200 字，以上分数仅供参考（统计量不够稳定）。");
            }

            if (Hits.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("命中（前 10 条）：");
                foreach (var h in Hits.Take(10))
                    sb.AppendLine($"· [{h.Kind}] {h.Excerpt} —— {h.Hint}");
            }

            if (Clichés.Count > 0)
            {
                sb.AppendLine();
                sb.Append("建议加入项目禁用清单：");
                sb.Append(string.Join("、", Clichés.Take(12).Select(c => c.Key)));
            }

            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// 本地风格审计：不调用模型、不花 token，纯统计。
    ///
    /// 为什么要有它：模型写完我们只能说"感觉有点 AI"，既无法比较前后，也无法
    /// 定位到底哪句像 AI。这层的输出同时给三处用：
    ///   1) 作者/agent 自查（MCP 工具 style_check）；
    ///   2) 挑选"最像人写"的段落做 few-shot 样本；
    ///   3) 生成命中套话 → 项目禁用清单。
    ///
    /// 指标选择的原则：**只收能在中文小说里稳定区分人写 / 模型写的量**。
    /// 词表类指标一律按密度而不是"出现即扣分"——人类也用"仿佛"，只是不会每段都用。
    /// </summary>
    public static class StyleAudit
    {
        /// <summary>套话表。权重 2 = 一眼 AI；1 = 滥用才算。</summary>
        private static readonly (string Phrase, int Weight)[] Phrases =
        {
            ("空气中弥漫着", 2), ("眼中闪过一丝", 2), ("嘴角勾起", 2), ("嘴角扬起", 1),
            ("不容置疑", 1), ("意味深长", 1), ("心中一凛", 1), ("心中一沉", 1),
            ("心头一紧", 1), ("时间仿佛静止", 2), ("空气凝固", 2), ("深吸一口气", 1),
            ("缓缓开口", 1), ("目光深邃", 1), ("神色复杂", 1), ("露出一丝苦笑", 2),
            ("心中暗道", 2), ("涌上心头", 1), ("无法言喻", 2), ("五味杂陈", 2),
            ("如释重负", 1), ("若有所思", 1), ("不动声色", 1), ("波澜不惊", 1),
            ("若有若无", 1), ("不置可否", 1), ("话音落下", 2), ("沉默了片刻", 1),
            ("与此同时", 1), ("在这一刻", 1), ("命运的齿轮", 2), ("宿命", 1),
            ("总而言之", 2), ("值得一提", 2), ("他知道，", 2), ("她不知道的是", 2),
            ("仿佛过了一个世纪", 2), ("深邃的眼眸", 2), ("像一潭死水", 1),
        };

        /// <summary>填充词：不进总分，只报密度（人写也会用，但密度差很多）。</summary>
        private static readonly string[] Fillers =
        {
            "仿佛", "似乎", "宛如", "不禁", "微微", "缓缓", "轻轻", "默默", "悄然",
        };

        /// <summary>排比 / 对仗的连接结构：模型特别爱用。</summary>
        private static readonly string[] Parallels =
        {
            "不仅", "而且", "既", "又", "一边", "不是", "而是", "与其", "不如",
        };

        private static readonly char[] SentenceEnds = { '。', '！', '？', '…', '；', '!', '?', ';' };

        public static StyleReport Analyze(string? text)
        {
            var raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var visible = CountVisible(raw);

            var paragraphs = raw.Split('\n')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            var sentences = new List<string>();
            foreach (var p in paragraphs)
            {
                var start = 0;
                for (var i = 0; i < p.Length; i++)
                {
                    if (Array.IndexOf(SentenceEnds, p[i]) < 0) continue;
                    var s = p.Substring(start, i - start + 1).Trim();
                    if (CountVisible(s) >= 4) sentences.Add(s);
                    start = i + 1;
                }
                var tail = p.Substring(Math.Min(start, p.Length)).Trim();
                if (CountVisible(tail) >= 4) sentences.Add(tail);
            }

            if (sentences.Count == 0 || visible == 0)
            {
                return new StyleReport(0, visible, 0, paragraphs.Count, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, Array.Empty<StyleHit>(),
                    Array.Empty<KeyValuePair<string, int>>());
            }

            var sentenceLens = sentences.Select(CountVisible).Where(n => n > 0).ToList();
            var paragraphLens = paragraphs.Select(CountVisible).Where(n => n > 0).ToList();

            var sentenceCv = Cv(sentenceLens);
            var paragraphCv = Cv(paragraphLens);
            var dialogueRatio = DialogueRatio(raw, visible);

            // ── 套话 ──
            var hits = new List<StyleHit>();
            var found = new List<KeyValuePair<string, int>>();
            var weightSum = 0;
            foreach (var (phrase, weight) in Phrases)
            {
                var n = CountOf(raw, phrase);
                if (n == 0) continue;
                weightSum += n * weight;
                found.Add(new KeyValuePair<string, int>(phrase, n));
                hits.Add(new StyleHit("套话", Excerpt(raw, phrase), $"出现 {n} 次，模型腔明显"));
            }
            found.Sort((a, b) => b.Value.CompareTo(a.Value));

            var clichePerK = visible == 0 ? 0 : weightSum * 1000.0 / visible;

            // ── 填充词密度（不进总分，只提示）──
            var fillerCount = Fillers.Sum(f => CountOf(raw, f));
            var fillerPerK = visible == 0 ? 0 : fillerCount * 1000.0 / visible;
            if (fillerPerK >= 25)
                hits.Add(new StyleHit("填充词", "仿佛 / 似乎 / 微微 / 缓缓…",
                    $"每千字 {fillerPerK:0.0} 次，密度偏高"));

            // ── 相邻句同头 ──
            var sameHead = 0;
            var pairs = 0;
            for (var i = 1; i < sentences.Count; i++)
            {
                var a = Head(sentences[i - 1]);
                var b = Head(sentences[i]);
                if (a.Length == 0 || b.Length == 0) continue;
                pairs++;
                if (a == b) sameHead++;
            }
            var sameHeadRatio = pairs == 0 ? 0 : (double)sameHead / pairs;

            // 只报"连续"同头。按全书词频统计会把"他"这种正常主语算成 30 句，
            // 那是误导——人不这么读，模型腔的典型症状是**连着几句**一个起手式。
            var runs = new Dictionary<string, int>();
            string prevHead = "";
            var run = 0;
            foreach (var sentence in sentences)
            {
                var h = Head(sentence);
                if (h.Length == 0) { prevHead = ""; run = 0; continue; }
                run = h == prevHead ? run + 1 : 1;
                prevHead = h;
                if (run >= 3) runs[h] = Math.Max(runs.TryGetValue(h, out var old2) ? old2 : 0, run);
            }
            foreach (var kv in runs.OrderByDescending(x => x.Value).Take(3))
                hits.Add(new StyleHit("同头", $"{kv.Key}……", $"有连续 {kv.Value} 句用同一个字起头"));

            // 排比连接词：同一段里出现 3 个以上不同连接词就提示
            var parallels = Parallels.Count(w => CountOf(raw, w) >= 2);
            if (parallels >= 3)
                hits.Add(new StyleHit("排比", "不仅…而且 / 既…又 / 一边…一边", "对仗句式密度高"));

            // ── 打分 ──
            var subSentence = Linear(sentenceCv, 0.35, 0.75) * 30;
            var subParagraph = Linear(paragraphCv, 0.20, 0.60) * 15;
            var subDialogue = DialogueScore(dialogueRatio) * 15;
            var subCliche = (1 - Linear(clichePerK, 0, 8)) * 25;
            var subRepeat = Math.Max(0, 1 - sameHeadRatio / 0.35) * 15;
            if (parallels >= 3) subRepeat = Math.Max(0, subRepeat - 3);

            var score = (int)Math.Round(subSentence + subParagraph + subDialogue + subCliche + subRepeat);
            score = Math.Clamp(score, 0, 100);

            return new StyleReport(
                score, visible, sentences.Count, paragraphs.Count,
                sentenceCv, paragraphCv, dialogueRatio, clichePerK, sameHeadRatio,
                subSentence, subParagraph, subDialogue, subCliche, subRepeat,
                hits.Take(20).ToList(), found.Take(12).ToList());
        }

        /// <summary>可见字数：中英文数字，不含标点与空白。密度类指标都按它算。</summary>
        private static int CountVisible(string s)
        {
            var n = 0;
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) n++;
            }
            return n;
        }

        /// <summary>变异系数；样本不足两个时返回 0。</summary>
        private static double Cv(List<int> values)
        {
            if (values.Count < 2) return 0;
            var mean = values.Average();
            if (mean <= 0) return 0;
            var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            return Math.Sqrt(variance) / mean;
        }

        /// <summary>把 x 从 [lo,hi] 线性映射到 [0,1]，两端夹住。</summary>
        private static double Linear(double x, double lo, double hi)
        {
            if (hi <= lo) return x >= hi ? 1 : 0;
            return Math.Clamp((x - lo) / (hi - lo), 0, 1);
        }

        /// <summary>对话占比用梯形给分：太少（纯叙述）和太多（纯对话）都扣。</summary>
        private static double DialogueScore(double ratio)
        {
            if (ratio < 0.05) return 0;
            if (ratio < 0.15) return (ratio - 0.05) / 0.10;
            if (ratio <= 0.45) return 1;
            if (ratio < 0.70) return (0.70 - ratio) / 0.25;
            return 0;
        }

        /// <summary>引号内的字数 / 总字数。支持中日引号与直角引号。</summary>
        private static double DialogueRatio(string text, int visible)
        {
            if (visible == 0) return 0;
            var pairs = new (char Open, char Close)[] { ('「', '」'), ('『', '』'), ('“', '”'), ('‘', '’') };
            var inside = 0;
            foreach (var (open, close) in pairs)
            {
                var i = 0;
                while (i < text.Length)
                {
                    var a = text.IndexOf(open, i);
                    if (a < 0) break;
                    var b = text.IndexOf(close, a + 1);
                    if (b < 0) break;
                    inside += CountVisible(text.Substring(a + 1, b - a - 1));
                    i = b + 1;
                }
            }
            return Math.Clamp((double)inside / visible, 0, 1);
        }

        private static int CountOf(string haystack, string needle)
        {
            if (needle.Length == 0) return 0;
            var n = 0;
            var i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }

        /// <summary>
        /// 句首特征：取第一个可见字。
        /// ★ 不用前两个字：中文里"他这样…他那样…"是两个不同的二字头，但语义上
        ///   一模一样——模型最典型的腔调就是每句都用同一个主语起头。取一个字才抓得到。
        /// </summary>
        private static string Head(string sentence)
        {
            foreach (var c in sentence)
                if (char.IsLetterOrDigit(c)) return c.ToString();
            return "";
        }

        private static string Excerpt(string text, string phrase)
        {
            var i = text.IndexOf(phrase, StringComparison.Ordinal);
            if (i < 0) return phrase;
            var start = Math.Max(0, i - 8);
            var end = Math.Min(text.Length, i + phrase.Length + 8);
            var s = text.Substring(start, end - start).Replace('\n', ' ');
            return $"…{s}…";
        }
    }
}
