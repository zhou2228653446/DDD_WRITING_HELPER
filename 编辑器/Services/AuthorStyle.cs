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

    /// <summary>
    /// 引导语总频次（每千字处数）。旧档案没有这个字段，反序列化得 0，
    /// 锚定块会省略频次约束、只留词表与反堆砌。</summary>
    public double DialogueTagPerK { get; init; }

    // ── L2 软指纹 ──

    /// <summary>
    /// few-shot 锚定**样本池**。构建时每类场景留 Top-N（默认 8），总计 ≤48 段、每段 120~350 字；
    /// 生成时按当前场景 + 章号只取 2~3 段放进**不可缓存段**（见
    /// <see cref="AuthorStyleBuilder.SelectSamples"/>）。旧档案只有 6 段也能照常读取。
    /// </summary>
    public IReadOnlyList<StyleSample> Samples { get; init; } = Array.Empty<StyleSample>();

    // ── L3 规则 ──

    /// <summary>作者守则（LLM 归纳 + 人工确认）。空 = 尚未提炼。</summary>
    public IReadOnlyList<string> Rules { get; init; } = Array.Empty<string>();

    public sealed record SignatureWord(string Word, double PerK);

    /// <summary>
    /// 一条锚定样本。前两个位置参数保持与旧档案 JSON 兼容（旧档只有 Scene/Text），
    /// 其余字段都有默认值，反序列化旧档不会炸。
    /// </summary>
    public sealed record StyleSample(string Scene, string Text)
    {
        /// <summary>池内稳定编号（s01…），供 style_profile_samples 禁用/启用。</summary>
        public string Id { get; init; } = "";

        /// <summary>出处（第 N 章 / 用户定稿），便于人工抽查与按作品分组。</summary>
        public string Source { get; init; } = "";

        /// <summary>「这段示范了什么」，由该段指纹自动生成，注入时一并给模型。</summary>
        public string Note { get; init; } = "";

        /// <summary>与全书指纹的贴合度（0~1，越大越典型）。</summary>
        public double Fit { get; init; }

        /// <summary>人工禁用后不参与选样；旧档默认 true。</summary>
        public bool Enabled { get; init; } = true;
    }
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

    /// <summary>Id 只允许字母数字连字符下划线与中文——防路径穿越。</summary>
    private static bool ValidId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 60 && Regex.IsMatch(id, @"^[\w\-一-龥]+$");

    private static string? SafePath(string id) => ValidId(id) ? Path.Combine(Directory, id + ".json") : null;

    /// <summary>守则独立文件名。与主档案分开，重算档案不会冲掉人工确认过的守则。</summary>
    private static string RulesFile(string id) => Path.Combine(Directory, id + ".rules.json");

    /// <summary>读取守则；文件不存在返回空表（旧档则由 Get 自动迁移）。</summary>
    public static IReadOnlyList<string> LoadRules(string id)
    {
        if (!ValidId(id)) return Array.Empty<string>();
        var file = RulesFile(id);
        if (!File.Exists(file)) return Array.Empty<string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(file), JsonOpts)
                   ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>写守则（唯一真相）。传空表 = 清空。</summary>
    public static void SaveRules(string id, IEnumerable<string> rules)
    {
        if (!ValidId(id)) throw new ArgumentException($"非法档案 Id：{id}");
        System.IO.Directory.CreateDirectory(Directory);
        var lines = rules.Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim()).ToArray();
        File.WriteAllText(RulesFile(id),
            System.Text.Json.JsonSerializer.Serialize(lines, JsonOpts));
    }

    /// <summary>全部档案（按创建时间降序）。目录不存在返回空表。</summary>
    public static List<AuthorStyleProfile> List()
    {
        var result = new List<AuthorStyleProfile>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            if (file.EndsWith(".rules.json", StringComparison.Ordinal)) continue;
            // 走 Get 才能合并独立守则 + 顺带迁移旧档
            var p = Get(Path.GetFileNameWithoutExtension(file));
            if (p != null) result.Add(p);
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
            var p = System.Text.Json.JsonSerializer.Deserialize<AuthorStyleProfile>(
                File.ReadAllText(file), JsonOpts);
            if (p == null || string.IsNullOrWhiteSpace(p.Id)) return null;

            var rules = LoadRules(p.Id);
            if (rules.Count > 0) return p with { Rules = rules };
            // 旧档案的 Rules 还嵌在主 JSON 里：首次读取时迁移到独立文件，
            // 之后 style_profile_import 重算档案就不会把它们冲掉。
            if (p.Rules.Count > 0) SaveRules(p.Id, p.Rules);
            return p;
        }
        catch { return null; }
    }

    public static void Save(AuthorStyleProfile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = SafePath(profile.Id)
                   ?? throw new ArgumentException($"非法档案 Id：{profile.Id}");
        // 守则只存 rules.json；主档案留空，避免出现两份真相。
        if (profile.Rules.Count > 0) SaveRules(profile.Id, profile.Rules);
        File.WriteAllText(file,
            System.Text.Json.JsonSerializer.Serialize(
                profile with { Rules = Array.Empty<string>() }, JsonOpts));
    }

    public static bool Delete(string id)
    {
        var file = SafePath(id);
        if (file == null || !File.Exists(file)) return false;
        File.Delete(file);
        var rf = RulesFile(id);
        if (File.Exists(rf)) File.Delete(rf);
        return true;
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

    /// <summary>
    /// 候选池里**不该当"风格"教给模型**的词：通用叙述词与题材词。
    ///
    /// 判据不是"看起来普通"，而是实测：这些词一旦写进锚定块，生成就会爆。
    /// 《规则怪谈》实测「看着」2.96/千字（原书 0.49，6 倍）、「声音」2.66（原书 0.46），
    /// 而同一张表里 6 个真正的风格词（微微/缓缓/瞬间/忽然/眼神/摇头）**一次都没出现**——
    /// 因为通用词在叙述里随处可塞，风格词要刻意才写得出来，模型只做省力的那半。
    /// 更麻烦的是通用词跨题材穿帮：写都市却按余华档案挂「田里」「村口」。
    ///
    /// 所以：只有风格标记进提示词；这两类词照样参与统计（档案是事实），
    /// 但判定侧只对"超量堆砌"报警，不当贴合度算。
    /// </summary>
    private static readonly HashSet<string> NonStyleWords = new(StringComparer.Ordinal)
    {
        // 通用叙述：任何题材、任何作者的叙事都会自然产生，不需要教
        "看着", "笑着", "声音", "身体", "一声", "点头", "摇头", "低头", "抬头",
        "坐下", "蹲下", "半天", "说完", "眯眼", "轻轻",
        // 题材/地点：由故事发生在哪里决定，跨题材提示会穿帮
        "屋里", "日子", "街道", "田里", "夜里", "早上", "中午", "村口",
    };

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
        var daoRaw = Regex.Matches(body, @"([\u4e00-\u9fa5]{1,3}道)[，：]")
            // ★ 引号邻近校验：真引导语必然紧挨引号（说话人刚开口/刚说完）。
            //   没有这条，「一条街道，」这类"X道，"的巧合同现会混进表里
            //   （余华语料实测穿帮）。
            .Where(m => QuoteNearby(body, m.Index, m.Length))
            .Select(m => m.Groups[1].Value)
            .Where(w => !notTagExact.Contains(w) && !notTagSuffix.Any(w.EndsWith))
            .ToList();

        // ★ 原先只收「X道」，把「他说/我问」整类漏掉了：余华语料「他说」0.82/千字，
        //   是「说道」0.09 的 9 倍，一个都没统计到——于是每个作者的引导语表都被
        //   「道」系垄断，锚定块只会教模型用「说道」（实测生成里 6 篇全是「说道」）。
        //   补上后归一成「说」「问」。不会与「道」系重复命中：`说道，` 的「说」后面
        //   跟的是「道」而不是逗号，`说[，：]` 匹配不到它。
        var notSayExact = new HashSet<string>(StringComparer.Ordinal)
        {
            "就是说", "也就是说", "比如说", "虽说", "再说", "听说", "要说", "只是说",
            "应该说", "可以说", "或者说", "这么说", "那么说", "怎么说", "别说", "不如说",
            "换句话说", "总之说", "简单说", "老实说", "实话说", "说实话", "据说", "按说",
            "照说", "论说", "且说", "却说",
        };
        var sayRaw = Regex.Matches(body, @"([\u4e00-\u9fa5]{0,3}(?:说|问))[，：]")
            .Where(m => QuoteNearby(body, m.Index, m.Length))
            .Select(m => m.Groups[1].Value)
            .Where(w => !notSayExact.Contains(w))
            .ToList();

        // ★ 贪婪捕获会把主语一起吞进来：「齐夏说道」→ 引导语表第一位是角色名，
        //   写进锚定块等于让模型拿别人的主角名当模板（《十日终焉》实测穿帮）。
        var tags = daoRaw.Select(NormalizeTag).Where(w => w != null).Select(w => w!)
            .Concat(sayRaw.Select(w => w.EndsWith("说", StringComparison.Ordinal) ? "说" : "问"))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => g.Key)
            .ToList();

        // 引导语总频次：只用于给锚定块一个"每千字几处"的预算。
        // 分母必须用 body 的可见字——超长语料截了尾部，拿全量算会系统性偏低。
        var bodyVisible = VisibleChars(body);
        var tagPerK = bodyVisible == 0 ? 0 : (daoRaw.Count + sayRaw.Count) * 1000.0 / bodyVisible;

        // ── L2 样本池 ──
        // 把全书 L1 指纹当"典型"的靶子传进去：挑样不再只看签名词密度，
        // 句长/标点/对话/段长都要靠近全书均值，才配当 few-shot 锚点。
        var samples = PickSamples(chapters, allText, visible,
            Median(sentenceLens),
            sentenceTotal == 0 ? 0 : (double)commaTotal / sentenceTotal,
            dialogueRatio,
            Median(paraLens));

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
            DialogueTagPerK = Math.Round(tagPerK, 2),
            Samples = samples,
        };
    }

    /// <summary>
    /// 作者相似度评分（v5 新增的"打分"职能——对**具体档案**比，不对抽象标准比）。
    ///
    /// 与已退役的"AI 味指数"的本质区别：AI 味试图量一个隐形目标（"AI 味"），
    /// 阈值全靠猜，v4 实测 AI 与人写都在 91~99、毫无区分度；相似度量的
    /// 是与一份具体语料分布的距离——每个判据都有实数可对。
    /// </summary>
    public sealed record SimilarityReport(int Score, string Detail)
    {
        public override string ToString() => $"作者相似度 {Score}/100\n{Detail}";
    }

    public static SimilarityReport ScoreSimilarity(AuthorStyleProfile profile, string text)
    {
        var seg = StyleAudit.Split(text);
        var visible = seg.VisibleChars;
        if (visible < 100)
            return new SimilarityReport(0, "样本不足 100 字，无法算相似度。");

        // 句长
        var mean = seg.SentenceLengths.Count > 0 ? seg.SentenceLengths.Average() : 0;
        var medSent = Median(seg.SentenceLengths);

        // 每句逗号
        var commaTotal = text.Count(c => c == '，');
        var sentenceTotal = 0;
        foreach (var sen in Regex.Split(text, @"[。！？…；”」]+"))
            if (VisibleChars(sen) >= 4) sentenceTotal++;
        var commaPer = sentenceTotal == 0 ? 0 : (double)commaTotal / sentenceTotal;

        var dialogue = StyleAudit.CountInteractiveDialogue(seg.Paragraphs);
        var dialogueRatio = visible == 0 ? 0 : (double)dialogue / visible;

        // 各成分健康度（1 = 与档案一致）：偏距离按对数尺，±50% 内基本不扣
        static double Health(double actual, double target, double tolerance)
        {
            if (target <= 0) return 1;
            var dist = Math.Abs(Math.Log(Math.Max(actual, target * 0.05) / target));
            return Math.Max(0, 1 - dist / tolerance);   // tolerance = ln(2.7) ≈ 1.0 时偏离 e 倍归零
        }

        var hSentence = Health(medSent, profile.SentenceMedian, 1.0);
        var hComma = Health(commaPer, profile.CommaPerSentence, 1.1);
        var hPara = Health(Median(seg.ParagraphLengths), profile.ParagraphMedian, 1.1);
        var hDialogue = profile.DialogueRatio < 0.02
            ? 1.0
            : Health(dialogueRatio, profile.DialogueRatio, 1.2);

        // 签名词：逐词比对实际频次与目标频次（没出现=0.1，不是"没扣分"——
        // 签名词是这位作者的呼吸，一篇里一次都没有就不像他）。
        // ★ 贴合度只算风格词：通用叙述词不可控（不提也会自然出现，提了就爆），
        //   算进平均分会让分数被"看着/声音"绑架。通用词只在超量时单独报警。
        var sigDetail = new List<string>();
        var piling = new List<string>();
        var scoredWords = profile.SignatureWords.Where(w => !NonStyleWords.Contains(w.Word)).ToList();
        if (scoredWords.Count == 0) scoredWords = profile.SignatureWords.ToList();   // 全是通用词时退回全量
        var sigHealth = 0.0;
        if (scoredWords.Count > 0)
        {
            foreach (var w in scoredWords)
            {
                var actual = CountOf(text, w.Word) * 1000.0 / visible;
                var h = actual <= 0 ? 0.1 : Health(actual, w.PerK, 1.3);
                sigHealth += h;
                if (h < 0.5)
                    sigDetail.Add($"  {w.Word}：目标 {w.PerK:0.00}/千字，实际 {actual:0.00}");
            }
            sigHealth /= scoredWords.Count;
        }
        else sigHealth = 1;

        // 堆砌：单个词超目标 3 倍要单列。平均分会被"没出现的词"稀释，看不出来是谁在爆
        // （实测「看着」2.96 对目标 0.49，六个风格词归零，总分只表现为"贴合度偏低"）。
        foreach (var w in profile.SignatureWords)
        {
            if (w.PerK <= 0) continue;
            var actual = CountOf(text, w.Word) * 1000.0 / visible;
            if (actual > w.PerK * 3 && actual > 0.6)
                piling.Add($"「{w.Word}」{actual:0.00}/千字（目标 {w.PerK:0.00}，{actual / w.PerK:0.0} 倍）");
        }

        // 引导语：文中「X道，」「他说，」命中档案词表的比例（无对话给中性分）
        var tagHealth = 1.0;
        var tagNote = "";
        if (profile.DialogueTags.Count > 0 && text.Contains('“'))
        {
            var matches = Regex.Matches(text, @"([一-龥]{0,3}(?:道|说|问))[，：]")
                .Select(m => m.Groups[1].Value)
                .Select(raw => raw.EndsWith("道", StringComparison.Ordinal)
                    ? NormalizeTag(raw)
                    : raw.EndsWith("说", StringComparison.Ordinal) ? "说" : "问")
                .Where(t => t != null)
                .Select(t => t!)
                .ToList();
            if (matches.Count > 0)
            {
                var hit = matches.Count(m => profile.DialogueTags.Any(t => t == m || m.EndsWith(t)));
                tagHealth = (double)hit / matches.Count;
                tagNote = $"（引导语 {hit}/{matches.Count} 命中档案词表）";
            }
            else tagNote = "（文中没有对话引导语，档案惯例是「" +
                           string.Join("、", profile.DialogueTags.Take(3)) + "」）";
        }

        // 病态证据：跨作者通病，出现即扣（这类与"像谁"无关，就是病）
        var pathological = StyleAudit.Analyze(text).Hits
            .Where(h => h.Kind is "翻译腔" or "升华" or "僵笑弧度" or "冗余代词")
            .ToList();

        var score = Math.Clamp((int)Math.Round(
            20 * hSentence + 15 * hComma + 10 * hPara + 15 * hDialogue
            + 25 * sigHealth + 15 * tagHealth
            - 4 * pathological.Count), 0, 100);

        var sb = new StringBuilder();
        sb.AppendLine($"对照「{profile.Name}」：中位句长 {medSent:0}（目标 {profile.SentenceMedian:0}）、" +
                      $"每句逗号 {commaPer:0.0}（目标 {profile.CommaPerSentence:0.0}）、" +
                      $"中位段长 {Median(seg.ParagraphLengths):0}（目标 {profile.ParagraphMedian:0}）、" +
                      $"对话 {dialogueRatio:P0}（目标 {profile.DialogueRatio:P0}）");
        sb.AppendLine($"· 签名词贴合度 {sigHealth:P0}{(sigDetail.Count > 0 ? "\n" + string.Join("\n", sigDetail.Take(4)) : "")}");
        if (piling.Count > 0)
            sb.AppendLine($"· ⚠ 单词堆砌：{string.Join("；", piling)}——同一个词反复出现，比少用更显机器");
        sb.AppendLine($"· 引导语贴合度 {tagHealth:P0}{tagNote}");
        if (pathological.Count > 0)
            sb.Append($"· 病态扣分：{string.Join("、", pathological.Select(h => h.Kind))}（-{4 * pathological.Count}）");
        else sb.Append("· 病态扣分：无");

        return new SimilarityReport(score, sb.ToString());
    }

    /// <summary>
    /// 生成**稳定前缀**（进可缓存 system 段）：指纹的可执行区间 + 守则，约 350~450 token。
    /// 原文样本改由 <see cref="BuildSampleBlock"/> 按场景动态注入不可缓存段——
    /// 前缀不随章节变化，缓存才能一直命中。
    ///
    /// 2026-11-10 方案 B：旧版是「数字 + 定性口号」，且自相矛盾——余华档案每句
    /// 1.8 个逗号却写「别把句子用逗号连成一长串」，7% 短句却写「短句剁节奏」。
    /// 模型只抓口号：固定前情对照实测六篇全部写成 19~21 字 / 1.37~1.39 逗号 /
    /// 对话 3~5%，系统性偏离目标 27 字 / 1.82 / 14%。现在改成「区间 + 边界 + 反例」，
    /// 且**所有数字从档案推导**，多作者各自出数，不硬编码任何作者的统计量。
    /// </summary>
    public static string BuildAnchorBlock(AuthorStyleProfile p, bool sceneBound = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【作者文风锚定 —— 目标作者：{p.Name}】");
        sb.AppendLine("以下区间统计自该作者的真实作品。题材、人物、情节一律以本书设定为准；");
        sb.AppendLine("只让句子、段落、用词与对话的节奏向这些区间靠拢。");

        // ── 句子：主区间要让实测偏差落在区间外，才有纠偏作用 ──
        var m = p.SentenceMedian;
        var comma = p.CommaPerSentence;
        sb.AppendLine();
        sb.AppendLine("【句子】");
        if (sceneBound)
        {
            // 实验分支（ai_write anchor=scene）：不把离散度当指标，改成「长度跟场景走」。
            // 理由：直接要求"句长有变化"会换来机械顿挫（长-短-长-短），
            // 而人的长短起伏是内容驱动的，所以只绑定场景，不给 CV 数字。
            sb.AppendLine($"· 句长跟着场景走，不要全章一个节奏：动作、冲突、视线切换处用短句" +
                          $"（{N(m * 0.3)}~{N(m * 0.55)} 字，允许一句一段）；交代往事、说明规则、" +
                          $"铺陈环境时用长句（{N(m * 1.3)}~{N(m * 1.9)} 字，带 3~5 个逗号），" +
                          "可以连着两三句长句。");
            sb.AppendLine($"· 全章中位句长落在 {N(m * 0.9)}~{N(m * 1.1)} 字即可，不要求每段都靠近这个数。");
            sb.AppendLine($"· 8 字以内的短句全章约占 {p.ShortSentenceRatio:P0}，出现在动作与对话的间隙；" +
                          "不要为了制造节奏在叙述中间硬插短句。");
            sb.AppendLine("  禁止项：「他停住。风很大。灯灭了。」——为顿挫而顿挫的三连短句，比通篇长句更假。");
            sb.AppendLine($"· 每句平均 {comma:0.0} 个逗号（多数句子 " +
                          $"{Math.Max(0.5, comma * 0.85):0.0}~{comma * 1.1:0.0} 个）：长句靠逗号推进，" +
                          "短句不带逗号；不为凑数硬加。");
        }
        else
        {
            sb.AppendLine($"· 中位句长 {N(m)} 字（句长 CV {p.SentenceCv:0.00}）：多数句子落在 " +
                          $"{N(m * 0.75)}~{N(m * 1.35)} 字，长短要有起伏，不要通篇一个长度。");
            sb.AppendLine($"· 每句平均 {comma:0.0} 个逗号，多数句子 {Math.Max(0.5, comma * 0.85):0.0}~{comma * 1.1:0.0} 个；" +
                          $"每章要有几句 {N(m * 1.1)} 字以上、带 {N(comma)}~{N(comma) + 1} 个逗号的长句，" +
                          "也要有不带逗号的短句。不为凑数硬加逗号。");
            sb.AppendLine($"· 8 字以内的短句约占 {p.ShortSentenceRatio:P0}，" +
                          $"不超过 {ShortCap(p.ShortSentenceRatio):P0}，且不要连续三句短句。");
            sb.AppendLine(ShortCounterExample(p));
        }

        // ── 段落：档案已有四分位，直接当区间用 ──
        sb.AppendLine();
        sb.AppendLine("【段落】");
        // ★ 别直接把 P25~P75 当「多数段落」区间：余华是 12~85，跨度太宽，
        //   模型一律往上界靠（实测段长 41→72、段数 33→16），反而离中位 30 更远。
        //   区间围绕中位值收窄，并明确厚段配额与断段依据。
        sb.AppendLine($"· 段长以 {N(p.ParagraphMedian)} 字为中心：多数段落 " +
                      $"{N(p.ParagraphMedian * 0.7)}~{N(p.ParagraphMedian * 1.5)} 字，" +
                      $"全章段长的中位数要达到 {N(p.ParagraphMedian)} 字左右。");
        sb.AppendLine($"· 超过 {N(p.ParagraphMedian * 2)} 字的厚段全章不超过两三处；" +
                      "段落按动作、视线或对话的自然切换断开，不要为凑长度把几句并成一段。");

        // ── 对话：高对话作者必须写成"主要推进手段" ──
        //   实测（杀虫队队员 32%）：旧措辞写「至少安排一两处人物交谈」，
        //   生成只有 12.9%——一句下限把 32% 的比例废掉了，
        //   和旧版「短句剁节奏」压掉句长数字是同一个病。
        sb.AppendLine();
        sb.AppendLine("【对话】");
        if (p.DialogueRatio >= 0.25)
        {
            sb.AppendLine($"· 对话是主要推进手段：约占全篇 {p.DialogueRatio:P0}（每千字约 {p.DialogueRatio * 1000:0} 字），" +
                          "大致每三段就有一段是带引号的人物对话。规则、冲突、信息都让人物开口说出来，不要压成叙述概述。");
            sb.AppendLine("· 允许人物连着说话，不要刚说两句就切回环境描写；也不要整章只有叙述。");
        }
        else
        {
            sb.AppendLine($"· 带引号的对话约占全篇 {p.DialogueRatio:P0}（每千字约 {p.DialogueRatio * 1000:0} 字）；" +
                          "一章里至少安排一两处人物交谈。");
            sb.AppendLine($"· 连续对话以 {N(p.DialogueRunMedian)} 段为常见长度；不要整章只有叙述。");
        }
        // ★ 旧句「引导语用「说道、问道…」这类表达，不要用翻译腔的「他说/他说道」堆砌」
        //   自相矛盾：同一个词前半句让用、后半句让别用，模型只能挑一半执行。
        //   现在只给「用哪些 + 总共几处」，反堆砌靠频次约束，不靠口号。
        if (p.DialogueTags.Count > 0)
        {
            var tagLine = "· 对话引导语用「" + string.Join("、", p.DialogueTags.Take(5)
                .Select(t => t == "说" ? "他说/她说" : t == "问" ? "他问/她问" : t)) + "」这类说法";
            if (p.DialogueTagPerK > 0)
                tagLine += $"，每千字约 {p.DialogueTagPerK:0.0} 处（三千字一章约 {p.DialogueTagPerK * 3:0} 处）";
            sb.AppendLine(tagLine + "；其余对话段直接上引号，不必每句都标明谁在说，连续几句对话时干脆不写引导语。");
        }
        else sb.AppendLine("· 对话引导语用简短的说话动词，不必每句都标注。");
        sb.AppendLine("· 不要为凑比例让角色没话找话。");

        // ── 用词：总预算 + 配比，不给逐词区间 ──
        //   旧版「每词每千字约 lo~hi 次」把 1.32 的「点头」和 0.34 的「眼神」
        //   压成同一个区间，实测模型于是平均铺：两个通用词超 6 倍、六个风格词归零，
        //   总量 7.77/千字（原书 5.54，+40%）。改成「总共多少 + 谁挑大梁」。
        //   通用叙述词/题材词整段不提——实测提示了只会超（看着 ×6、声音 ×5.9）。
        var styleWords = p.SignatureWords.Where(w => !NonStyleWords.Contains(w.Word)).ToList();
        if (styleWords.Count > 0)
        {
            var budget = styleWords.Sum(w => w.PerK);
            var lead = styleWords.Take(5).ToList();
            sb.AppendLine();
            sb.AppendLine("【用词】");
            sb.AppendLine($"· 风格词合计每千字约 {budget:0.0} 次（三千字一章共约 {budget * 3:0} 处），分散开用：" +
                          string.Join("、", lead.Select(w => $"「{w.Word}」{w.PerK:0.0}")) +
                          "（每千字次数）" +
                          (styleWords.Count > lead.Count
                              ? $"；其余{string.Join("、", styleWords.Skip(5).Select(w => $"「{w.Word}」"))}零星点一两处即可。"
                              : "。"));
            // ★ 只写"宁少不多"实测会被理解成"可以完全不用"：5 个风格词里 4 个归零
            //   （忽然 1.19 是唯一的幸存者）。改成双向——上界防堆砌，下界防归零。
            sb.AppendLine("· 同一个词不在相邻两段重复，一段里不叠两个同义修饰；不为凑词硬塞，也别整章一个都不用。");
        }

        sb.AppendLine();
        sb.AppendLine("（以上是统计参照，不是逐句硬指标；读起来自然优先。下面的作者守则与原文示例只用于" +
                      "学句式接续、情绪处理与结构习惯；一旦与上述数字区间冲突，一律以区间为准。）");

        if (p.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【作者守则】");
            foreach (var r in p.Rules) sb.AppendLine($"- {r}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>区间取整（至少 1）——避免档案缺字段时出现「0 字」。</summary>
    private static int N(double v) => Math.Max(1, (int)Math.Round(v));

    /// <summary>短句占比上限：以档案值的两倍为界，夹在 12%~30%，防止反例和区间互相打架。</summary>
    private static double ShortCap(double r) => Math.Min(0.30, Math.Max(0.12, r * 2));

    /// <summary>
    /// 反例方向跟着档案走：短句作者防「通篇碎片」（旧口号把人带偏成 19 字句），
    /// 长句作者（中位 ≥35 字）防「每句一样长的匀速长句」。
    /// </summary>
    private static string ShortCounterExample(AuthorStyleProfile p) =>
        p.ShortSentenceRatio < 0.12 && p.SentenceMedian < 35
            ? "  反例：「他停住。风很大。灯灭了。」——这不是节奏，是通篇碎片。"
            : "  反例：每句都拉到同样长度的匀速长句。";

    /// <summary>把 agent/界面给的场景提示归一到 6 个桶名；认不出返回 null。</summary>
    public static string? NormalizeScene(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        var h = hint.Trim();
        foreach (var s in SceneOrder)
            if (h.Contains(s, StringComparison.Ordinal)) return s;

        static bool Has(string s, params string[] keys) =>
            keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (Has(h, "开篇", "起笔", "opening")) return "开篇起笔";
        if (Has(h, "章末", "收束", "结尾", "ending")) return "章末收束";
        if (Has(h, "战斗", "打斗", "大战", "冲突", "厮杀", "交手", "battle", "action")) return "战斗冲突";
        if (Has(h, "对话", "交谈", "争执", "dialogue")) return "对话交锋";
        if (Has(h, "环境", "氛围", "景物", "scene")) return "环境氛围";
        if (Has(h, "日常", "过渡", "推进")) return "日常推进";
        return null;
    }

    /// <summary>
    /// 猜本次写作的场景：显式提示 &gt; 章节标题 &gt; 指令 &gt; 正文末尾 600 字的段落投票。
    /// 纯本地、确定性，不调模型。猜不出返回 null（此时从整个池轮换）。
    /// </summary>
    public static string? GuessScene(string? title, string? hint, string? tail)
    {
        var byTitle = NormalizeScene(title);
        if (byTitle != null) return byTitle;
        var byHint = NormalizeScene(hint);
        if (byHint != null) return byHint;
        if (string.IsNullOrWhiteSpace(tail)) return null;

        var cut = tail.Length > 600 ? tail[^600..] : tail;
        var paras = cut.Split('\n').Where(p => VisibleChars(p) >= 8).TakeLast(6).ToList();
        if (paras.Count == 0) return null;
        return paras.Select(SceneOf)
            .GroupBy(x => x, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }

    /// <summary>
    /// 从样本池挑本次要注入的 2~3 段：先命中场景，再按**章号确定性轮换**。
    /// 同一章反复生成拿到同一组（章内连贯），换章自动换组（不会把同几段原文背下来）。
    /// 池不足时从其他场景补位；池为空返回空表（不注入，不报错）。
    /// </summary>
    public static List<AuthorStyleProfile.StyleSample> SelectSamples(
        AuthorStyleProfile p, string? scene, int chapterNumber, int count = SamplesPerRequest,
        bool spread = false)
    {
        var pool = p.Samples.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (pool.Count == 0 || count <= 0) return new();

        var want = NormalizeScene(scene);
        var seed = StableHash(chapterNumber);

        if (want == null)
        {
            // 没判出场景时按**场景分层**取样：池是按场景顺序连续排的，
            // 直接连续取 count 段会整组落在「开篇起笔」里（实测首章拿到
            // s02/s03/s04 三段开场文）。分层后每类一段，风格示范不偏科。
            var byScene = SceneOrder
                .Select(sc => Enumerable.Range(0, pool.Count)
                    .Where(i => pool[i].Scene == sc).ToList())
                .Where(g => g.Count > 0)
                .ToList();
            if (byScene.Count > 0)
            {
                var start = seed % byScene.Count;
                var mixed = new List<int>();
                for (var k = 0; k < count && k < byScene.Count; k++)
                {
                    var group = byScene[(start + k) % byScene.Count];
                    var chosen = group[(seed / byScene.Count + k) % group.Count];
                    if (!mixed.Contains(chosen)) mixed.Add(chosen);
                }
                return mixed.Select(i => pool[i]).ToList();
            }
        }

        var primary = new List<int>();
        var secondary = new List<int>();
        for (var i = 0; i < pool.Count; i++)
        {
            if (want == null || pool[i].Scene == want) primary.Add(i);
            else secondary.Add(i);
        }
        if (primary.Count == 0)
        {
            primary = secondary;
            secondary = new List<int>();
        }

        var picked = Rotate(primary, seed).Take(count).ToList();
        if (picked.Count < count)
            picked.AddRange(Rotate(secondary, seed).Take(count - picked.Count));

        // 实验分支（anchor=scene）：留一段场景命中，再补**长短两极**各一段。
        // 目的不是拉高 CV，而是让模型眼前同时有"短句密集"和"长句铺陈"两种样本，
        // 分布靠示例带过去，而不是靠一句"长短要有起伏"的指令。
        if (spread && pool.Count >= count)
        {
            var ordered = Enumerable.Range(0, pool.Count)
                .OrderBy(i => MedianSentenceLen(pool[i].Text))
                .ToList();
            var band = Math.Max(1, ordered.Count / 3);
            var wantLong = Rotate(ordered.Skip(ordered.Count - band).ToList(), seed).First();
            var wantShort = Rotate(ordered.Take(band).ToList(), seed / 7 + 3).First();

            var keep = picked.Take(Math.Max(0, count - 2)).ToList();
            foreach (var i in new[] { wantLong, wantShort })
                if (!keep.Contains(i)) keep.Add(i);
            foreach (var i in picked)
                if (keep.Count < count && !keep.Contains(i)) keep.Add(i);
            picked = keep.Take(count).ToList();
        }
        return picked.Select(i => pool[i]).ToList();
    }

    /// <summary>样本自身的句长中位（用于长短两极取样）。</summary>
    private static double MedianSentenceLen(string text)
    {
        var lens = text
            .Split(new[] { '。', '！', '？', '…', '；', '”', '」' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(VisibleChars)
            .Where(x => x >= 4)
            .OrderBy(x => x)
            .ToList();
        if (lens.Count == 0) return 0;
        return lens[lens.Count / 2];
    }

    private static List<int> Rotate(List<int> src, int seed)
    {
        if (src.Count == 0) return new List<int>();
        var start = seed % src.Count;
        return src.Skip(start).Concat(src.Take(start)).ToList();
    }

    private static int StableHash(int n)
    {
        unchecked
        {
            var x = (uint)n;
            x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
            return (int)(x & 0x7fffffff);
        }
    }

    /// <summary>动态样本块（进不可缓存段）。每段带场景、出处与「示范了什么」。</summary>
    public static string BuildSampleBlock(
        AuthorStyleProfile p, IReadOnlyList<AuthorStyleProfile.StyleSample> samples)
    {
        if (samples.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"【本次风格示例 —— {p.Name} 原文】");
        sb.AppendLine($"以下是与本次写作场景最接近的 {samples.Count} 段原文。模仿它的句式、节奏、对话接续与情绪处理；");
        sb.AppendLine("只学怎么写，不要抄情节、人名、专名与题材词。");
        sb.AppendLine("接着上文写时向示例的句法靠拢，但内容与人物一律以本书设定为准。");
        foreach (var s in samples)
        {
            sb.AppendLine();
            sb.AppendLine($"──{s.Scene}｜{s.Source}" +
                          (string.IsNullOrWhiteSpace(s.Note) ? "" : "｜" + s.Note) + "──");
            sb.AppendLine(s.Text);
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

    /// <summary>每类场景留几段。6 类 × 8 = 上限 48 段。</summary>
    internal const int SamplesPerScene = 8;

    /// <summary>样本池上限（构建期）。注入期每次只取 2~3 段，不占缓存前缀。</summary>
    internal const int MaxSamples = 48;

    /// <summary>每次请求注入几段。</summary>
    public const int SamplesPerRequest = 3;

    /// <summary>场景固定顺序——决定样本 Id 的稳定性。</summary>
    public static readonly string[] SceneOrder =
        { "开篇起笔", "对话交锋", "战斗冲突", "环境氛围", "日常推进", "章末收束" };

    private sealed record SampleWindow(List<string> Paragraphs, int Chars, string Source);

    /// <summary>
    /// 构建样本池：场景分桶 → 多维贴合度排序 → 病态过滤 + 去重 → 每场景 Top-N。
    /// 与旧版（每场景只取"签名词密度最接近均值"的一段）的三个差别：
    /// ① 留池不留单段；② 贴合度看句长/标点/对话/段长而不止词频；③ 同章、近似重复段只留一份，
    /// 免得扩到 48 段后全是同一个角色在同一章里打转。
    /// </summary>
    internal static List<AuthorStyleProfile.StyleSample> PickSamples(
        List<string> chapters, string allText, int visible,
        double targetSentence = 0, double targetComma = 0,
        double targetDialogue = 0, double targetPara = 0)
    {
        // 场景分桶：对话交锋 / 战斗冲突 / 环境氛围 / 日常推进。
        // ★ 先按段分类，再把**同场景连续段落**聚成 250~350 字的窗口——
        //   跨场景聚窗会把对话段和环境段搅在一起，哪种场景都判不准。
        var buckets = new Dictionary<string, List<SampleWindow>>();
        for (var ci = 0; ci < chapters.Count; ci++)
        {
            var ch = chapters[ci];
            var source = $"第{ci + 1}章";
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
                    Flush(window, winChars, windowScene, buckets, source);
                    window = new List<string>();
                    winChars = 0;
                }
                windowScene = scene;
                window.Add(p);
                winChars += VisibleChars(p);
                if (winChars >= 300)
                {
                    Flush(window, winChars, windowScene, buckets, source);
                    window = new List<string>();
                    winChars = 0;
                    windowScene = "";
                }
            }
            if (window.Count > 0) Flush(window, winChars, windowScene, buckets, source);
        }

        // 章首/章末：起笔与收束方式靠指令说不清，靠样本一学就会。
        // 只取足额章（≥600 字）的首尾窗，避开广告残章。
        for (var ci = 0; ci < chapters.Count; ci++)
        {
            var ch = chapters[ci];
            var source = $"第{ci + 1}章";
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
            Flush(opening, openChars, "开篇起笔", buckets, source + "·开头");

            var closing = new List<string>();
            var closeChars = 0;
            for (var i = paras.Count - 1; i >= 0 && closeChars < 250; i--)
            {
                closing.Insert(0, paras[i]);
                closeChars += VisibleChars(paras[i]);
            }
            Flush(closing, closeChars, "章末收束", buckets, source + "·结尾");
        }

        var meanSig = MeanSignatureDensity(allText, visible);
        var picked = new List<AuthorStyleProfile.StyleSample>();
        var seq = 1;
        foreach (var scene in SceneOrder)
        {
            if (!buckets.TryGetValue(scene, out var windows) || windows.Count == 0) continue;

            // ★ 贴合度排序很便宜、StyleAudit.Analyze 很贵：先排序取候选，
            //   再对候选跑病态检查。百万字语料下比"每个窗口都 Analyze 一遍"快一个量级。
            var ranked = windows
                .Select(w => (W: w, Fit: WindowFit(w, meanSig,
                    targetSentence, targetComma, targetDialogue, targetPara)))
                .OrderByDescending(x => x.Fit)
                .Take(SamplesPerScene * 8)
                .ToList();

            var grams = new List<HashSet<string>>();
            var perChapter = new Dictionary<string, int>(StringComparer.Ordinal);
            var taken = 0;
            foreach (var (w, fit) in ranked)
            {
                if (taken >= SamplesPerScene || picked.Count >= MaxSamples) break;
                var text = string.Join("\n", w.Paragraphs);

                // 剔除跨作者通用的模型腔（签名词本身不剔——那是风格）
                if (StyleAudit.Analyze(text).Hits.Any(h => PathologicalKinds.Contains(h.Kind))) continue;

                // 近似重复段（同一情节的两种切窗）只留一份
                var g = Bigrams(text);
                if (grams.Any(prev => Jaccard(prev, g) > 0.72)) continue;
                perChapter.TryGetValue(w.Source, out var used);
                if (used >= 2) continue;                       // 同一章最多 2 段
                perChapter[w.Source] = used + 1;

                grams.Add(g);
                picked.Add(new AuthorStyleProfile.StyleSample(scene, text)
                {
                    Id = $"s{seq++:00}",
                    Source = w.Source,
                    Note = DescribeSample(text),
                    Fit = Math.Round(fit, 3),
                    Enabled = true,
                });
                taken++;
            }
        }
        return picked;
    }

    /// <summary>窗口入桶。太短的窗口（&lt;120 字）统计不稳，不入。</summary>
    private static void Flush(
        List<string> window, int chars, string scene,
        Dictionary<string, List<SampleWindow>> buckets, string source)
    {
        if (chars < 120) return;
        if (!buckets.TryGetValue(scene, out var list)) buckets[scene] = list = new();
        list.Add(new SampleWindow(new List<string>(window), chars, source));
    }

    /// <summary>一段有多"典型的他"：五个指纹按健康度加权。目标全为 0 时退回只看签名词。</summary>
    private static double WindowFit(SampleWindow w, double meanSig,
        double tSent, double tComma, double tDlg, double tPara)
    {
        var text = string.Join("\n", w.Paragraphs);
        static double H(double a, double t, double tol)
        {
            if (t <= 0) return 1;
            return Math.Max(0, 1 - Math.Abs(Math.Log(Math.Max(a, t * 0.05) / t)) / tol);
        }

        var sigFit = H(SignatureDensity(w.Paragraphs), meanSig, 1.2);
        if (tSent <= 0) return sigFit;

        var lens = new List<int>();
        foreach (var sen in Regex.Split(text, @"[。！？…；”」]+"))
        {
            var n = VisibleChars(sen);
            if (n >= 4) lens.Add(n);
        }
        var commaPer = lens.Count == 0 ? 0 : (double)text.Count(c => c == '，') / lens.Count;
        var visible = Math.Max(1, VisibleChars(text));
        var dlg = (double)CountDialogueChars(text) / visible;
        var paras = w.Paragraphs.Select(VisibleChars).Where(n => n >= 4).ToList();

        return 0.30 * sigFit
             + 0.25 * H(Median(lens), tSent, 1.2)
             + 0.20 * H(commaPer, tComma, 1.2)
             + 0.15 * H(dlg, tDlg, 1.4)
             + 0.10 * H(Median(paras), tPara, 1.3);
    }

    private static HashSet<string> Bigrams(string text)
    {
        var flat = Regex.Replace(text, @"\s+", "");
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i++) set.Add(flat.Substring(i, 2));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(x => b.Contains(x));
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>把"这段示范了什么"写成一行，注入时让模仿从隐式变半显式。</summary>
    private static string DescribeSample(string text)
    {
        var lens = new List<int>();
        foreach (var sen in Regex.Split(text, @"[。！？…；”」]+"))
        {
            var n = VisibleChars(sen);
            if (n >= 4) lens.Add(n);
        }
        var visible = Math.Max(1, VisibleChars(text));
        var comma = lens.Count == 0 ? 0 : (double)text.Count(c => c == '，') / lens.Count;
        var dlg = (double)CountDialogueChars(text) / visible;
        var quotes = text.Split('\n').Count(IsQuoteOnly);
        return $"中位句长 {Median(lens):0} 字、每句 {comma:0.0} 逗号、对话 {dlg:P0}、连续引号段 {quotes}";
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

    /// <summary>
    /// 引导语规范化：从自由捕获的「X道」里取出**已知引导语**，取不到就丢弃。
    ///
    /// 为什么不用"剥人名"：正则只能看到「1~3 个汉字 + 道」，分不清
    /// 「齐夏说道」（专名）和「低声说道」（方式状语），剥的时候必然误伤。
    /// 改成白名单最长后缀匹配后，只保留确定是引导语的词——
    /// 召回率低一点无所谓（只要 top 8），但专名一个字都进不来。
    /// </summary>
    internal static string? NormalizeTag(string w)
    {
        if (string.IsNullOrEmpty(w) || !w.EndsWith("道", StringComparison.Ordinal)) return null;
        foreach (var t in TagLexicon)
            if (w.EndsWith(t, StringComparison.Ordinal)) return t;
        return null;
    }

    /// <summary>
    /// 引导语白名单，按长度降序（最长后缀优先：「低声说道」不能被拆成「说道」）。
    /// 新增语料若出现表外引导语，宁可丢弃也不要放开自由捕获。
    /// </summary>
    private static readonly string[] TagLexicon =
        new[]
        {
            // 方式状语 + 说话动词 + 道
            "低声说道", "沉声说道", "轻声说道", "冷冷说道", "笑着说道", "接口说道", "接着说道",
            "开口问道", "开口说道", "点头说道", "摇头说道", "低声问道", "沉声问道", "轻声问道",
            // 状语 + 道
            "淡淡地道", "缓缓地道", "冷冷地道", "轻轻地道", "慢慢地道", "重重地道",
            "淡淡道", "缓缓道", "冷冷道", "轻轻道", "慢慢道", "重重道",
            "轻声道", "低声道", "沉声道", "冷声道", "厉声道", "柔声道", "哑声道", "颤声道",
            "高声说道", "大声说道", "高声问道", "大声问道",
            // 表情 / 动作 + 道
            "冷笑道", "苦笑道", "怒道", "忙道", "急道", "顿道", "接口道", "接着道", "跟着道",
            "补充道", "解释道", "吩咐道", "嘟囔道", "咕哝道", "喃喃道", "答道", "应道", "回道",
            // 单字说话动词 + 道
            "说道", "问道", "叫道", "喊道", "喝道", "笑道", "骂道", "叹道",
            "嚷道", "吼道", "哼道", "念道", "讲道", "劝道", "催道",
        }
        .OrderByDescending(t => t.Length)
        .ToArray();

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
