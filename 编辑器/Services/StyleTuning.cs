using System.IO;
using System.Text.Json;

namespace 编辑器.Services;

/// <summary>
/// 单个作者档案的「调参」——覆盖自动推导出来的开关与提示词措辞。
///
/// 为什么要这一层：锚定块里的规则是从语料统计推出来的（见 <see cref="AuthorStyleProfile"/>
/// 的 QuietLongRatio 等字段），但统计只能近似人的意图。使用者要能说
/// 「这位作者就是要留白」「这句系统措辞读着别扭，换掉」。
///
/// 与 rules.json 的分工：
///   - rules（作者守则）：**内容要求**，写进锚定块的【作者守则】，优先级最高。
///   - tuning（本类）：**机制参数**，改的是「说不说这条规则」和「这条规则怎么措辞」。
///
/// 优先级：Overrides > 结构化开关 > 档案统计（自动） > 代码默认。
/// </summary>
public sealed class StyleTuning
{
    /// <summary>与主档案同目录，<c>{id}.tuning.json</c>。重新 import 不会覆盖。</summary>
    public const string FileSuffix = ".tuning.json";

    // ── 默认阈值：只在这里定义一次，BuildAnchorBlock 与 PickSamples 都读它 ──
    public const double DefaultQuietLongMin = 0.45;    // 长段无引号占比 ≥ 此值才教「锚」
    public const double DefaultQuietDominant = 0.75;   // ≥ 此值才要求「每章一处空转」
    public const double DefaultLongParaMin = 0.08;     // 长段占比 ≥ 此值才认为「他有长段」
    // ★ 0.25 → 0.18（2026-11-24）：三位作者的实测是 余华 32.5%、杀虫 19.9%、土豆 8.3%。
    //   0.25 把杀虫（移动端网文，五分之一是单句段）也挡在外面——而单句成段正是
    //   手机阅读最关键的版面特征，挡掉等于让网文档拿不到这条指令。
    public const double DefaultShortParaMin = 0.18;    // 短段占比 ≥ 此值才给单句成段量化

    /// <summary>
    /// 留白档位：auto（按档案统计）| off（不提留白）| anchor（只给锚点）| force（每章强制一处）。
    /// null 等同 auto。
    /// </summary>
    public string? QuietMode { get; set; }

    /// <summary>单句成段量化：auto（按统计）| off（只说「该短就短」）| on（给百分比）。</summary>
    public string? ShortParaMode { get; set; }

    public double? QuietLongMin { get; set; }
    public double? QuietDominant { get; set; }
    public double? LongParaMin { get; set; }
    public double? ShortParaMin { get; set; }

    /// <summary>
    /// 单条覆盖：key → 文本。空字符串 = **删除这一条**（不是恢复默认）。
    /// key 见 <see cref="StylePromptKeys"/>。
    /// </summary>
    public Dictionary<string, string>? Overrides { get; set; }

    /// <summary>追加在锚定块末尾的自定义指令（低风险，直接生效）。</summary>
    public List<string>? Extra { get; set; }

    public double QuietLongMinOr() => QuietLongMin ?? DefaultQuietLongMin;
    public double QuietDominantOr() => QuietDominant ?? DefaultQuietDominant;
    public double LongParaMinOr() => LongParaMin ?? DefaultLongParaMin;
    public double ShortParaMinOr() => ShortParaMin ?? DefaultShortParaMin;

    /// <summary>归一化档位；未知值当 auto（别因为拼错就静默换行为）。</summary>
    public string QuietModeOr()
    {
        var v = (QuietMode ?? "").Trim().ToLowerInvariant();
        return v is "off" or "anchor" or "force" ? v : "auto";
    }

    public string ShortParaModeOr()
    {
        var v = (ShortParaMode ?? "").Trim().ToLowerInvariant();
        return v is "off" or "on" ? v : "auto";
    }

    /// <summary>
    /// 留白档位按本档案统计推导，再被手动设置覆盖。
    /// 返回 none（不提留白）/ anchor（给锚点）/ force（每章强制）。
    /// </summary>
    public string ResolveQuiet(AuthorStyleProfile p) => ResolveQuiet(p.QuietLongRatio, p.LongParagraphRatio);

    /// <summary>
    /// 同上，直接吃两个统计量——Build 阶段（还没组装出 profile）与样本池选样都走这个重载。
    /// </summary>
    public string ResolveQuiet(double quietLong, double longPara)
    {
        var mode = QuietModeOr();
        if (mode == "off") return "none";
        if (mode is "anchor" or "force") return mode;
        // auto
        if (quietLong < QuietLongMinOr() || longPara < LongParaMinOr()) return "none";
        return quietLong >= QuietDominantOr() ? "force" : "anchor";
    }

