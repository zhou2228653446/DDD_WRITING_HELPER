using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace 编辑器.Services
{
    /// <summary>一条命中：哪个指标、命中了什么、该怎么处理。</summary>
    public sealed record StyleHit(string Kind, string Excerpt, string Hint);

    /// <summary>节奏类指标（事实，不判好坏）。</summary>
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
        int LeadInHits,
        double BodyPartPerK,
        double TagMonotonyRatio);

    /// <summary>
    /// 一次风格审计的结果。
    /// ★ v5（2026-10-28）：**0~100 的"AI 味指数"已退役**——统计指标抓不住"AI 味"
    ///   这个隐形目标（v4 实测 AI 与人写都在 91~99，毫无区分度）。现在只输出
    ///   两类可信的东西：**事实统计**（读者自己判断）与**病态证据清单**
    ///   （翻译腔/升华/僵笑弧度这类跨作者通病，逐条带位置）。
    ///   "像不像某位作者"走作者相似度线（AuthorStyleBuilder.ScoreSimilarity）。
    /// </summary>
    public sealed record StyleReport(
        int CharCount,
        int SentenceCount,
        int ParagraphCount,
        RhythmMetrics Rhythm,
        FlavorMetrics Flavor,
        double DialogueRatio,
        IReadOnlyList<StyleHit> Hits,
        IReadOnlyList<KeyValuePair<string, int>> Clichés)
    {
        /// <summary>样本太短时统计量不稳（几十个字上没有意义）。</summary>
        public bool TooShort => CharCount < 200;

        /// <summary>相邻句同头率。</summary>
        public double SameHeadRatio { get; init; }

        /// <summary>平均每句逗号数（标点习惯的事实）。</summary>
        public double CommaPerSentence { get; init; }

        /// <summary>≤8 字短句占比。</summary>
        public double ShortSentenceRatio { get; init; }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【风格审计】样本 {CharCount} 字 / {SentenceCount} 句 / {ParagraphCount} 段" +
                          "（只报事实与病态证据，不打总分）");

            if (SentenceCount == 0)
            {
                sb.Append("正文为空或没有可分析的句子。");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine($"· 事实：中位句长 {Rhythm.MeanSentenceLen:0} 字（CV {Rhythm.SentenceCv:0.00}）、" +
                          $"中位段长 {Rhythm.MeanParagraphLen:0} 字、对话 {DialogueRatio:P0}、" +
                          $"每句逗号 {CommaPerSentence:0.0}、短句占比 {ShortSentenceRatio:P0}、同头率 {SameHeadRatio:P0}");
            sb.AppendLine($"· 模型腔密度：死词 {Flavor.WordClichePerK:0.0}/千字、句式 {Flavor.PatternPerK:0.0}/千字、" +
                          $"比喻 {Flavor.SimilePerK:0.0}/千字、连接词 {Flavor.ConnectivePerK:0.0}/千字、" +
                          $"程度副词 {Flavor.DegreeAdverbPerK:0.0}/千字、章末升华 {Flavor.ElevationHits} 处");
            sb.AppendLine($"· 流水句占比 {Rhythm.FlowRatio:P0}、五官/神态词 {Flavor.BodyPartPerK:0.0}/千字");

            if (TooShort)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 样本不足 200 字，以上统计量不稳定，仅供参考。");
            }

            if (Hits.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("病态与套话命中（证据，逐条可改）：");
                foreach (var h in Hits.Take(12))
                    sb.AppendLine($"· [{h.Kind}] {h.Excerpt} —— {h.Hint}");
            }
            else
            {
                sb.AppendLine();
                sb.Append("病态与套话命中：无。");
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
            (new Regex(@"(眼中|眼底|眸中|眸底|眸里|眼里|目光|眼神|眸子|脸上|面色|神情|嘴角|唇角|心头|脑海|眉梢|指尖|喉结|眉头).{0,4}(闪过|掠过|浮现|勾起|咧开|扯出|涌起|泛起|漾起|浮起|扭曲|绷紧|凝|沉|凛|僵|垮|拧).{0,4}(一丝|一抹|一股|一缕|一个|一点|一片|半分|几分)?"),
                "情绪量词", "模板化情绪描写：器官 + 动词（+ 量词），换个词也还是它"),
            (new Regex(@"(目光|眼神|眸子|脸色|神色|神情|面色|嘴角|唇角)(一凝|一沉|一凛|一紧|一变|一暗|一僵|微变|微沉|微凛)"),
                "神态微变", "「目光一凝」「脸色一沉」——模型最爱的瞬时神态切换"),
            // ★ v4：评审连续两轮点名的漏抓——"嘴角裂开一个生硬的弧度""嘴角扯开一个
            //   毫无温度的弧度"。动词表换成 裂开/扯开/扯出/绽开/堆出/挤出 也不能靠
            //   情绪量词那条盖住（它们的名词结尾是 弧度/冷笑/笑意，不是量词）。
            (new Regex(@"(嘴角|唇角|脸上|面上|面庞|脸上)(裂开|扯开|扯出|绽开|堆出|挤出|挑起|扬起)[^。！？]{0,8}(弧度|冷笑|笑意|笑容)"),
                "僵笑弧度", "「嘴角裂开一个生硬的弧度」——模板化狞笑，换个动词也一样"),
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
            (new Regex(@"赫然(写着|印着|出现|挂着|站着|是)"),
                "震惊副词", "「赫然写着」——模型表达意外发现的固定件"),
        };

        // ── 比喻标记 ──
        private static readonly Regex SimileRx =
            new(@"(仿佛|宛如|好似|如同|好像|像是|像[^，。！？]{1,12}一样|像[^，。！？]{1,12}一般|如[^，。！？]{1,10}般)");

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
                // ★ 过滤章节标题行（"=== 第3章 常住人口摸排（1856 字）==="）：
                //   它不是叙事文本，混进第一段会把句长/段长统计全部带歪。
                if (Regex.IsMatch(line, @"^={2,}.*={2,}$")) continue;
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
            seg.DialogueChars = CountInteractiveDialogue(seg.Paragraphs);
            return seg;
        }

        /// <summary>
        /// 判断一段是否"几乎全是引号内容"（引号外的叙述可见字 &lt; 15）。
        /// </summary>
        private static bool IsQuoteOnly(string p)
        {
            if (CountVisible(p) == 0) return false;
            var outside = Regex.Replace(p, "[「『“‘][^」』”’]*[」』”’]", " ");
            return CountVisible(outside) < 15;
        }

        /// <summary>
        /// 交互对白字符数 = 引号内字符 − **单人演讲/独白**。
        /// ★ 连续 ≥3 段几乎全是引号的内容是"长篇独白 / 大会演讲"（如第 90 章体育馆
        ///   演讲），文学体裁上根本不是"两人打乒乓球的对白"。把它算进对话占比，
        ///   一个感人演讲章会被误判成 85% 空心化对白——体裁误判（评审点名）。
        /// </summary>
        internal static int CountInteractiveDialogue(List<string> paragraphs)
        {
            var mono = new bool[paragraphs.Count];
            var run = 0;
            var runStart = 0;
            for (var i = 0; i < paragraphs.Count; i++)
            {
                if (!IsQuoteOnly(paragraphs[i])) { run = 0; continue; }
                if (run == 0) runStart = i;
                run++;
                if (run >= 3)
                    for (var k = runStart; k <= i; k++) mono[k] = true;
            }

            var pairs = new (char Open, char Close)[] { ('「', '」'), ('『', '』'), ('“', '”'), ('‘', '’') };
            var total = 0;
            for (var i = 0; i < paragraphs.Count; i++)
            {
                if (mono[i]) continue;
                var p = paragraphs[i];
                foreach (var (open, close) in pairs)
                {
                    var j = 0;
                    while (j < p.Length)
                    {
                        var a = p.IndexOf(open, j);
                        if (a < 0) break;
                        var b = p.IndexOf(close, a + 1);
                        if (b < 0) break;
                        total += CountVisible(p.Substring(a + 1, b - a - 1));
                        j = b + 1;
                    }
                }
            }
            return total;
        }

        private static void AddSentence(Segmentation seg, string s)
        {
            seg.Sentences.Add(s);
            seg.SentenceLengths.Add(CountVisible(s));

            // 流水句：一句里逗号一逗到底（网文常见的"AI 流水句"形态）。
            // ★ 阈值 ≥4 逗号且 ≥45 字：3 逗号 30 字会误伤"同一主语的连续动作链"
            //   （那恰恰是人写得出色的长句）。
            var commas = s.Count(c => Array.IndexOf(ClauseMarks, c) >= 0);
            if (commas >= 4 && CountVisible(s) >= 45) seg.FlowSentences++;
        }

        /// <summary>主入口。只测事实与病态证据，不打分（v5）。</summary>
        public static StyleReport Analyze(string? text)
        {
            var seg = Split(text);
            var visible = seg.VisibleChars;

            if (seg.Sentences.Count == 0 || visible == 0)
            {
                var empty = new RhythmMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0);
                var emptyFlavor = new FlavorMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                return new StyleReport(visible, 0, seg.Paragraphs.Count, empty, emptyFlavor,
                    0, Array.Empty<StyleHit>(), Array.Empty<KeyValuePair<string, int>>())
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
                // ★ 用 Match 的 Index/Length 直接切上下文，保证命中词本身一定在
                //   摘录里——以前传 m.Value 再 IndexOf，遇到正则匹配跨度含不可见
                //   变化时会产出 "…。" 这种读者看不懂抓了什么的空摘录。
                var m0 = ms[0];
                hits.Add(new StyleHit(kind, ExcerptAt(raw, m0.Index, m0.Length), $"{hint}（{ms.Count} 处）"));
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

            // ── 词汇复用：五官/神态词密度（AI 在千字内能把这个表用 15 次）──
            var bodyParts = new[]
            {
                "目光", "眼神", "眼眸", "眼睛", "声音", "手指", "眉头", "嘴角", "唇角",
                "脸上", "心中", "喉结", "眸子", "神情", "神色", "脸色", "瞳孔",
            };
            var bodyCount = bodyParts.Sum(w => CountOf(raw, w));
            var bodyPerK = visible == 0 ? 0 : bodyCount * 1000.0 / visible;

            // 对话引导语单调：引号句的引导部分只剩"说/道/问"而没有动作
            var dialogueSentences = 0;
            var taggedSentences = 0;
            foreach (var sen in seg.Sentences)
            {
                if (!sen.Contains('「') && !sen.Contains('“') && !sen.Contains('"')) continue;
                dialogueSentences++;
                var outside = Regex.Replace(sen, @"「[^」]*」|“[^”]*”|""[^""]*""", " ");
                if (Regex.IsMatch(outside, "(说|道|问|答|开口|喊|叫)") && CountVisible(outside) < 8)
                    taggedSentences++;
            }
            var tagMonotony = dialogueSentences == 0 ? 0 : (double)taggedSentences / dialogueSentences;

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

            // ── v5：只出事实，不出分数 ──
            // "AI 味"抓不住（隐形目标、阈值全靠猜），能可信报告的是：
            // ① 事实统计（自己判断）② 病态证据（hits，跨作者通病）。
            // 打分职能移交给 AuthorStyleBuilder.ScoreSimilarity（对具体档案比）。
            var rhythm = new RhythmMetrics(mean, cv, rmssd, normRmssd, flipRate,
                seg.ParagraphLengths.Count == 0 ? 0 : seg.ParagraphLengths.Average(),
                Cv(seg.ParagraphLengths), flowRatio, seg.FlowSentences);
            var flavor = new FlavorMetrics(wordClichePerK, found.Sum(f => f.Value), patternHits,
                patternPerK, similePerK, connectivePerK, degreePerK, elevationHits, fillerCount, fillerPerK, leadInHits,
                bodyPerK, tagMonotony);

            // 事实层补充：每句逗号数与短句占比（切句把收尾引号当断句，见 AuthorStyle 同款修正）
            var commaTotal = raw.Count(c => c == '，');
            var sentenceCount = 0;
            var shortSentences = 0;
            foreach (var sen in Regex.Split(raw, @"[。！？…；”」]+"))
            {
                var n = CountVisible(sen);
                if (n >= 4) { sentenceCount++; if (n <= 8) shortSentences++; }
            }

            return new StyleReport(
                visible, seg.Sentences.Count, seg.Paragraphs.Count,
                rhythm, flavor, dialogueRatio,
                hits.Take(20).ToList(), found.Take(12).ToList())
            {
                SameHeadRatio = sameHeadRatio,
                CommaPerSentence = sentenceCount == 0 ? 0 : Math.Round((double)commaTotal / sentenceCount, 2),
                ShortSentenceRatio = sentenceCount == 0 ? 0 : Math.Round((double)shortSentences / sentenceCount, 3),
            };
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
            var mean = lens.Average();
            // ★ 死区滤波：相邻句长变化 < max(4 字, 平均句长 25%) 不算"换档"。
            //   没有这一条，"20/21/19/20/18"这种 ±1 字抖动的匀速催眠句
            //   交替率能刷到 100%——评审点名的漏洞。
            var deadband = Math.Max(4.0, 0.25 * mean);
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
                if (Math.Abs(d) < deadband) continue;
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
            return ExcerptAt(text, i, phrase.Length);
        }

        /// <summary>按命中位置切上下文（保证命中词本身完整出现在摘录里）。</summary>
        private static string ExcerptAt(string text, int index, int length)
        {
            var start = Math.Max(0, index - 10);
            var end = Math.Min(text.Length, index + length + 10);
            var s = text.Substring(start, end - start).Replace('\n', ' ').Trim();
            return $"…{s}…";
        }
    }
}
