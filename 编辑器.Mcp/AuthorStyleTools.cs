using System.IO;
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

        var id = NovelTools.Str(args, "id").Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            var old = p.AuthorStyleId;
            p.AuthorStyleId = null;
            s.Save();
            return ToolResult.Ok(old == null
                ? "本书本来就没有绑定作者风格。"
                : $"已解除本书与「{old}」的绑定。");
        }

        var profile = AuthorStyleStore.Get(id);
        if (profile == null)
            return ToolResult.Fail($"没有档案「{id}」。style_profile_list 看现有档案。");

        p.AuthorStyleId = profile.Id;
        s.Save();
        return ToolResult.Ok($"本书已绑定作者风格 [{profile.Id}] {profile.Name}。\n" +
                             $"之后的 ai_write（style 参数未覆盖时）都会带它的文风锚定段；" +
                             $"请求级换风格只传 style 参数，不动这里的绑定。");
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
}
