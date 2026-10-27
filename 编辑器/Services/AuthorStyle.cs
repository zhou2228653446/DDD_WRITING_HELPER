using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace 编辑器.Services;

/// <summary>
/// 作者风格档案——「让文字像某个特定作者」的核心数据。
///
/// 设计前提（用户明确）：**模仿对象不止一个人**，不同书可以用不同作者的文风，
/// 所以档案按作者一人一份存全局库（<see cref="AuthorStyleStore"/>），
/// 书通过 <see cref="NovelProject.AuthorStyleId"/> 绑定，请求级可再覆盖。
///
/// 三层指纹（全部本地构建、离线可用）：
///   L1 硬指纹：句长/段长分布、对话占比、签名词表（词→目标频次）、对话引导语表
///   L2 软指纹：按场景自动挑选的 few-shot 锚定样本（注入可缓存 system 段）
///   L3 规则：LLM 离线归纳、人工确认后的「作者守则」（可为空，等 P3 补）
///
/// 为什么不叫「去 AI 味」：土豆本人高频词「旋即 1.1 / 缓缓 1.0 / 微微 0.9 / 一抹 0.5
/// （每千字）」全在 AI 味词表上——绝对化的"像 AI"评分把名家用词当病灶。
/// 换成"与目标作者的偏离度"后，这些词从扣分项变成得分项。
/// </summary>
public sealed record AuthorStyleProfile
{
    /// <summary>档案 Id（文件名即 Id，如 tiancantudou.json）。稳定、可读。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示名（作者笔名）。</summary>
    public string Name { get; init; } = "";

    /// <summary>语料来源说明（文件名 + 字数），便于溯源。</summary>
    public string Source { get; init; } = "";

    public DateTime Created { get; init; } = DateTime.Now;

    // ── L1 硬指纹 ──

    public double SentenceMedian { get; init; }
    public double SentenceCv { get; init; }
    public double ParagraphMedian { get; init; }
    public double ParagraphP25 { get; init; }
    public double ParagraphP75 { get; init; }
    public double DialogueRatio { get; init; }

    /// <summary>平均每句逗号数——标点习惯：连缀长句（土豆≈2.5）vs 短促断句（余华≈1.2）。</summary>
    public double CommaPerSentence { get; init; }

    /// <summary>≤8 字短句占比——白描作者用短句剁节奏。</summary>
    public double ShortSentenceRatio { get; init; }

    /// <summary>连续引号段的中位长度（段数）——对话节奏：短促乒乓 vs 长段训话。</summary>
    public double DialogueRunMedian { get; init; }

    /// <summary>签名词 → 每千字目标频次（区间中值，容差 ±35%）。按频次降序。</summary>
    public IReadOnlyList<SignatureWord> SignatureWords { get; init; } = Array.Empty<SignatureWord>();

    /// <summary>对话引导语（"轻声道""淡淡地道"），按语料频次降序。</summary>
    public IReadOnlyList<string> DialogueTags { get; init; } = Array.Empty<string>();

    // ── L2 软指纹 ──

    /// <summary>few-shot 锚定样本。每类场景至多一段，总计 ≤6 段、每段 200~320 字。</summary>
    public IReadOnlyList<StyleSample> Samples { get; init; } = Array.Empty<StyleSample>();

    // ── L3 规则 ──

    /// <summary>作者守则（LLM 归纳 + 人工确认）。空 = 尚未提炼。</summary>
    public IReadOnlyList<string> Rules { get; init; } = Array.Empty<string>();

    public sealed record SignatureWord(string Word, double PerK);

    public sealed record StyleSample(string Scene, string Text);
}

/// <summary>
/// 作者风格档案库。全局一份，多书共用：
/// 目录 = {配置目录}/author_profiles/*.json（Id 即文件名）。
/// 配置目录解析与 SystemPromptStore 同源：TDXCLAW_CONFIG_DIR → %APPDATA%\TdxClaw。
/// </summary>
public static class AuthorStyleStore
{
    public const string DirName = "author_profiles";

    private static string? _dirOverride;   // 测试注入用

