using System.IO;
using System.Text;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Mcp;

/// <summary>
/// 作者风格档案工具：多作者库的导入 / 列表 / 按书绑定。
///
/// 接口设计（用户明确）：模仿对象不止天蚕土豆一人，不同书可以用不同作者的文风——
/// 所以档案按作者一人一份存全局库（AuthorStyleStore），书通过
/// NovelProject.AuthorStyleId 绑定；请求级临时覆盖走 ai_write 的 style 参数，
/// 三层各管各的，互不踩。
/// </summary>
public static class AuthorStyleTools
{
    /// <summary>列出全部档案（概览：签名词前 5 + 样本数）。</summary>
    internal static ToolResult List()
    {
        var profiles = AuthorStyleStore.List();
        if (profiles.Count == 0)
            return ToolResult.Ok("档案库为空。用 style_profile_import 从一位作者的作品 txt 导入第一份档案。");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"共 {profiles.Count} 份作者风格档案：");
        sb.AppendLine();
        foreach (var p in profiles)
        {
            sb.AppendLine($"· [{p.Id}] {p.Name}（{p.Source}）");
            sb.AppendLine($"  指纹：中位句长 {p.SentenceMedian:0} 字 / CV {p.SentenceCv:0.00}、" +
                          $"对话 {p.DialogueRatio:P0}、签名词 {p.SignatureWords.Count} 个、" +
                          $"样本池 {p.Samples.Count} 段（启用 {p.Samples.Count(x => x.Enabled)}、" +
                          $"每次按场景取 {AuthorStyleBuilder.SamplesPerRequest} 段）" +
                          (p.Rules.Count > 0 ? $"、守则 {p.Rules.Count} 条" : "（守则未提炼）"));
            if (p.SignatureWords.Count > 0)
                sb.AppendLine($"  签名词：{string.Join("、", p.SignatureWords.Take(5).Select(w => w.Word))}");
        }
        sb.AppendLine();
        sb.Append("绑定到当前书：style_profile_bind。临时覆盖一次请求：ai_write 的 style 参数。");
        return ToolResult.Ok(sb.ToString().Trim());
    }

    /// <summary>
    /// 从作品 txt 导入并构建档案。语料可以是一整部或多部合集
    /// （自动清洗广告页、按「第N章」分章——缩进与否都认）。
    /// </summary>
    internal static ToolResult Import(Session s, JsonElement args)
    {
        var name = NovelTools.Str(args, "name").Trim();
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 不能为空（作者笔名，如「天蚕土豆」）。");

        var path = NovelTools.Str(args, "path").Trim();
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path 不能为空（作品 txt 的完整路径）。");
        if (!File.Exists(path)) return ToolResult.Fail($"文件不存在：{path}");

        string text;
        try
        {
            using var reader = new StreamReader(path);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"读取失败：{ex.Message}");
        }
        if (AuthorStyleBuilder.VisibleChars(text) < 200_000)
            return ToolResult.Fail("语料太短（可见字数 <20 万），统计不出稳定的风格指纹。请提供至少一部长篇的完整文本。");

        AuthorStyleProfile profile;
        try
        {
            profile = AuthorStyleBuilder.Build(name, text, $"{Path.GetFileName(path)}（{AuthorStyleBuilder.VisibleChars(text) / 10000} 万字）");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        AuthorStyleStore.Save(profile);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"已构建并保存档案 [{profile.Id}] {profile.Name}（来自 {profile.Source}）");
        sb.AppendLine();
        sb.AppendLine($"· 指纹：中位句长 {profile.SentenceMedian:0} 字 / CV {profile.SentenceCv:0.00}；" +
                      $"中位段长 {profile.ParagraphMedian:0} 字（{profile.ParagraphP25:0}~{profile.ParagraphP75:0}）；对话占比 {profile.DialogueRatio:P0}");
        sb.AppendLine($"· 签名词：{string.Join("、", profile.SignatureWords.Select(w => $"{w.Word}({w.PerK:0.00})"))}");
        sb.AppendLine($"· 引导语：{string.Join("、", profile.DialogueTags)}");
        sb.AppendLine($"· 样本池：{profile.Samples.Count} 段（" +
                      string.Join("、", AuthorStyleBuilder.SceneOrder.Select(sc =>
                          $"{sc} {profile.Samples.Count(x => x.Scene == sc)}")) + "）" +
                      $"；每次生成按场景 + 章号取 {AuthorStyleBuilder.SamplesPerRequest} 段");
        sb.AppendLine($"· 守则：{AuthorStyleStore.LoadRules(profile.Id).Count} 条" +
                      "（独立保存，重新 import 不会覆盖；用 style_profile_samples action=rules 写入）");
        sb.AppendLine();
        sb.Append("下一步：style_profile_samples 抽查/禁用样本；style_profile_bind 绑定到某本书；" +
                  "或 ai_write 传 style 参数临时试用。");
        return ToolResult.Ok(sb.ToString().Trim());
    }

    /// <summary>把某份档案绑定到当前打开的书（写进项目文件）。不传 id = 解绑。</summary>
    internal static ToolResult Bind(Session s, JsonElement args)
    {
        if (!s.TryGet(out var p, out var err)) return err;

        // rhythmFrom：段落与对话节奏改取另一份档案（混合档）。
        // 不传 = 保持现状；传空串 = 取消混合，回到单一档案。
        var hasRhythm = args.TryGetProperty("rhythmFrom", out var rhEl)
                        && rhEl.ValueKind == JsonValueKind.String;
        var rhythmId = hasRhythm ? (rhEl.GetString() ?? "").Trim() : null;

        var id = NovelTools.Str(args, "id").Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            var old = p.AuthorStyleId;
            var oldRhythm = p.AuthorRhythmId;
            p.AuthorStyleId = null;
            p.AuthorRhythmId = null;
            s.Save();
            if (old == null) return ToolResult.Ok("本书本来就没有绑定作者风格。");
            return ToolResult.Ok(oldRhythm == null
                ? $"已解除本书与「{old}」的绑定。"
                : $"已解除本书与「{old}」的绑定（节奏原取自「{oldRhythm}」）。");
        }

        var profile = AuthorStyleStore.Get(id);
        if (profile == null)
            return ToolResult.Fail($"没有档案「{id}」。style_profile_list 看现有档案。");

        p.AuthorStyleId = profile.Id;

        if (hasRhythm)
        {
            if (rhythmId!.Length == 0 || string.Equals(rhythmId, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                // 空串或与句法档案同一份 → 等于不混合
                p.AuthorRhythmId = null;
            }
            else
            {
                var rhythm = AuthorStyleStore.Get(rhythmId);
                if (rhythm == null)
                    return ToolResult.Fail(
                        $"没有叫「{rhythmId}」的档案，rhythmFrom 无法生效。style_profile_list 看现有档案。");
                p.AuthorRhythmId = rhythm.Id;
            }
        }
        else if (p.AuthorRhythmId != null
                 && string.Equals(p.AuthorRhythmId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            p.AuthorRhythmId = null;   // 换了个句法档案，原来的节奏档案正好同名 → 清掉
        }

        s.Save();

        var msg = new StringBuilder();
        msg.Append($"本书已绑定作者风格 [{profile.Id}] {profile.Name}。");
        if (p.AuthorRhythmId != null)
        {
            var rhythm = AuthorStyleStore.Get(p.AuthorRhythmId)!;
            msg.Append($"\n段落与对话节奏取自 [{rhythm.Id}] {rhythm.Name}（混合档）：\n" +
                       $"  · 句子 / 用词 / 引导语 → {profile.Name}（句长 {profile.SentenceMedian} 字、" +
                       $"每句 {profile.CommaPerSentence:0.0} 逗号）\n" +
                       $"  · 段长 / 长段 / 留白 / 单句成段 / 对话占比 → {rhythm.Name}（段长 " +
                       $"{rhythm.ParagraphMedian:0.#} 字、单句成段 {rhythm.ShortParagraphRatio:P0}、" +
                       $"对话 {rhythm.DialogueRatio:P0}）");
        }
        msg.Append("\n之后的 ai_write（style 参数未覆盖时）都会带它的文风锚定段；" +
                   "请求级换风格只传 style 参数，不动这里的绑定。");
        return ToolResult.Ok(msg.ToString());
    }

    /// <summary>
    /// 样本池管理：列样本 / 预览 / 禁用 / 启用 / 删除 / 加自己的定稿 / 写守则。
    /// 守则单独存 rules.json，所以 style_profile_import 重算档案不会冲掉它。
    /// </summary>
    internal static ToolResult Samples(Session s, JsonElement args)
    {
        var id = NovelTools.Str(args, "id").Trim();
        if (id.Length == 0)
        {
            if (!s.TryGet(out var cur, out var openErr)) return openErr;
            id = cur.AuthorStyleId ?? "";
        }
        if (id.Length == 0)
            return ToolResult.Fail("没有指定档案，当前书也没绑定作者风格。传 id，或先 style_profile_bind。");

        var p = AuthorStyleStore.Get(id);
        if (p == null)
            return ToolResult.Fail($"没有档案「{id}」。style_profile_list 看现有档案。");

        var action = NovelTools.Str(args, "action", "list").Trim().ToLowerInvariant();
        var sampleId = NovelTools.Str(args, "sample").Trim();

        switch (action)
        {
            case "list":
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{p.Id}] {p.Name} 样本池 {p.Samples.Count} 段" +
                              $"（每次生成按场景 + 章号取 {AuthorStyleBuilder.SamplesPerRequest} 段）");
                sb.AppendLine($"守则 {p.Rules.Count} 条；场景分布：" +
                    string.Join("、", AuthorStyleBuilder.SceneOrder.Select(sc =>
                        $"{sc} {p.Samples.Count(x => x.Scene == sc && x.Enabled)}")));
                sb.AppendLine();
                foreach (var x in p.Samples)
                    sb.AppendLine($"· {x.Id,-4} [{x.Scene}] {x.Source} " +
                                  $"{AuthorStyleBuilder.VisibleChars(x.Text)} 字 " +
                                  $"贴合 {x.Fit:0.00}{(x.Enabled ? "" : "（已禁用）")}｜{x.Note}");
                sb.AppendLine();
                sb.Append("预览全文：action=preview & sample=<id>；禁用/启用：action=disable|enable；" +
                          "加自己定稿：action=add & text=... & scene=...；写守则：action=rules & rules=...（换行分隔）");
                return ToolResult.Ok(sb.ToString().Trim());
            }
            case "preview":
            {
                var x = p.Samples.FirstOrDefault(v => v.Id == sampleId);
                if (x == null)
                    return ToolResult.Fail($"没有样本「{sampleId}」。action=list 看编号。");
                return ToolResult.Ok($"──{x.Scene}｜{x.Source}｜{x.Note}──\n{x.Text}");
            }
            case "disable":
            case "enable":
            case "delete":
            {
                var list = p.Samples.ToList();
                var idx = list.FindIndex(v => v.Id == sampleId);
                if (idx < 0)
                    return ToolResult.Fail($"没有样本「{sampleId}」。action=list 看编号。");
                if (action == "delete") list.RemoveAt(idx);
                else list[idx] = list[idx] with { Enabled = action == "enable" };
                AuthorStyleStore.Save(p with { Samples = list });
                return ToolResult.Ok(action == "delete"
                    ? $"已从 [{p.Id}] 样本池删除 {sampleId}。"
                    : $"[{p.Id}] 样本 {sampleId} 已{(action == "enable" ? "启用" : "禁用")}。");
            }
            case "add":
            {
                var text = NovelTools.Str(args, "text").Trim();
                if (AuthorStyleBuilder.VisibleChars(text) < 60)
                    return ToolResult.Fail("text 太短（<60 字），当不了 few-shot 样本。请给一段完整定稿。");
                var scene = AuthorStyleBuilder.NormalizeScene(NovelTools.Str(args, "scene"))
                            ?? AuthorStyleBuilder.GuessScene(null, null, text)
                            ?? "日常推进";
                var note = NovelTools.Str(args, "note").Trim();
                var list = p.Samples.ToList();
                var n = 1;
                while (list.Any(v => v.Id == $"u{n:00}")) n++;
                list.Add(new AuthorStyleProfile.StyleSample(scene, text)
                {
                    Id = $"u{n:00}",
                    Source = "用户定稿",
                    Note = note.Length > 0 ? note : "作者本人定稿，优先贴近本书语感",
                    Fit = 1,
                    Enabled = true,
                });
                AuthorStyleStore.Save(p with { Samples = list });
                return ToolResult.Ok($"已把这段定稿加入 [{p.Id}] 样本池（u{n:00}／{scene}）。" +
                                     "它会和其他样本一样按场景参与轮换。");
            }
            case "rules":
            {
                var lines = NovelTools.Str(args, "rules")
                    .Split('\n')
                    .Select(x => x.Trim().TrimStart('-', '·', '*').Trim())
                    .Where(x => x.Length > 0)
                    .ToArray();
                AuthorStyleStore.SaveRules(p.Id, lines);
                return ToolResult.Ok($"[{p.Id}] 作者守则已写入 {lines.Length} 条" +
                                     "（独立文件，重新 import 不会丢）。");
            }
            default:
                return ToolResult.Fail(
                    $"未知 action「{action}」。可用：list / preview / disable / enable / delete / add / rules。");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  调参（tuning）：覆盖自动推导的开关与提示词措辞
    //
    //  ★ 风险分级是**硬性契约**，不是建议：
    //    L0 低（extra）           —— 直接生效
    //    L1 中（档位开关）         —— 必须 confirm=yes，且先向使用者说明
    //    L2 高（阈值 / 措辞覆盖）  —— 两阶段：先返回确认码，approve 才落盘
    //
    //  为什么 L2 要两阶段：MCP 工具在技术上无法分辨调用者是人是 agent，
    //  所以「人类许可」只能靠流程强制——变更不立刻生效，agent 必须把摘要
    //  念给使用者并拿到确认码。每次落盘都写 audit.log，事后可查。
    // ════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> KeyHelp = new(StringComparer.OrdinalIgnoreCase)
    {
        [StylePromptKeys.Intro] = "开头三行总述",
        [StylePromptKeys.SentenceShort] = "句长跟场景走（sceneBound 分支）",
        [StylePromptKeys.SentenceMedian] = "中位句长与 CV",
        [StylePromptKeys.SentenceComma] = "每句逗号数",
        [StylePromptKeys.SentenceShortRatio] = "8 字短句占比",
        [StylePromptKeys.SentenceCounter] = "短句反例（禁止项）",
        [StylePromptKeys.ParaCenter] = "段长中心",
        [StylePromptKeys.ParaAnchor] = "长段要有「锚」",
        [StylePromptKeys.ParaQuietQuota] = "留白段落的长度配额",
        [StylePromptKeys.ParaQuietForce] = "每章强制一处空转",
        [StylePromptKeys.ParaQuietSoft] = "留白不必每章都有",
        [StylePromptKeys.ParaLongLoose] = "长段按需（不教留白那档）",
        [StylePromptKeys.ParaShort] = "单句成段量化",
        [StylePromptKeys.ParaBreak] = "断段规则",
        [StylePromptKeys.ParaNoCounting] = "不要数着写",
        [StylePromptKeys.DialogueMain] = "对话占比要求",
        [StylePromptKeys.DialogueRun] = "连续对话长度",
        [StylePromptKeys.DialogueTag] = "对话引导语",
        [StylePromptKeys.DialogueNoPadding] = "不要没话找话",
        [StylePromptKeys.WordBudget] = "风格词预算与主力词",
        [StylePromptKeys.WordRepeat] = "同词不连段",
        [StylePromptKeys.Closing] = "结尾声明（守则优先）",
    };

    /// <summary>0 = 低风险；1 = 中；2 = 高；-1 = 未知 key。</summary>
    private static int RiskOf(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "extra": return 0;
            case "quietmode":
            case "shortparamode": return 1;
            case "quietlongmin":
            case "quietdominant":
            case "longparamin":
            case "shortparamin": return 2;
        }
        return KeyHelp.ContainsKey(key) ? 2 : -1;
    }

    internal static ToolResult Tuning(Session s, JsonElement args)
    {
        var id = NovelTools.Str(args, "id").Trim();
        if (id.Length == 0)
        {
            if (!s.TryGet(out var cur, out var openErr)) return openErr;
            id = cur.AuthorStyleId ?? "";
        }
        if (id.Length == 0)
            return ToolResult.Fail("没有指定档案，当前书也没绑定作者风格。传 id，或先 style_profile_bind。");

        var p = AuthorStyleStore.Get(id);
        if (p == null) return ToolResult.Fail($"没有档案「{id}」。style_profile_list 看现有档案。");

        var action = NovelTools.Str(args, "action", "get").Trim().ToLowerInvariant();
        var tune = AuthorStyleStore.LoadTuning(p.Id);

        switch (action)
        {
            case "get": return ShowTuning(p, tune);
            case "keys": return ShowKeys(tune);
            case "set": return SetTuning(p, tune, args);
            case "reset": return ResetTuning(p, tune, args);
            case "pending": return ShowPending(p);
            case "approve": return Approve(p, args);
            default:
                return ToolResult.Fail($"未知 action「{action}」。可用：get / keys / set / reset / pending / approve。");
        }
    }

    private static ToolResult ShowTuning(AuthorStyleProfile p, StyleTuning t)
    {
        var sb = new System.Text.StringBuilder();
        var file = Path.Combine(AuthorStyleStore.Directory, p.Id + StyleTuning.FileSuffix);
        sb.AppendLine($"[{p.Id}] {p.Name} 调参" +
                      (File.Exists(file) ? $"（{p.Id}{StyleTuning.FileSuffix}）" : "（文件不存在 = 全自动）"));
        sb.AppendLine();

        // 当前实际生效的档位——这是使用者最想知道的一句
        var quiet = t.ResolveQuiet(p);
        var quietWhy = t.QuietModeOr() != "auto"
            ? $"手动设为 {t.QuietModeOr()}"
            : $"自动：长段 {p.LongParagraphRatio:P0}（阈 {t.LongParaMinOr():0.00}），" +
              $"其中无引号 {p.QuietLongRatio:P0}（阈 {t.QuietLongMinOr():0.00} / 强制 {t.QuietDominantOr():0.00}）";
        var quietText = quiet switch
        {
            "none" => "不提留白",
            "anchor" => "给锚点，不强制每章",
            _ => "每章强制一处空转",
        };
        sb.AppendLine($"【当前生效】留白：{quietText}　（{quietWhy}）");

        var shortText = t.ResolveShortPara(p) ? $"量化 {p.ShortParagraphRatio:P0}" : "不量化";
        sb.AppendLine($"　　　　　　单句成段：{shortText}　（阈 {t.ShortParaMinOr():0.00}）");
        sb.AppendLine();

        sb.AppendLine("【人工设置】");
        if (t.IsEmpty) sb.AppendLine("· 无（全部按档案统计自动推导）");
        else
        {
            if (t.QuietMode != null) sb.AppendLine($"· 留白档位 = {t.QuietMode}");
            if (t.ShortParaMode != null) sb.AppendLine($"· 单句成段 = {t.ShortParaMode}");
            if (t.QuietLongMin != null) sb.AppendLine($"· 留白阈值 = {t.QuietLongMin:0.00}");
            if (t.QuietDominant != null) sb.AppendLine($"· 强制空转阈值 = {t.QuietDominant:0.00}");
            if (t.LongParaMin != null) sb.AppendLine($"· 长段阈值 = {t.LongParaMin:0.00}");
            if (t.ShortParaMin != null) sb.AppendLine($"· 短段阈值 = {t.ShortParaMin:0.00}");
            if (t.Overrides is { Count: > 0 })
                foreach (var kv in t.Overrides)
                    sb.AppendLine($"· 覆盖 {kv.Key} = " +
                                  (string.IsNullOrWhiteSpace(kv.Value) ? "（删除该条）" : $"「{Clip(kv.Value, 30)}」"));
            if (t.Extra is { Count: > 0 })
                foreach (var e in t.Extra) sb.AppendLine($"· 补充指令「{Clip(e, 30)}」");
        }
        sb.AppendLine();

        var pending = AuthorStyleStore.LoadPending(p.Id);
        if (pending != null)
        {
            sb.AppendLine($"【待确认】码 {pending.Code}（{pending.Created}）：{pending.Summary}");
            sb.AppendLine("　　确认：action=approve & value=<码>；不想要就忽略，10 分钟后失效。");
            sb.AppendLine();
        }

        var audit = AuthorStyleStore.LoadAudit(p.Id, 5);
        if (audit.Count > 0)
        {
            sb.AppendLine("【最近操作】");
            foreach (var a in audit) sb.AppendLine("· " + a);
        }

        sb.Append("列表：action=keys；修改见下方说明。");
        sb.AppendLine();
        sb.AppendLine("· 低风险（直接生效）：key=extra（追加指令）");
        sb.AppendLine("· 中风险（要 confirm=yes）：key=quietMode|shortParaMode");
        sb.AppendLine("· 高风险（先返回确认码，approve 才落盘）：4 个阈值、任何提示词条目覆盖");
        return ToolResult.Ok(sb.ToString().Trim());
    }

    private static ToolResult ShowKeys(StyleTuning t)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("可覆盖的提示词条目（value 传空字符串 = 删掉这一条）：");
        foreach (var k in StylePromptKeys.All)
        {
            var over = t.Overrides != null && t.Overrides.ContainsKey(k) ? " ←已覆盖" : "";
            sb.AppendLine($"· {k,-22} {KeyHelp[k]}{over}");
        }
        sb.AppendLine();
        sb.AppendLine("结构化开关（不属于提示词条目）：");
        sb.AppendLine("· quietMode      auto | off（不提留白）| anchor（给锚点）| force（每章强制）");
        sb.AppendLine("· shortParaMode  auto | off | on");
        sb.AppendLine("· quietLongMin / quietDominant / longParaMin / shortParaMin   0~1 之间的小数");
        sb.AppendLine("· extra          追加一段自定义要求（低风险，可多次追加）");
        return ToolResult.Ok(sb.ToString().Trim());
    }

    private static ToolResult SetTuning(AuthorStyleProfile p, StyleTuning tune, JsonElement args)
    {
        var key = NovelTools.Str(args, "key").Trim();
        if (key.Length == 0) return ToolResult.Fail("key 不能为空。action=keys 看可用条目。");

        // value 必须区分「没传」和「传了空串」——空串在覆盖里表示删除该条
        var hasValue = args.TryGetProperty("value", out var vEl) &&
                       vEl.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        var value = hasValue ? (vEl.ValueKind == JsonValueKind.String ? vEl.GetString() ?? "" : vEl.ToString()) : null;

        var risk = RiskOf(key);
        if (risk < 0)
            return ToolResult.Fail($"未知 key「{key}」。action=keys 看可用条目。");
        if (value == null)
            return ToolResult.Fail("要改哪个值？传 value=…（覆盖提示词条目时，空字符串表示删掉这一条）。");

        // 先把变更应用到副本
        var next = Clone(tune);
        var lower = key.ToLowerInvariant();
        string summary;
        switch (lower)
        {
            case "quietmode":
            {
                var v = value.Trim().ToLowerInvariant();
                if (v is not ("auto" or "off" or "anchor" or "force"))
                    return ToolResult.Fail("quietMode 只能是 auto / off / anchor / force。");
                next.QuietMode = v == "auto" ? null : v;
                summary = $"留白档位 → {v}";
                break;
            }
            case "shortparamode":
            {
                var v = value.Trim().ToLowerInvariant();
                if (v is not ("auto" or "off" or "on"))
                    return ToolResult.Fail("shortParaMode 只能是 auto / off / on。");
                next.ShortParaMode = v == "auto" ? null : v;
                summary = $"单句成段量化 → {v}";
                break;
            }
            case "quietlongmin":
            case "quietdominant":
            case "longparamin":
            case "shortparamin":
            {
                if (!double.TryParse(value.Trim(), out var d) || d < 0 || d > 1)
                    return ToolResult.Fail($"{key} 要 0~1 之间的小数（现在传的是「{value}」）。");
                switch (lower)
                {
                    case "quietlongmin": next.QuietLongMin = d; break;
                    case "quietdominant": next.QuietDominant = d; break;
                    case "longparamin": next.LongParaMin = d; break;
                    default: next.ShortParaMin = d; break;
                }
                summary = $"{key} → {d:0.00}";
                break;
            }
            case "extra":
            {
                if (string.IsNullOrWhiteSpace(value))
                    return ToolResult.Fail("extra 要传入指令文本；清空请用 reset 或覆盖成空。");
                next.Extra ??= new List<string>();
                next.Extra.Add(value.Trim());
                summary = $"追加指令「{Clip(value.Trim(), 40)}」";
                break;
            }
            default:
            {
                // 提示词条目覆盖：key 大小写不敏感，落盘时用规范写法
                var canon = StylePromptKeys.All.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                next.Overrides ??= new Dictionary<string, string>();
                if (string.IsNullOrWhiteSpace(value)) next.Overrides[canon] = "";
                else next.Overrides[canon] = value;
                summary = string.IsNullOrWhiteSpace(value)
                    ? $"删除条目 {canon}（{KeyHelp[canon]}）"
                    : $"改写条目 {canon}（{KeyHelp[canon]}）";
                break;
            }
        }

        // ── L0：直接落盘 ──
        if (risk == 0)
        {
            AuthorStyleStore.SaveTuning(p.Id, next);
            AuthorStyleStore.AppendAudit(p.Id, $"L0 {summary}");
            return ToolResult.Ok($"已生效：{summary}。\n" +
                                  "（低风险项，直接写入；下次生成立即使用。）");
        }

        // ── L1：中风险，要显式确认 ──
        if (risk == 1)
        {
            var confirm = NovelTools.Str(args, "confirm").Trim().ToLowerInvariant();
            if (confirm is not ("yes" or "true" or "1" or "确认"))
                return ToolResult.Ok(
                    $"⚠ 这将改变生成行为，尚未生效。\n\n变更：{summary}\n" +
                    $"影响：{DescribeImpact(p, next)}\n\n" +
                    "请先把上面两行告诉使用者，得到同意后带 confirm=yes 再调用一次。");

            AuthorStyleStore.SaveTuning(p.Id, next);
            AuthorStyleStore.AppendAudit(p.Id, $"L1 {summary}（已确认）");
            return ToolResult.Ok($"已生效：{summary}。\n影响：{DescribeImpact(p, next)}");
        }

        // ── L2：高风险，两阶段 ──
        var code = NewCode();
        var pending = new StylePendingChange
        {
            Code = code,
            Created = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Summary = summary,
            Tuning = next,
            Changes = new List<string> { summary },
        };
        AuthorStyleStore.SavePending(p.Id, pending);

        return ToolResult.Ok(
            $"⛔ 底层修改，**尚未生效**。\n\n" +
            $"档案：{p.Name}（{p.Id}）\n" +
            $"变更：{summary}\n" +
            $"影响：{DescribeImpact(p, next)}\n\n" +
            "请把「档案 / 变更 / 影响」三行完整展示给使用者，说明这会改变以后每一章的生成，" +
            "拿到明确同意后，再调用：\n" +
            $"style_profile_tuning action=approve & id={p.Id} & value={code}\n\n" +
            $"（确认码 {code}，10 分钟内有效、一次性。不要自行填写——没有使用者的同意就不要 approve。）");
    }

    private static ToolResult ResetTuning(AuthorStyleProfile p, StyleTuning tune, JsonElement args)
    {
        if (tune.IsEmpty) return ToolResult.Ok($"〔{p.Id}〕本来就是全自动，无需重置。");
        var confirm = NovelTools.Str(args, "confirm").Trim().ToLowerInvariant();
        if (confirm is not ("yes" or "true" or "1" or "确认"))
            return ToolResult.Ok($"⚠ 将清空〔{p.Id}〕的全部人工调参（当前：{tune.Describe()}），回到按档案统计自动推导。\n" +
                                  "确认后带 confirm=yes 再调用一次。");
        var before = tune.Describe();
        AuthorStyleStore.ResetTuning(p.Id);
        AuthorStyleStore.AppendAudit(p.Id, $"reset 清空调参（原：{before}）");
        return ToolResult.Ok($"已清空〔{p.Id}〕的调参，回到全自动。");
    }

    private static ToolResult ShowPending(AuthorStyleProfile p)
    {
        var pending = AuthorStyleStore.LoadPending(p.Id);
        if (pending == null) return ToolResult.Ok($"〔{p.Id}〕没有待确认的变更。");
        return ToolResult.Ok($"〔{p.Id}〕待确认：\n· 码 {pending.Code}（{pending.Created}）\n" +
                              $"· 变更：{pending.Summary}\n· 内容：{pending.Tuning.Describe()}\n\n" +
                              $"确认：action=approve & value={pending.Code}（10 分钟有效，一次性）");
    }

    private static ToolResult Approve(AuthorStyleProfile p, JsonElement args)
    {
        var code = NovelTools.Str(args, "value").Trim();
        if (code.Length == 0) code = NovelTools.Str(args, "code").Trim();
        var pending = AuthorStyleStore.LoadPending(p.Id);
        if (pending == null) return ToolResult.Fail($"〔{p.Id}〕没有待确认的变更（或已过期被清理）。");
        if (!string.Equals(pending.Code, code, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"确认码不对。当前待确认的是 {pending.Code}，传的是「{code}」。");

        if (DateTimeOffset.TryParse(pending.Created, out var created) &&
            DateTimeOffset.Now - created > TimeSpan.FromMinutes(10))
        {
            AuthorStyleStore.SavePending(p.Id, null);
            return ToolResult.Fail("确认码已过期（超过 10 分钟），未落盘。请重新发起修改。");
        }

        AuthorStyleStore.SaveTuning(p.Id, pending.Tuning);
        AuthorStyleStore.SavePending(p.Id, null);
        AuthorStyleStore.AppendAudit(p.Id,
            $"L2 {pending.Summary}（码 {pending.Code} 已确认）");
        return ToolResult.Ok($"已落盘：{pending.Summary}\n" +
                              $"当前调参：{pending.Tuning.Describe()}\n" +
                              "（下次生成生效；已记入 audit.log）");
    }

    /// <summary>这句是给人看的——要说清「改了以后会怎样」，而不只是复述参数。</summary>
    private static string DescribeImpact(AuthorStyleProfile p, StyleTuning t)
    {
        var quiet = t.ResolveQuiet(p);
        var quietText = quiet switch
        {
            "none" => "不再要求任何留白/空转段落，长段只给长度上界",
            "anchor" => "长段要求「有一个不动的人」作锚点，但不强制每章都有",
            _ => "每章强制至少一处不推进情节的空转段落",
        };
        var shortText = t.ResolveShortPara(p)
            ? $"要求单句成段约占 {p.ShortParagraphRatio:P0}"
            : "不对单句成段给量化要求";
        return $"{quietText}；{shortText}。";
    }

    private static StyleTuning Clone(StyleTuning t) => new()
    {
        QuietMode = t.QuietMode,
        ShortParaMode = t.ShortParaMode,
        QuietLongMin = t.QuietLongMin,
        QuietDominant = t.QuietDominant,
        LongParaMin = t.LongParaMin,
        ShortParaMin = t.ShortParaMin,
        Overrides = t.Overrides == null ? null : new Dictionary<string, string>(t.Overrides),
        Extra = t.Extra == null ? null : new List<string>(t.Extra),
    };

    private static string NewCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // 去掉易混的 I/O/0/1
        var buf = new char[6];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(buf);
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
