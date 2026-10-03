using System.Text;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Mcp;

/// <summary>
/// 把「AI 写作」暴露给 agent。
///
/// ★ 这一层是整个 MCP 的关键：agent 拿到的不是"一个通用大模型"，而是**本软件调好的那套
/// AI**：当前生效的提示词方案（小说/论文/公文）、设定集、五项贯穿设定、叙事视角硬约束，
/// 全都和界面里点「续写」时一模一样。否则 agent 另写一套提示词，写出来的东西和软件里
/// 的风格、设定必然对不上，等于两套系统在写同一本书。
/// </summary>
internal static class AiTools
{
    public static async Task<ToolResult> WriteAsync(NovelProject p, JsonElement args)
    {
        var task = NovelTools.Str(args, "task", "continue").Trim().ToLowerInvariant();
        var instruction = NovelTools.Str(args, "instruction");
        var number = NovelTools.Int(args, "number", -1);
        var maxTokens = NovelTools.Int(args, "maxTokens", 0);
        var writeBack = NovelTools.Bool(args, "writeBack", false);

        // ---- 1. 取 API 配置（和界面共用同一份 api_profiles.json）----
        ApiConfig config;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");
            var mgr = new ApiProfileManager(Path.Combine(dir, "api_profiles.json"));
            mgr.Load();
            config = mgr.ActiveProfile
                     ?? throw new InvalidOperationException("没有已启用的 API 配置");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                $"读取 API 配置失败：{ex.Message}\n" +
                "请先在软件里「AI 设置」配好服务商与 Key（MCP 与界面共用同一份配置）。");
        }

        // ---- 2. 拼提示词（全部走 AiPrompts，与界面同源）----
        string taskPrompt;
        string outputContract = "";
        switch (task)
        {
            case "continue": taskPrompt = AiPrompts.Task.Continue; break;
            case "polish": taskPrompt = AiPrompts.Task.Polish; break;
            case "expand": taskPrompt = AiPrompts.Task.Expand; break;
            case "review":
                taskPrompt = AiPrompts.Task.Review;
                outputContract = "只输出问题清单，不要改写正文。";
                break;
            case "setting_book": taskPrompt = AiPrompts.Task.SettingBook; break;
            case "name": taskPrompt = AiPrompts.Task.Name; break;
            case "chat": taskPrompt = AiPrompts.Task.Chat; break;
            default:
                return ToolResult.Fail(
                    $"未知任务「{task}」。可选：continue（续写）/ polish（润色）/ expand（扩写）/ " +
                    "review（一致性审稿）/ setting_book（补设定集）/ name（起名）/ chat（自由问答）");
        }

        var contextBlock = AiPrompts.BuildContextBlock(
            p.FullOutline, p.ChapterOutline, p.CharacterSettings,
            p.BackgroundSettings, p.WritingStyle, null, p.NarrativeViewpoint);

        var system = AiPrompts.Build(taskPrompt, contextBlock, outputContract);

        // ---- 3. 拼 user 侧素材 ----
        var chapter = number > 0
            ? p.Chapters.FirstOrDefault(c => c.ChapterNumber == number)
            : null;
        if (number > 0 && chapter == null)
            return ToolResult.Fail($"没有第 {number} 章。用 chapters_list 看现有章节号。");

        var user = new StringBuilder();
        if (chapter != null)
        {
            user.AppendLine(AiPrompts.Section(
                task == "review" ? "待审章节正文" : "当前章节正文",
                $"第{chapter.ChapterNumber}章「{chapter.Title}」\n{chapter.Content}"));
        }

        // 续写要给前情，否则容易接不上；审稿要给前情链，否则看不见跨章矛盾。
        if (chapter != null && (task == "continue" || task == "review"))
        {
            var take = task == "review" ? 3 : 1;
            var prev = p.Chapters
                .Where(c => c.ChapterNumber < chapter.ChapterNumber && !string.IsNullOrWhiteSpace(c.Content))
                .OrderByDescending(c => c.ChapterNumber)
                .Take(take).OrderBy(c => c.ChapterNumber).ToList();
            if (prev.Count > 0)
            {
                var brief = new StringBuilder();
                foreach (var c in prev)
                    brief.AppendLine($"第{c.ChapterNumber}章「{c.Title}」：" +
                                     Session.Truncate(c.Content, 800));
                user.AppendLine(AiPrompts.Section("前情梗概（供衔接与跨章比对）", brief.ToString()));
            }
        }

        // 审稿需要事实基准：设定集里的事实章就是"本书的真相"
        if (task == "review" && p.SettingsBook?.Chapters.Count > 0)
        {
            var factKeys = new[] { "characters", "relations", "glossary", "history", "world", "power", "foreshadow" };
            var facts = new StringBuilder();
            foreach (var c in p.SettingsBook.Chapters)
            {
                if (!factKeys.Contains(c.SourceKey)) continue;
                if (string.IsNullOrWhiteSpace(c.Content) || !c.IncludeInExport) continue;
                facts.AppendLine($"【{c.Title}】\n{c.Content}\n");
            }
            if (facts.Length > 0)
                user.AppendLine(AiPrompts.Section("设定集事实基准", facts.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(instruction))
            user.AppendLine(AiPrompts.Section("作者的额外要求", instruction));

        if (user.Length == 0)
            user.AppendLine(instruction);

        // ---- 4. 调模型 ----
        // 输出预算：续写/扩写给足，润色按原文字数动态算（与界面 BudgetForRewrite 同思路）
        if (maxTokens <= 0)
        {
            maxTokens = task switch
            {
                "continue" or "expand" => 4000,
                "polish" or "review" => Math.Clamp((chapter?.WordCount ?? 1000) * 2, 1200, 8000),
                _ => 2000,
            };
        }

        IApiService service = IsAnthropic(config)
            ? new AnthropicService(config)
            : new OpenAIService(config);

        AiResult result;
        try
        {
            result = await service.CompleteTextAsync(
                user.ToString(),
                system,
                new CompletionOptions
                {
                    MaxTokens = maxTokens,
                    Temperature = task == "review" ? 0.3 : 0.7,
                    Model = config.Model,
                });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"调用模型失败：ex={ex.Message}");
        }

        if (!result.IsUsable)
            return ToolResult.Fail(
                $"模型没有返回可用内容。{(result.IsCanceled ? "（已取消）" : "（出错）")}\n" +
                $"输出 token：{result.OutputTokens}。可以试试换模型或调小 maxTokens。");

        var head = $"[task={task} · model={config.Model} · 输入 {result.InputTokens} / 输出 {result.OutputTokens} token" +
                   (result.CachedInputTokens > 0 ? $" · 缓存命中 {result.CachedInputTokens}" : "") + "]\n\n";

        // 写回正文是危险操作，默认关闭，必须 agent 显式要求
        if (writeBack && chapter != null && task is "continue" or "polish" or "expand")
        {
            return ToolResult.Ok(head + result.Text +
                $"\n\n⚠ writeBack 需要调用方自己确认：本工具不直接改正文，请复制上面的内容，" +
                $"用 chapter_write（mode=replace 或 append）写入第{chapter.ChapterNumber}章。");
        }

        return ToolResult.Ok(head + result.Text);
    }

    private static bool IsAnthropic(ApiConfig c)
    {
        var s = (c.Provider ?? "") + " " + (c.ApiUrl ?? "");
        return s.Contains("anthropic", StringComparison.OrdinalIgnoreCase);
    }
}