    /// <summary>档案目录（不存在时由 Save 创建）。</summary>
    public static string Directory
    {
        get
        {
            if (_dirOverride != null) return _dirOverride;
            var cfg = Environment.GetEnvironmentVariable("TDXCLAW_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(cfg))
                cfg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");
            return Path.Combine(cfg, DirName);
        }
    }

    internal static void SetDirForTests(string dir) => _dirOverride = dir;
    internal static void ClearDirForTests() => _dirOverride = null;

    /// <summary>全部档案（按创建时间降序）。目录不存在返回空表。</summary>
    public static List<AuthorStyleProfile> List()
    {
        var result = new List<AuthorStyleProfile>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                var p = System.Text.Json.JsonSerializer.Deserialize<AuthorStyleProfile>(
                    File.ReadAllText(file), JsonOpts);
                if (p != null && !string.IsNullOrWhiteSpace(p.Id)) result.Add(p);
            }
            catch { /* 单个档案损坏不拖垮整库 */ }
        }
        return result.OrderByDescending(p => p.Created).ToList();
    }

    public static AuthorStyleProfile? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var file = SafePath(id);
        if (file == null || !File.Exists(file)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AuthorStyleProfile>(
                File.ReadAllText(file), JsonOpts);
        }
        catch { return null; }
    }

    public static void Save(AuthorStyleProfile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = SafePath(profile.Id)
                   ?? throw new ArgumentException($"非法档案 Id：{profile.Id}");
        File.WriteAllText(file,
            System.Text.Json.JsonSerializer.Serialize(profile, JsonOpts));
    }

    public static bool Delete(string id)
    {
        var file = SafePath(id);
        if (file == null || !File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>Id 只允许字母数字连字符下划线与中文——防路径穿越。</summary>
    private static string? SafePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 60) return null;
        if (!Regex.IsMatch(id, @"^[\w\-一-龥]+$")) return null;
        return Path.Combine(Directory, id + ".json");
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// 从一部（或多部）作品全文构建风格档案。
///
/// 管线：清洗 → 分章 → 指纹统计 → 场景分桶 → 代表段挑选。
/// 挑样标准：先用 StyleAudit 剔除跨作者通用的模型腔（翻译腔/升华/僵笑弧度），
/// 再按「签名词密度最接近全书均值」挑典型段——要"典型的他"，不要"最华丽的一段"。
/// </summary>
public static class AuthorStyleBuilder
{
    private static readonly string[] CandidateSignatureWords =
    {
        "旋即", "顿时", "缓缓", "微微", "一抹", "瞬间", "随即", "忽然", "刹那", "骤然",
        "淡淡", "轻轻", "一丝", "一股", "一声", "咧嘴", "嘴角", "眸子", "目光", "眼神",
        "怒喝", "低喝", "暴喝", "冷哼", "咬牙", "握拳", "皱眉", "挑眉", "撇嘴", "摊手",
        "苦笑", "冷笑", "嗤笑", "大笑", "凛然", "漠然", "悚然", "哑然", "骇然", "愕然",
        "轰鸣", "爆喝", "狞笑", "阴沉", "扭曲", "翻涌", "涌起", "浮现", "弥漫", "笼罩",
        // 朴素派（白描作者：余华式）——签名落在动作与具体名词上
        "沉默", "眼泪", "看着", "笑着", "坐下", "低头", "抬头", "点头", "摇头", "蹲下",
        "屋里", "日子", "声音", "身体", "月光", "阳光", "街道", "田里", "夜里", "早上",
        "中午", "村口", "说完", "半天", "咳嗽", "眯眼",
    };

    private static readonly string[] PathologicalKinds = { "翻译腔", "升华", "僵笑弧度" };

    private static readonly string[] ActionVerbs =
    {
        "冲", "轰", "斩", "劈", "砸", "爆", "射", "炸", "撞", "踢", "抓", "撕", "绞", "砸落",
    };

    public static AuthorStyleProfile Build(string name, string corpusText, string sourceNote)
    {
        var chapters = SplitChapters(corpusText);
        if (chapters.Count == 0)
            throw new InvalidOperationException("没识别出任何章节行（需要「第N章」格式的章节标题）。");

        var allText = string.Join("\n", chapters);
        var visible = VisibleChars(allText);

        // ── L1 指纹 ──
        var sentenceLens = new List<int>();
        foreach (var ch in chapters) CollectSentences(ch, sentenceLens);
        var paraLens = new List<int>();
        foreach (var ch in chapters)
            foreach (var p in ch.Split('\n'))
                if (VisibleChars(p) >= 4) paraLens.Add(VisibleChars(p));

        var dialogueChars = chapters.Sum(CountDialogueChars);
        var dialogueRatio = visible == 0 ? 0 : (double)dialogueChars / visible;

        // 标点习惯与对话节奏。★ 分子分母必须同源：句子切分带收尾引号
        // （网文对话常无句末标点，不算会虚增三倍），且短句占比的分母
        // 就是这一次切分的句数——上版分母用了另一套切分，两个数互相打架。
        var commaTotal = 0;
        var sentenceTotal = 0;
        var shortSentences = 0;
        foreach (var ch in chapters)
        {
            commaTotal += ch.Count(c => c == '，');
            foreach (var sen in Regex.Split(ch, @"[。！？…；”」]+"))
            {
                var n = VisibleChars(sen);
                if (n >= 4)
                {
                    sentenceTotal++;
                    if (n <= 8) shortSentences++;
                }
            }
        }
        var dialogueRuns = new List<int>();
        foreach (var ch in chapters)
        {
            var run = 0;
            foreach (var p in ch.Split('\n'))
            {
                if (IsQuoteOnly(p)) run++;
                else if (run > 0) { dialogueRuns.Add(run); run = 0; }
            }
            if (run > 0) dialogueRuns.Add(run);
        }

        // 签名词：候选池中频次 ≥0.3/千字 的，取前 16
        var sig = CandidateSignatureWords
            .Select(w => (Word: w, PerK: visible == 0 ? 0 : CountOf(allText, w) * 1000.0 / visible))
            .Where(x => x.PerK >= 0.3)
            .OrderByDescending(x => x.PerK)
            .Take(16)
            .Select(x => new AuthorStyleProfile.SignatureWord(x.Word, Math.Round(x.PerK, 2)))
            .ToList();

        // 引导语：X道 家族 top 8
        var body = allText.Length > 4_000_000 ? allText[^4_000_000..] : allText;
        // ★ 停用词：「知道/听到/看到…」也是"X道，"形态，但不是对话引导语——
        //   不滤的话土豆语料的引导语表第一位是「他知道」（实测穿帮）。
        // ★ 停用词按**后缀**判：贪婪捕获会把「他知道，」整块抓成候选（组=「他知道」
        //   而非「知道」），按词精确匹配滤不掉——实测穿帮过一次。
        var notTagSuffix = new[] { "知道", "得到", "做到", "用到", "想到", "听到", "看到",
            "感到", "跑到", "走到", "来到", "遇到", "收到", "接到", "意识到", "注意到" };
        var notTagExact = new HashSet<string> { "一条", "这道", "那道", "几道", "数道" };
        var tags = Regex.Matches(body, @"([\u4e00-\u9fa5]{1,3}道)[，：]")
            // ★ 引号邻近校验：真引导语必然紧挨引号（说话人刚开口/刚说完）。
            //   没有这条，「一条街道，」这类"X道，"的巧合同现会混进表里
            //   （余华语料实测穿帮）。
            .Where(m => QuoteNearby(body, m.Index, m.Length))
            .Select(m => m.Groups[1].Value)
            .Where(w => !notTagExact.Contains(w) && !notTagSuffix.Any(w.EndsWith))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => g.Key)
            .ToList();

        // ── L2 样本 ──
        var samples = PickSamples(chapters, allText, visible);

        return new AuthorStyleProfile
        {
            Id = ToId(name),
            Name = name,
            Source = sourceNote,
            SentenceMedian = Math.Round(Median(sentenceLens), 1),
            SentenceCv = Math.Round(Stdev(sentenceLens) / Math.Max(1, sentenceLens.Average()), 2),
            ParagraphMedian = Math.Round(Median(paraLens), 1),
            ParagraphP25 = Math.Round(Quantile(paraLens, 0.25), 1),
            ParagraphP75 = Math.Round(Quantile(paraLens, 0.75), 1),
            DialogueRatio = Math.Round(dialogueRatio, 3),
            CommaPerSentence = sentenceTotal == 0 ? 0 : Math.Round((double)commaTotal / sentenceTotal, 2),
            ShortSentenceRatio = sentenceTotal == 0 ? 0 : Math.Round((double)shortSentences / sentenceTotal, 3),
            DialogueRunMedian = Math.Round(Median(dialogueRuns), 1),
            SignatureWords = sig,
            DialogueTags = tags,
            Samples = samples,
        };
    }

    /// <summary>生成请求用的锚定块（进可缓存 system 段）。约 1000~1400 token。</summary>
    public static string BuildAnchorBlock(AuthorStyleProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【作者文风锚定 —— 目标作者：{p.Name}】");
        sb.AppendLine("以下要素统计自该作者的真实作品语料。本次写作在题材与剧情之外，");
        sb.AppendLine("句长、用词、引导语、段落节奏必须向这位作者对齐。");
        sb.AppendLine();
        sb.AppendLine($"· 节奏：中位句长 {p.SentenceMedian:0} 字（句长 CV {p.SentenceCv:0.00}）；" +
                      $"中位段长 {p.ParagraphMedian:0} 字（常见区间 {p.ParagraphP25:0}~{p.ParagraphP75:0} 字），短段瀑布流排版。");
        sb.AppendLine($"· 对话占比约 {p.DialogueRatio:P0}；对话引导语用「{string.Join("、", p.DialogueTags.Take(5))}」这类" +
                      (p.DialogueTags.Any(t => t.EndsWith("道")) ? "「X道」家族，不要用翻译腔的「他说/他说道」堆砌。" : "表达。"));
        sb.AppendLine($"· 标点：平均每句 {p.CommaPerSentence:0.0} 个逗号、{p.ShortSentenceRatio:P0} 的句子不超过 8 字——" +
                      (p.CommaPerSentence >= 2.0 ? "长句连缀铺陈，靠逗号推进。" : "短句剁节奏，句号换气，别把句子用逗号连成一长串。"));
        sb.AppendLine($"· 对话节奏：连续引号段中位 {p.DialogueRunMedian:0} 段——" +
                      (p.DialogueRunMedian >= 3 ? "对话常连发成片（乒乓快节奏或成段训话）。" : "对话一两段就切回叙述，别让角色滔滔不绝。"));
        if (p.SignatureWords.Count > 0)
        {
            sb.AppendLine($"· 标志词（按自然频次使用，不要堆砌）：{string.Join("、", p.SignatureWords.Take(10).Select(w => w.Word))}。");
        }
        if (p.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【作者守则】");
            foreach (var r in p.Rules) sb.AppendLine($"- {r}");
        }
        if (p.Samples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【风格示例（摘自其原文，模仿基准）】");
            foreach (var s in p.Samples)
            {
                sb.AppendLine($"──{s.Scene}──");
                sb.AppendLine(s.Text);
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    //  分章（混合格式：行首「第596章」与缩进「第001章」并存）
    // ------------------------------------------------------------------

    internal static List<string> SplitChapters(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var chapterRx = new Regex(@"^\s*第[0-9〇零一二三四五六七八九十百千两]+章.*$");
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            // 清洗：分节标记、书目广告
            if (trimmed.StartsWith("===")) continue;
            if (trimmed.Contains("申明:本书") || trimmed.Contains("申明：本书")) continue;
            if (trimmed.StartsWith("《") && trimmed.Contains("来自")) continue;
            if (trimmed.StartsWith("作者：")) continue;
            if (trimmed.StartsWith("分节阅读")) continue;

            if (chapterRx.IsMatch(line))
            {
                if (current.Length > 0)
                {
                    var ch = current.ToString().Trim();
                    if (VisibleChars(ch) >= 300) result.Add(ch);   // 太短的是广告页/目录，丢
                }
                current.Clear();
                continue;   // 章节标题行本身不进正文
            }
            if (trimmed.Length > 0) current.AppendLine(trimmed);
        }
        if (current.Length > 0)
        {
            var ch = current.ToString().Trim();
            if (VisibleChars(ch) >= 400) result.Add(ch);
        }
        return result;
    }

    // ------------------------------------------------------------------
    //  场景分桶与代表段挑选
    // ------------------------------------------------------------------

    internal static List<AuthorStyleProfile.StyleSample> PickSamples(
        List<string> chapters, string allText, int visible)
    {
        // 场景分桶：对话交锋 / 战斗冲突 / 环境氛围 / 日常推进。
        // ★ 先按段分类，再把**同场景连续段落**聚成 250~350 字的窗口——
        //   跨场景聚窗会把对话段和环境段搅在一起，哪种场景都判不准。
        var buckets = new Dictionary<string, List<List<string>>>();
        foreach (var ch in chapters)
        {
            var paras = ch.Split('\n').Where(p => VisibleChars(p) >= 4).ToList();
            if (paras.Count < 3) continue;

            List<string> window = new();
            var windowScene = "";
            var winChars = 0;
            foreach (var p in paras)
            {
                var scene = SceneOf(p);
                if (scene != windowScene && window.Count > 0)
                {
                    Flush(window, winChars, windowScene, buckets);
                    window = new List<string>();
                    winChars = 0;
                }
                windowScene = scene;
                window.Add(p);
                winChars += VisibleChars(p);
                if (winChars >= 300)
                {
                    Flush(window, winChars, windowScene, buckets);
                    window = new List<string>();
                    winChars = 0;
                    windowScene = "";
                }
            }
            if (window.Count > 0) Flush(window, winChars, windowScene, buckets);
        }

        // 章首/章末：起笔与收束方式靠指令说不清，靠样本一学就会。
        // 只取足额章（≥600 字）的首尾窗，避开广告残章。
        foreach (var ch in chapters)
        {
            if (VisibleChars(ch) < 600) continue;
            // ★ 前言/目录页不是叙事文本：余华档案的开篇样本曾抓到「外文版评论摘要」。
            var head = ch[..Math.Min(ch.Length, 200)];
            if (Regex.IsMatch(head, "目录|Contents|自序|序言|附录|后记|出版|评论|评论")) continue;
            if (!ch.Contains('“') && !ch.Contains('”') && !ch.Contains('「')) continue;
            var paras = ch.Split('\n').Where(x => VisibleChars(x) >= 4).ToList();
            if (paras.Count < 4) continue;

            var opening = new List<string>();
            var openChars = 0;
            foreach (var p in paras)
            {
                opening.Add(p);
                openChars += VisibleChars(p);
                if (openChars >= 250) break;
            }
            Flush(opening, openChars, "开篇起笔", buckets);

            var closing = new List<string>();
            var closeChars = 0;
            for (var i = paras.Count - 1; i >= 0 && closeChars < 250; i--)
            {
                closing.Insert(0, paras[i]);
                closeChars += VisibleChars(paras[i]);
            }
            Flush(closing, closeChars, "章末收束", buckets);
        }

        var meanSig = MeanSignatureDensity(allText, visible);
        var picked = new List<AuthorStyleProfile.StyleSample>();
        foreach (var scene in new[] { "开篇起笔", "对话交锋", "战斗冲突", "环境氛围", "日常推进", "章末收束" })
        {
            if (!buckets.TryGetValue(scene, out var windows) || windows.Count == 0) continue;
            AuthorStyleProfile.StyleSample? best = null;
            var bestDist = double.MaxValue;
            foreach (var w in windows)
            {
                // 剔除跨作者通用的模型腔（签名词本身不剔——那是风格）
                var report = StyleAudit.Analyze(string.Join("\n", w));
                if (report.Hits.Any(h => PathologicalKinds.Contains(h.Kind))) continue;

                var dist = Math.Abs(SignatureDensity(w) - meanSig);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = new AuthorStyleProfile.StyleSample(scene, string.Join("\n", w));
                }
            }
            if (best != null) picked.Add(best);
        }
        return picked;
    }

    /// <summary>样本总数上限（token 预算：锚定段整体控制在 ~1600 token 内）。</summary>
    internal const int MaxSamples = 6;

    /// <summary>窗口入桶。太短的窗口（&lt;120 字）统计不稳，不入。</summary>
    private static void Flush(
        List<string> window, int chars, string scene,
        Dictionary<string, List<List<string>>> buckets)
    {
        if (chars < 120) return;
        if (!buckets.TryGetValue(scene, out var list)) buckets[scene] = list = new();
        list.Add(window);
    }

    /// <summary>按段判场景。阈值针对"一段"而非窗口——对话段引号占大半，一眼可辨。</summary>
    private static string SceneOf(string paragraph)
    {
        var chars = Math.Max(1, VisibleChars(paragraph));
        var dialogue = CountDialogueChars(paragraph);
        var dRatio = (double)dialogue / chars;
        var action = ActionVerbs.Sum(v => CountOf(paragraph, v)) * 1000.0 / chars;

        if (dRatio >= 0.30) return "对话交锋";
        if (action >= 4.0) return "战斗冲突";
        if (dRatio < 0.08 && action < 2.0) return "环境氛围";
        return "日常推进";
    }

    /// <summary>段落是否几乎全是引号内容（引号外可见字 &lt; 15）。</summary>
    private static bool IsQuoteOnly(string p)
    {
        if (VisibleChars(p) == 0) return false;
        var outside = Regex.Replace(p, "[「『“‘][^」』”’]*[」』”’]", " ");
        return VisibleChars(outside) < 15;
    }

    /// <summary>位置前后 12 字内出现引号 = 疑似对话引导语。</summary>
    private static bool QuoteNearby(string text, int index, int length)
    {
        var start = Math.Max(0, index - 12);
        var end = Math.Min(text.Length, index + length + 12);
        return text.IndexOfAny(new[] { '“', '”', '「', '」' }, start, end - start) >= 0;
    }

    private static double MeanSignatureDensity(string allText, int visible)
    {
        if (visible == 0) return 0;
        return CandidateSignatureWords.Sum(w => CountOf(allText, w)) * 1000.0 / visible;
    }

    private static double SignatureDensity(List<string> window)
    {
        var text = string.Join("", window);
        var chars = VisibleChars(text);
        if (chars == 0) return 0;
        return CandidateSignatureWords.Sum(w => CountOf(text, w)) * 1000.0 / chars;
    }

    // ------------------------------------------------------------------
    //  基础统计
    // ------------------------------------------------------------------

    private static void CollectSentences(string chapter, List<int> into)
    {
        foreach (var s in Regex.Split(chapter, @"[。！？…；]+"))
        {
            var n = VisibleChars(s);
            if (n is >= 4 and <= 90) into.Add(n);
        }
    }

    /// <summary>可见字数（字母数字汉字）。给 MCP 工具做语料量校验用。</summary>
    public static int VisibleChars(string s)
    {
        var n = 0;
        foreach (var c in s) if (char.IsLetterOrDigit(c)) n++;
        return n;
    }

    private static int CountDialogueChars(string text)
    {
        var n = 0;
        foreach (Match m in Regex.Matches(text, "[“「][^”」]*[”」]"))
            n += VisibleChars(m.Value);
        return n;
    }

    private static int CountOf(string text, string word)
    {
        var n = 0;
        var i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0) { n++; i += word.Length; }
        return n;
    }

    private static double Median(List<int> xs)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    private static double Quantile(List<int> xs, double q)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        var idx = (int)Math.Clamp(q * (sorted.Count - 1), 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static double Stdev(List<int> xs)
    {
        if (xs.Count < 2) return 0;
        var mean = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / xs.Count);
    }

    internal static string ToId(string name)
    {
        var id = Regex.Replace(name.Trim(), @"\s+", "-");
        id = Regex.Replace(id, @"[^\w\-一-龥]", "");
        return string.IsNullOrEmpty(id) ? "author" : id[..Math.Min(id.Length, 40)];
    }
}
