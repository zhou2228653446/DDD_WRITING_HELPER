using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace 编辑器.Services
{
    /// <summary>一条命中：哪个指标、命中了什么、该怎么处理。</summary>
    public sealed record StyleHit(string Kind, string Excerpt, string Hint);

    /// <summary>
    /// 全书基线。**段落节奏与对话占比必须相对它来判**——
    /// 用固定黄金区间去判所有章节，等于假设所有章节都是"匀速推进的普通剧情章"：
    /// 孤身潜行章（对话 &lt;5%）会被无端扣分，朝堂辩论章（对话 70%）也会被无端扣分。
    /// 同一本书里所有章节本来就有各自的体裁，用它自己的常态当尺子才说得通。
    /// </summary>
    public sealed record StyleBaseline
    {
        public double MedianSentenceLen { get; init; }
        public double MedianParagraphLen { get; init; }
        public double DialogueRatio { get; init; }
        public int SentenceCount { get; init; }
        public int ParagraphCount { get; init; }

        /// <summary>各章对话占比的最小/最大值。落在区间里 = 这本书的正常体裁，不扣分。</summary>
        public double DialogueMin { get; init; }
        public double DialogueMax { get; init; }

        /// <summary>各章中位段长的最小/最大值（移动端短段排版的书，这里本来就是短的）。</summary>
        public double ParagraphMin { get; init; }
        public double ParagraphMax { get; init; }

        public int ChapterCount { get; init; }

        public static StyleBaseline From(string text) => FromChapters(new[] { text });

        /// <summary>
        /// 按章建立基线。★ 用"各章的取值范围"而不是"全书平均"：
        /// 一本书里既有潜行章又有辩论章是正常的，只要落在它自己的范围内就不该扣分。
        /// </summary>
        public static StyleBaseline FromChapters(IEnumerable<string> chapters)
        {
            var list = chapters.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            var seg = StyleAudit.Split(string.Join("\n", list));

            var dialogueRatios = new List<double>();
            var paraMedians = new List<double>();
            foreach (var c in list)
            {
                var s = StyleAudit.Split(c);
                if (s.VisibleChars == 0) continue;
                dialogueRatios.Add(s.DialogueChars / (double)s.VisibleChars);
                paraMedians.Add(Median(s.ParagraphLengths));
            }

            return new StyleBaseline
            {
                MedianSentenceLen = Median(seg.SentenceLengths),
                MedianParagraphLen = Median(seg.ParagraphLengths),
                DialogueRatio = seg.VisibleChars == 0
                    ? 0
                    : seg.DialogueChars / (double)seg.VisibleChars,
                SentenceCount = seg.SentenceLengths.Count,
                ParagraphCount = seg.ParagraphLengths.Count,
                DialogueMin = dialogueRatios.Count > 0 ? dialogueRatios.Min() : 0,
                DialogueMax = dialogueRatios.Count > 0 ? dialogueRatios.Max() : 0,
                ParagraphMin = paraMedians.Count > 0 ? paraMedians.Min() : 0,
                ParagraphMax = paraMedians.Count > 0 ? paraMedians.Max() : 0,
                ChapterCount = list.Count,
            };
        }

        internal static double Median(List<int> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }

    /// <summary>节奏类指标。</summary>
    public sealed record RhythmMetrics(
        double MeanSentenceLen,
        double SentenceCv,
        double Rmssd,           // 相邻句长差的均方根（未归一）
        double NormRmssd,        // RMSSD / 平均句长
        double FlipRate,         // 长短方向交替率：0=从不交替，1=句句交替
        double MeanParagraphLen,
        double ParagraphCv,
        double FlowRatio,        // 流水句占比（一句里逗号一逗到底）
        int FlowSentences);

    /// <summary>模型腔类指标。</summary>
    public sealed record FlavorMetrics(
        double WordClichePerK,
        int WordClicheHits,
        int PatternHits,
        double PatternPerK,
        double SimilePerK,
        double ConnectivePerK,
        double DegreeAdverbPerK,
        int ElevationHits,
        int FillerHits,
        double FillerPerK,
        int LeadInHits);

    /// <summary>一次风格审计的结果。分数越高越像人写（100 = 完全不像 AI）。</summary>
    public sealed record StyleReport(
        int Score,
        int CharCount,
        int SentenceCount,
        int ParagraphCount,
        RhythmMetrics Rhythm,
        FlavorMetrics Flavor,
        double DialogueRatio,
        double DialogueDelta,      // 与全书基线的差（相对模式才有意义）
        double ParagraphLenDelta,   // 与全书基线中位段长的比
        double SubRhythm,
        double SubParagraph,
        double SubDialogue,
        double SubFlavor,
        double SubRepeat,
        double SubFlow,
        bool Relative,
        IReadOnlyList<StyleHit> Hits,
        IReadOnlyList<KeyValuePair<string, int>> Clichés)
    {
        /// <summary>样本太短时分数不可信（统计量在几十个字上没有意义）。</summary>
        public bool TooShort => CharCount < 200;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【风格审计】AI 味指数 {Score}/100（越高越像人写，" +
                          $"样本 {CharCount} 字 / {SentenceCount} 句 / {ParagraphCount} 段" +
                          (Relative ? "，已对全书基线" : "") + "）");

            if (SentenceCount == 0)
            {
                sb.Append("正文为空或没有可分析的句子。");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine($"· 句长节奏 {SubRhythm:0.#}/25（RMSSD {Rhythm.NormRmssd:0.00}、交替率 {Rhythm.FlipRate:P0}、CV {Rhythm.SentenceCv:0.00}）");
            sb.AppendLine($"· 段落节奏 {SubParagraph:0.#}/10（段长 CV {Rhythm.ParagraphCv:0.00}、中位段长 {Rhythm.MeanParagraphLen:0} 字" +
                          (Relative ? $"、相对全书 {ParagraphLenDelta:P0}" : "") + "）");
            sb.AppendLine($"· 对话占比 {SubDialogue:0.#}/10（{DialogueRatio:P0}" +
                          (Relative ? $"，与全书差 {DialogueDelta:P0}" : "") + "）");
            sb.AppendLine($"· 模型腔 {SubFlavor:0.#}/35（词表 {Flavor.WordClichePerK:0.0}/千字、句式 {Flavor.PatternPerK:0.0}/千字、" +
                          $"比喻 {Flavor.SimilePerK:0.0}/千字、连接词 {Flavor.ConnectivePerK:0.0}/千字、程度副词 {Flavor.DegreeAdverbPerK:0.0}/千字、" +
                          $"章末升华 {Flavor.ElevationHits} 处）");
            sb.AppendLine($"· 重复与同构 {SubRepeat:0.#}/10（相邻句同头率 {SameHeadRatio:P0}）");
            sb.AppendLine($"· 流水句 {SubFlow:0.#}/10（占比 {Rhythm.FlowRatio:P0}）");
            if (CharCount < 800)
                sb.AppendLine($"· 样本置信度 {Confidence:0.00}（不足 800 字，" +
                              "总分已按比例折减——短文本的\"没命中\"不能当证据）");

            if (TooShort)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 样本不足 200 字，以上分数仅供参考（统计量不够稳定）。");
            }

            if (Hits.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("命中（前 12 条）：");
                foreach (var h in Hits.Take(12))
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

        /// <summary>相邻句同头率（报告里要显示，得留在报告上）。</summary>
        public double SameHeadRatio { get; init; }

        /// <summary>样本置信度（样本量对结论的支撑程度）。</summary>
        public double Confidence { get; init; } = 1;
    }

    /// <summary>
    /// 本地风格审计：纯统计，不调用模型、不花 token。
    ///
    /// 设计教训（2026-10-27，评审意见）：
    ///   1) **CV 单独用会被刷分**——"前半章长句、后半章短句"照样有高方差。
    ///      所以节奏改成"RMSSD（变化幅度）+ 方向交替率（是否在长短交替）"两个量一起看：
    ///      聚簇型文本的交替率很低，长短交错的自然文本才高。
    ///   2) **固定黄金区间会误伤不同体裁**——段落节奏与对话占比改成相对全书基线来判。
    ///   3) **死词表挡不住同义改写**——"嘴角勾起"换个说法就是"嘴角咧开"，
    ///      所以补了正则句式树（情绪量词 / 感官套话 / 翻译腔 / 冗余代词）、
    ///      比喻密度、连接词密度、程度副词密度、章末升华句、句首状语。
    /// </summary>
    public static class StyleAudit
    {
        // ── 固定词表：只收"高频且几乎总是模型腔"的短语。权重 2 = 一眼 AI ──
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

        // ── 正则句式树：同义改写挡不住，结构挡得住 ──
        private static readonly (Regex Rx, string Kind, string Hint)[] Patterns =
        {
            (new Regex(@"(眼中|眼底|眸中|眸底|眸里|眼里|脸上|嘴角|唇角|心头|脑海|眉梢|指尖|喉结).{0,4}(闪过|掠过|浮现|勾起|咧开|涌起|泛起|漾起|浮起|扭曲|绷紧).{0,4}(一丝|一抹|一股|一缕|一个|一点|一片)"),
                "情绪量词", "模板化情绪描写：器官 + 动词 + 量词，换个词也还是它"),
            (new Regex(@"(空气|四周|整个|全场|空气里|空气中).{0,4}(弥漫|充斥|飘荡|散发|飘浮|充满|凝滞).{0,8}的?(气息|味道|气味|威压|压迫感)"),
                "感官套话", "模板化氛围描写：空气 + 弥漫 + 味道/气息"),
            (new Regex(@"(这一刻|从这一刻起|从那天起|命运的齿轮|无人知晓|没有人知道|没有人能|注定|终将|将是一场|在等待|等待[^，。！？]{0,8}的|悄然转动|才刚刚开始|才刚刚拉开|不过是开始|一切都才|他知道，|她知道，)"),
                "升华", "总结/升华句：模型收尾的本能"),
            (new Regex(@"(下一秒|紧接着|与此同时|然而|就在这时|不得不说|须臾|转瞬间|同一时间|没过多久|不多时)"),
                "状语起手", "句首塞状语糊弄节奏（也是同头规避手法）"),
            (new Regex(@"(被[^，。！？]{0,12}(所|予以|遭到|正式地)|进行了一(场|次)|做出了一?个?[^，。！？]{0,8}的(举动|决定|行为)|流露出[^，。！？]{0,8}的(情绪|神色))"),
                "翻译腔", "翻译腔句式：被字句 / 进行了一场 / 流露出…的情绪"),
            (new Regex(@"(他的眼中|她的眼中|他的手掌|她的手掌|他的目光|她的目光|他的声音|她的声音|他的心中|她的心中)"),
                "冗余代词", "该写「眼底」「掌心」却写成「他的眼中」——翻译腔的残留"),
        };

        // ── 比喻标记 ──
        private static readonly Regex SimileRx =
            new(@"(仿佛|宛如|好似|如同|好像|像是|像[^，。！？]{1,12}一样|像[^，。！？]{1,12}一般)");

        // ── 连接词（翻译腔/公文腔）──
        private static readonly string[] Connectives =
        {
            "然而", "与此同时", "紧接着", "毫无疑问", "某种意义上", "不仅如此", "于是乎",
            "从而", "进而", "除此之外", "总而言之", "换句话说", "众所周知", "显而易见",
        };
        private static readonly Regex ConnectiveAdvanceRx = new(@"随着[^，。！？]{0,8}的推移");

        // ── 程度副词（空洞修饰）──
        private static readonly string[] DegreeAdverbs =
        {
            "极其", "无比", "死死地", "疯狂地", "彻骨", "难以言喻", "不可思议", "深深地",
            "缓缓地", "微微地", "轻轻地", "默默地", "悄然", "极为", "格外", "异常地", "十分地",
        };

        // ── 填充词：只报密度，不进分 ──
        private static readonly string[] Fillers =
        {
            "仿佛", "似乎", "宛如", "不禁", "微微", "缓缓", "轻轻", "默默", "悄然",
        };

        // ── 排比连接结构 ──
        private static readonly string[] Parallels =
        {
            "不仅", "而且", "既", "又", "一边", "不是", "而是", "与其", "不如",
        };

        private static readonly char[] SentenceEnds = { '。', '！', '？', '…', '；', '!', '?', ';' };
        private static readonly char[] ClauseMarks = { '，', ',', '、' };

        /// <summary>切句结果。</summary>
        internal sealed class Segmentation
        {
            public List<string> Sentences = new();
            public List<string> Paragraphs = new();
            public List<int> SentenceLengths = new();
            public List<int> ParagraphLengths = new();
            public int VisibleChars;
            public int DialogueChars;
            public int FlowSentences;
        }

        internal static Segmentation Split(string? text)
        {
            var raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var seg = new Segmentation();

            foreach (var p in raw.Split('\n'))
            {
                var line = p.Trim();
                if (line.Length == 0) continue;
                seg.Paragraphs.Add(line);
                seg.ParagraphLengths.Add(CountVisible(line));
            }

            foreach (var p in seg.Paragraphs)
            {
                var start = 0;
                for (var i = 0; i < p.Length; i++)
                {
                    if (Array.IndexOf(SentenceEnds, p[i]) < 0) continue;
                    var s = p.Substring(start, i - start + 1).Trim();
                    if (CountVisible(s) >= 4) AddSentence(seg, s);
                    start = i + 1;
                }
                var tail = p.Substring(Math.Min(start, p.Length)).Trim();
                if (CountVisible(tail) >= 4) AddSentence(seg, tail);
            }

            seg.VisibleChars = CountVisible(raw);
            seg.DialogueChars = CountDialogueChars(raw);
            return seg;
        }

        private static void AddSentence(Segmentation seg, string s)
        {
            seg.Sentences.Add(s);
            seg.SentenceLengths.Add(CountVisible(s));

            // 流水句：一句里逗号一逗到底（网文常见的"AI 流水句"形态）
            var commas = s.Count(c => Array.IndexOf(ClauseMarks, c) >= 0);
            if (commas >= 3 && CountVisible(s) >= 30) seg.FlowSentences++;
        }

        /// <summary>主入口。不给基线就是绝对判；给了基线就相对判。</summary>
        public static StyleReport Analyze(string? text, StyleBaseline? baseline = null)
        {
            var seg = Split(text);
            var visible = seg.VisibleChars;

            if (seg.Sentences.Count == 0 || visible == 0)
            {
                var empty = new RhythmMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0);
                var emptyFlavor = new FlavorMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                return new StyleReport(0, visible, 0, seg.Paragraphs.Count, empty, emptyFlavor,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, false,
                    Array.Empty<StyleHit>(), Array.Empty<KeyValuePair<string, int>>())
                { SameHeadRatio = 0 };
            }

            var hits = new List<StyleHit>();
            var found = new List<KeyValuePair<string, int>>();

            // ── 节奏：RMSSD + 方向交替率 ──
            var lens = seg.SentenceLengths;
            var mean = lens.Average();
            var cv = Cv(lens);
            var (rmssd, flipRate) = RmssdAndFlip(lens);
            var normRmssd = mean > 0 ? rmssd / mean : 0;
            var flowRatio = (double)seg.FlowSentences / seg.Sentences.Count;

            // ── 对话 ──
            var dialogueRatio = visible == 0 ? 0 : seg.DialogueChars / (double)visible;

            // ── 模型腔各成分 ──
            var raw = string.Join("\n", seg.Paragraphs);

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
            var wordClichePerK = visible == 0 ? 0 : weightSum * 1000.0 / visible;

            var patternHits = 0;
            foreach (var (rx, kind, hint) in Patterns)
            {
                var ms = rx.Matches(raw);
                if (ms.Count == 0) continue;
                patternHits += ms.Count;
                hits.Add(new StyleHit(kind, Excerpt(raw, ms[0].Value), $"{hint}（{ms.Count} 处）"));
            }
            var patternPerK = visible == 0 ? 0 : patternHits * 1000.0 / visible;

            var simileCount = SimileRx.Matches(raw).Count;
            var similePerK = visible == 0 ? 0 : simileCount * 1000.0 / visible;
            if (similePerK >= 2)
                hits.Add(new StyleHit("比喻泛滥", "像…一样 / 仿佛 / 宛如", $"每千字 {similePerK:0.0} 处明喻，太密"));

            var connectiveCount = Connectives.Sum(c => CountOf(raw, c))
                                    + ConnectiveAdvanceRx.Matches(raw).Count;
            var connectivePerK = visible == 0 ? 0 : connectiveCount * 1000.0 / visible;
            if (connectivePerK >= 3)
                hits.Add(new StyleHit("连接词", "然而 / 与此同时 / 紧接着…", $"每千字 {connectivePerK:0.0} 处，翻译腔重"));

            var degreeCount = DegreeAdverbs.Sum(d => CountOf(raw, d));
            var degreePerK = visible == 0 ? 0 : degreeCount * 1000.0 / visible;
            if (degreePerK >= 4)
                hits.Add(new StyleHit("程度副词", "极其 / 无比 / 难以言喻…", $"每千字 {degreePerK:0.0} 处，空洞修饰"));

            var fillerCount = Fillers.Sum(f => CountOf(raw, f));
            var fillerPerK = visible == 0 ? 0 : fillerCount * 1000.0 / visible;

            // ── 章末升华：只看每段最后一句 ──
            var elevationHits = 0;
            foreach (var p in seg.Paragraphs)
            {
                var last = LastSentence(p);
                if (last.Length == 0) continue;
                if (Patterns.Any(x => x.Kind == "升华" && x.Rx.IsMatch(last)))
                {
                    elevationHits++;
                    hits.Add(new StyleHit("升华", Excerpt(raw, last), "段末总结/升华句，模型收尾的本能"));
                }
            }

            // ── 句首状语 ──
            var leadInHits = 0;
            foreach (var s in seg.Sentences)
            {
                var m = Regex.Match(s, @"^(下一秒|紧接着|与此同时|然而|就在这时|不得不说|须臾|转瞬间|同一时间|没过多久|不多时)");
                if (!m.Success) continue;
                leadInHits++;
                if (leadInHits <= 3) hits.Add(new StyleHit("状语起手", Excerpt(raw, m.Value), "用状语开头糊弄节奏"));
            }

            // ── 连续同头 ──
            var runs = new Dictionary<string, int>();
            string prevHead = "";
            var run = 0;
            var sameHead = 0;
            var pairs = 0;
            for (var i = 0; i < seg.Sentences.Count; i++)
            {
                var h = Head(seg.Sentences[i]);
                if (h.Length == 0) { prevHead = ""; run = 0; continue; }
                run = h == prevHead ? run + 1 : 1;
                prevHead = h;
                if (run >= 3) runs[h] = Math.Max(runs.TryGetValue(h, out var old2) ? old2 : 0, run);
                if (i > 0)
                {
                    var prev = Head(seg.Sentences[i - 1]);
                    if (prev.Length > 0 && h.Length > 0)
                    {
                        pairs++;
                        if (prev == h) sameHead++;
                    }
                }
            }
            var sameHeadRatio = pairs == 0 ? 0 : (double)sameHead / pairs;
            foreach (var kv in runs.OrderByDescending(x => x.Value).Take(3))
                hits.Add(new StyleHit("同头", $"{kv.Key}……", $"有连续 {kv.Value} 句用同一个字起头"));

            // 排比
            var parallels = Parallels.Count(w => CountOf(raw, w) >= 2);
            if (parallels >= 3)
                hits.Add(new StyleHit("排比", "不仅…而且 / 既…又 / 一边…一边", "对仗句式密度高"));

            // ── 打分 ──
            var relative = baseline != null;

            // 句长节奏 25：变化幅度 + 是否在长短交替（两个都要，聚簇型骗不过）
            var subRhythm = 25 * (0.45 * Clamp((normRmssd - 0.25) / 0.75)
                                 + 0.55 * Clamp((flipRate - 0.25) / 0.50));

            // 段落节奏 10：有基线看相对（不同体裁不算错），没基线只看 CV
            var medianPara = StyleBaseline.Median(seg.ParagraphLengths);
            var paraRatio = baseline != null && baseline.MedianParagraphLen > 0
                ? medianPara / baseline.MedianParagraphLen : 0;
            // 段落节奏 = 内部参差（一半）+ 与全书各章区间的关系（一半）。
            // ★ 有下限 2 分：排版风格差异（移动端瀑布流短段）不是 AI 味，不能清零。
            var cvTerm = Clamp(Cv(seg.ParagraphLengths) / 0.6);
            var conformTerm = 1.0;
            if (baseline != null && baseline.ChapterCount >= 3)
            {
                var inRange = medianPara >= baseline.ParagraphMin && medianPara <= baseline.ParagraphMax;
                conformTerm = inRange
                    ? 1.0
                    : Clamp(1 - Math.Abs(Math.Log(Math.Max(paraRatio, 0.01))) / 1.5);
            }
            var subParagraph = seg.ParagraphLengths.Count < 3
                ? 5                       // 段落太少，段长统计没意义，给中性分
                : Math.Max(2, 10 * (0.5 * cvTerm + 0.5 * conformTerm));

            // 对话 10：相对全书（孤身潜行章不该扣分）；无基线时用很宽的带
            var dialogueDelta = baseline != null ? dialogueRatio - baseline.DialogueRatio : 0;
            // ★ 没有基线时**不再罚"对话少"**：孤身潜行章没有对话是体裁，不是 AI 味。
            //   只在对话多到不成比例（剧本杀式对白）时扣一点。
            double subDialogue;
            if (baseline != null && baseline.ChapterCount >= 3)
            {
                // 落在"这本书各章本来就有的范围"里 = 正常体裁，不扣分
                var inRange = dialogueRatio >= baseline.DialogueMin && dialogueRatio <= baseline.DialogueMax;
                var dist = inRange
                    ? 0
                    : dialogueRatio < baseline.DialogueMin
                        ? baseline.DialogueMin - dialogueRatio
                        : dialogueRatio - baseline.DialogueMax;
                subDialogue = 10 * Clamp(1 - dist / 0.25);
            }
            else if (baseline != null)
            {
                subDialogue = 10 * Clamp(1 - Math.Abs(dialogueDelta) / 0.50);
            }
            else
            {
                subDialogue = 10 * (dialogueRatio <= 0.70 ? 1.0 : Clamp(1 - (dialogueRatio - 0.70) / 0.25));
            }

            // 模型腔 35
            // ★ 用"最差项主导 + 加权平均"混合，而不是纯平均：
            //   一段文本只要有一项爆表（套话刷屏 / 翻译腔 / 章末升华），
            //   整体就该塌下来；纯平均会让"其他项都没命中"把爆表项稀释掉。
            var flavorComponents = new[]
            {
                Clamp(1 - wordClichePerK / 8),      // 死词表
                Clamp(1 - patternPerK / 10),       // 正则句式
                Clamp(1 - similePerK / 4),         // 比喻泛滥
                Clamp(1 - connectivePerK / 6),     // 连接词
                Clamp(1 - degreePerK / 8),         // 程度副词
                Clamp(1 - elevationHits / 3.0),    // 章末升华
            };
            var flavorWeights = new[] { 0.25, 0.25, 0.15, 0.15, 0.10, 0.10 };
            var flavorAvg = 0.0;
            for (var i = 0; i < flavorComponents.Length; i++)
                flavorAvg += flavorComponents[i] * flavorWeights[i];
            var flavorMin = flavorComponents.Min();
            var subFlavor = 35 * (0.6 * flavorMin + 0.4 * flavorAvg);

            // 重复与同构 10
            var subRepeat = Math.Max(0, 10 * Clamp(1 - sameHeadRatio / 0.35));
            if (parallels >= 3) subRepeat = Math.Max(0, subRepeat - 2);

            // 流水句 10
            var subFlow = 10 * Clamp(1 - flowRatio / 0.35);

            // 样本置信度：字数越少，"没命中套话"这种**否定证据**越不可信。
            // 乘一个 0.5~1 的系数，短样本拿不到高分（也就堵死了"59 字稻草人跑 0 分"
            // 这种自嗨式验证）。
            var trust = 0.5 + 0.5 * Math.Min(1.0, visible / 800.0);
            var score = (int)Math.Round(
                (subRhythm + subParagraph + subDialogue + subFlavor + subRepeat + subFlow) * trust);
            score = Math.Clamp(score, 0, 100);

            var rhythm = new RhythmMetrics(mean, cv, rmssd, normRmssd, flipRate,
                seg.ParagraphLengths.Count == 0 ? 0 : seg.ParagraphLengths.Average(),
                Cv(seg.ParagraphLengths), flowRatio, seg.FlowSentences);
            var flavor = new FlavorMetrics(wordClichePerK, found.Sum(f => f.Value), patternHits,
                patternPerK, similePerK, connectivePerK, degreePerK, elevationHits, fillerCount, fillerPerK, leadInHits);

            return new StyleReport(
                score, visible, seg.Sentences.Count, seg.Paragraphs.Count,
                rhythm, flavor, dialogueRatio, dialogueDelta, paraRatio,
                subRhythm, subParagraph, subDialogue, subFlavor, subRepeat, subFlow,
                relative, hits.Take(20).ToList(), found.Take(12).ToList())
            { SameHeadRatio = sameHeadRatio, Confidence = trust };
        }

        /// <summary>可见字数：中英文数字，不含标点与空白。密度类指标都按它算。</summary>
        private static int CountVisible(string s)
        {
            var n = 0;
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) n++;
            return n;
        }

        /// <summary>变异系数；样本不足两个时返回 0（仅供参考，不能当主指标）。</summary>
        private static double Cv(List<int> values)
        {
            if (values.Count < 2) return 0;
            var mean = values.Average();
            if (mean <= 0) return 0;
            var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            return Math.Sqrt(variance) / mean;
        }

        /// <summary>
        /// 相邻句长差的均方根（RMSSD，借鉴心率变异性）+ **长短方向交替率**。
        ///
        /// ★ 为什么两个都要：只看 RMSSD，"前半章长句、后半章短句"依然能拿高分
        ///   （过渡处那一两个大跳变贡献了平方和）；加上交替率就能堵住——
        ///   聚簇型文本在段内几乎不交替，而自然的长短句是句句在换方向。
        /// </summary>
        internal static (double Rmssd, double FlipRate) RmssdAndFlip(List<int> lens)
        {
            if (lens.Count < 3) return (0, 0);
            var sum = 0.0;
            var n = 0;
            var meaningful = 0;
            var flips = 0;
            int? prevDir = null;
            for (var i = 1; i < lens.Count; i++)
            {
                var d = lens[i] - lens[i - 1];
                sum += d * d;
                n++;
                if (Math.Abs(d) < 3) continue;      // 差太小不算"换向"
                meaningful++;
                var dir = d > 0 ? 1 : -1;
                if (prevDir.HasValue && prevDir.Value != dir) flips++;
                prevDir = dir;
            }
            var rmssd = n == 0 ? 0 : Math.Sqrt(sum / n);
            var flip = meaningful == 0 ? 0 : (double)flips / meaningful;
            return (rmssd, flip);
        }

        private static double Clamp(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

        /// <summary>引号内的字数。用于算对话占比。</summary>
        private static int CountDialogueChars(string text)
        {
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
            return inside;
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
        /// 句首特征：第一个可见字。
        /// ★ 标点会被跳过，所以「对话」不会把同头率顶高——引号内的第一个字才算。
        /// </summary>
        private static string Head(string sentence)
        {
            foreach (var c in sentence)
                if (char.IsLetterOrDigit(c)) return c.ToString();
            return "";
        }

        /// <summary>段落里的最后一句（以句末标点收尾的那一句，不是尾巴残句）。</summary>
        private static string LastSentence(string paragraph)
        {
            var start = 0;
            var last = paragraph.Trim();
            for (var i = 0; i < paragraph.Length; i++)
            {
                if (Array.IndexOf(SentenceEnds, paragraph[i]) < 0) continue;
                var s = paragraph.Substring(start, i - start + 1).Trim();
                if (s.Length > 0) last = s;
                start = i + 1;
            }
            return last;
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