    /// <summary>单句成段量化是否输出。</summary>
    public bool ResolveShortPara(AuthorStyleProfile p) => ResolveShortPara(p.ShortParagraphRatio);

    public bool ResolveShortPara(double shortPara) => ShortParaModeOr() switch
    {
        "off" => false,
        "on" => true,
        _ => shortPara >= ShortParaMinOr(),
    };

    /// <summary>
    /// 取某个 key 的最终文本：覆盖优先（空串返回 null 表示「删掉这条」）。
    /// </summary>
    public string? Resolve(string key, string fallback)
    {
        if (Overrides != null && Overrides.TryGetValue(key, out var v))
            return string.IsNullOrWhiteSpace(v) ? null : v;
        return fallback;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        QuietMode == null && ShortParaMode == null &&
        QuietLongMin == null && QuietDominant == null &&
        LongParaMin == null && ShortParaMin == null &&
        (Overrides == null || Overrides.Count == 0) &&
        (Extra == null || Extra.Count == 0);

    /// <summary>人读的变更摘要——高风险确认时要展示给用户看的就是这段。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (QuietMode != null) parts.Add($"留白={QuietMode}");
        if (ShortParaMode != null) parts.Add($"短段量化={ShortParaMode}");
        if (QuietLongMin != null) parts.Add($"留白阈值={QuietLongMin:0.00}");
        if (QuietDominant != null) parts.Add($"强制空转阈值={QuietDominant:0.00}");
        if (LongParaMin != null) parts.Add($"长段阈值={LongParaMin:0.00}");
        if (ShortParaMin != null) parts.Add($"短段阈值={ShortParaMin:0.00}");
        if (Overrides is { Count: > 0 })
            parts.Add("覆盖 " + string.Join("、", Overrides.Keys.Take(6)) +
                      (Overrides.Count > 6 ? $" 等 {Overrides.Count} 条" : ""));
        if (Extra is { Count: > 0 }) parts.Add($"追加指令 {Extra.Count} 条");
        return parts.Count == 0 ? "（无调参）" : string.Join("；", parts);
    }
}

/// <summary>
/// 锚定块里每条规则的键——用来单条覆盖。
/// 值即「默认文本」，<c>style_profile_tuning action=keys</c> 会连默认文本一起列出来。
/// </summary>
public static class StylePromptKeys
{
    public const string Intro = "intro";                       // 开头三行总述
    public const string SentenceShort = "sentence.short";       // 句长跟着场景走
    public const string SentenceMedian = "sentence.median";     // 中位句长/CV
    public const string SentenceComma = "sentence.comma";       // 逗号数
    public const string SentenceShortRatio = "sentence.shortRatio"; // 8 字短句占比
    public const string SentenceCounter = "sentence.counter";   // 反例
    public const string ParaCenter = "para.center";             // 段长中心
    public const string ParaAnchor = "para.anchor";             // 长段要有锚
    public const string ParaQuietQuota = "para.quietQuota";     // 这种段落的配额
    public const string ParaQuietForce = "para.quietForce";     // 每章强制一处空转
    public const string ParaQuietSoft = "para.quietSoft";       // 留白不必每章都有
    public const string ParaLongLoose = "para.longLoose";       // 长段按需（不教留白那档）
    public const string ParaShort = "para.short";               // 单句成段量化
    public const string ParaBreak = "para.break";               // 断段规则
    public const string ParaNoCounting = "para.noCounting";     // 不要数着写
    public const string DialogueMain = "dialogue.main";         // 对话是主要推进手段
    public const string DialogueRun = "dialogue.run";           // 连续对话长度
    public const string DialogueTag = "dialogue.tag";           // 引导语
    public const string DialogueNoPadding = "dialogue.noPadding"; // 不要没话找话
    public const string WordBudget = "word.budget";             // 风格词预算
    public const string WordRepeat = "word.repeat";             // 同词不连段
    public const string Closing = "closing";                    // 结尾声明

    public static readonly string[] All =
    {
        Intro, SentenceShort, SentenceMedian, SentenceComma, SentenceShortRatio, SentenceCounter,
        ParaCenter, ParaAnchor, ParaQuietQuota, ParaQuietForce, ParaQuietSoft, ParaLongLoose,
        ParaShort, ParaBreak, ParaNoCounting,
        DialogueMain, DialogueRun, DialogueTag, DialogueNoPadding,
        WordBudget, WordRepeat, Closing,
    };
}

/// <summary>高风险变更的待确认记录（{id}.pending.json）。</summary>
public sealed class StylePendingChange
{
    public string Code { get; set; } = "";
    public string Created { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>完整的待落盘调参——确认时整体替换，避免二次调用再造一份。</summary>
    public StyleTuning Tuning { get; set; } = new();
    public List<string> Changes { get; set; } = new();
}
