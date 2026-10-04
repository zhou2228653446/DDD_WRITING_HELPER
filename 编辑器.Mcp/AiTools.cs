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
    public static async Task<ToolResult> WriteAsync(Session s, NovelProject p, JsonElement args)
    {
        var task = NovelTools.Str(args, "task", "continue").Trim().ToLowerInvariant();
        var instruction = NovelTools.Str(args, "instruction");
        var number = NovelTools.Int(args, "number", -1);
        var maxTokens = NovelTools.Int(args, "maxTokens", 0);
        var writeBack = NovelTools.Bool(args, "writeBack", false);

        // ---- 1. 取 API 配置（和界面共用同一份 api_profiles.json）----
        ApiProfileManager? mgr = null;
        ApiConfig config;
        try
        {
            mgr = LoadProfiles();
            var want = NovelTools.Str(args, "profile");
            if (!string.IsNullOrWhiteSpace(want))
            {
                // 允许 agent 指定用哪一个配置（比如论文走 GPT、小说走 DeepSeek）
                config = mgr.Profiles.TryGetValue(want, out var chosen)
                    ? chosen
                    : throw new InvalidOperationException(
                        $"没有名为「{want}」的配置。已有的：{string.Join("、", mgr.GetProfileNames())}");
            }
            else
            {
                config = mgr.ActiveProfile
                         ?? throw new InvalidOperationException("没有已启用的 API 配置");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                $"读取 API 配置失败：{ex.Message}\n" +
                "请先在软件里「AI 设置」配好服务商与 Key（MCP 与界面共用同一份配置），" +
                "或用 ai_config_check 查看当前配置状态。");
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
        {
            // 出错时把服务端/本地的原因带出来——只说「没有可用内容」等于让 agent 瞎猜重试。
            var why = string.IsNullOrWhiteSpace(result.Text) ? "" : $"返回内容：{Session.Truncate(result.Text, 300)}\n";
            return ToolResult.Fail(
                $"模型没有返回可用内容。{(result.IsCanceled ? "（已取消）" : "（出错）")}\n{why}" +
                $"输出 token：{result.OutputTokens}\n" +
                "常见原因：Key 失效或欠费（401）、模型名不对（404）、服务商地址不通。\n" +
                "可在软件「AI 设置」里点测试连接确认（MCP 与界面共用同一份配置）。");
        }

        var head = $"[task={task} · model={config.Model} · 输入 {result.InputTokens} / 输出 {result.OutputTokens} token" +
                   (result.CachedInputTokens > 0 ? $" · 缓存命中 {result.CachedInputTokens}" : "") + "]\n\n";

        var body = head + result.Text;

        // ---- 5. 可选写回正文 ----
        // 默认关闭：正文是作者的稿子，绝不能在 agent 没明确要求时替他改。
        // 真写回时同样要过防覆盖检查（Session.IsStale），和 chapter_write 一个规矩。
        if (writeBack && chapter != null && task is "continue" or "polish" or "expand")
        {
            if (s.IsStale(out var stale))
            {
                return ToolResult.Fail(
                    stale.Text + "\n\n本次生成的内容**没有写入**（正文如下，请人工确认后再写入）：\n\n" + body);
            }

            // 续写/扩写默认追加（接在后面），润色默认替换（改的就是这一章）
            var mode = NovelTools.Str(args, "writeMode",
                task == "polish" ? "replace" : "append").ToLowerInvariant();
            var append = mode != "replace";
            int before = chapter.WordCount;

            if (append)
            {
                var sep = chapter.Content.EndsWith('\n') || chapter.Content.Length == 0 ? "" : "\n";
                chapter.Content = chapter.Content + sep + result.Text;
            }
            else
            {
                chapter.Content = result.Text;
            }

            chapter.ModifiedDate = DateTime.Now;
            chapter.LastModified = DateTime.Now;
            s.Save();

            return ToolResult.Ok(body +
                $"\n\n✅ 已{(append ? "追加到" : "覆盖")}第{chapter.ChapterNumber}章「{chapter.Title}」：" +
                $"{before} 字 → {chapter.WordCount} 字（writeMode={mode}）。");
        }

        return ToolResult.Ok(body +
            "\n\n（只返回文本，未改动正文。要落盘：再调一次加 writeBack=true，" +
            "或自行整理后用 chapter_write 写入。）");
    }

    /// <summary>
    /// ai_config_check：让 agent 自己能诊断"为什么调不通"。
    /// ai_write 失败时最常见的原因是 Key 失效/欠费（401）或模型名不对（404），
    /// 如果只回一句「没有可用内容」，agent 只能盲目重试。这里把配置状态与（可选的）
    /// 真实连通性探测结果摊开给它看。
    ///
    /// ★ 安全：任何情况下都**不输出 Key 本身**，只给长度和是否为空。
    /// </summary>
    public static async Task<ToolResult> ConfigCheckAsync(JsonElement args)
    {
        ApiProfileManager mgr;
        try
        {
            mgr = LoadProfiles();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"读取 API 配置失败：{ex.Message}\n配置目录：{ConfigDir()}");
        }

        var names = mgr.GetProfileNames();
        var sb = new StringBuilder();
        sb.AppendLine($"配置文件：{mgr.GetJsonPath()}");
        sb.AppendLine($"已配置：{names.Count} 个{(names.Count == 0 ? "（还没有配置过）" : "：" + string.Join("、", names))}");
        sb.AppendLine($"当前启用：{(string.IsNullOrWhiteSpace(mgr.ActiveProfileName) ? "（无）" : mgr.ActiveProfileName)}");

        var want = NovelTools.Str(args, "profile");
        var name = string.IsNullOrWhiteSpace(want) ? mgr.ActiveProfileName : want;
        if (string.IsNullOrWhiteSpace(name) || !mgr.Profiles.TryGetValue(name, out var cfg))
        {
            sb.AppendLine("\n⚠ 没有可用的配置。请在软件「AI 设置」里添加服务商与 Key。");
            return ToolResult.Ok(sb.ToString());
        }

        var key = cfg.ApiKey ?? "";
        var provider = ApiProviders.Find(cfg.Provider);
        sb.AppendLine();
        sb.AppendLine($"—— 配置「{name}」——");
        sb.AppendLine($"服务商：{provider?.Name ?? cfg.Provider ?? "（未填）"}（id={cfg.Provider}）");
        sb.AppendLine($"模型：{cfg.Model ?? "（未填）"}");
        sb.AppendLine($"地址：{cfg.ApiUrl ?? "（未填）"}");
        sb.AppendLine($"Key：{(key.Length == 0 ? "❌ 空" : $"已填（{key.Length} 字符）")}");

        var problems = new List<string>();
        if (key.Length == 0) problems.Add("Key 为空");
        if (string.IsNullOrWhiteSpace(cfg.Model)) problems.Add("没填模型名");
        if (string.IsNullOrWhiteSpace(cfg.ApiUrl)) problems.Add("没填接口地址");

        var probe = NovelTools.Bool(args, "probe", true);
        if (probe && problems.Count == 0)
        {
            sb.AppendLine("\n连通性探测：");
            IApiService service = IsAnthropic(cfg) ? new AnthropicService(cfg) : new OpenAIService(cfg);
            try
            {
                var r = await service.CompleteTextAsync(
                    "回复一个字：好",
                    "你是连通性测试。只回复一个字。",
                    new CompletionOptions { MaxTokens = 16, Temperature = 0, Model = cfg.Model ?? "" });

                if (r.IsUsable)
                {
                    sb.AppendLine($"✅ 通。模型回复：{Session.Truncate(r.Text.Trim(), 40)}" +
                                  $"（输入 {r.InputTokens} / 输出 {r.OutputTokens} token）");
                }
                else
                {
                    problems.Add("模型返回不可用");
                    sb.AppendLine($"❌ 调不通：{Session.Truncate(r.Text, 300)}");
                    sb.AppendLine("   常见原因：Key 失效或欠费（401）、模型名不存在（404）、地址不通。");
                }
            }
            catch (Exception ex)
            {
                problems.Add("请求异常");
                sb.AppendLine($"❌ 请求异常：{ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(problems.Count == 0
            ? "结论：配置看起来正常，可以直接用 ai_write。"
            : "结论：❌ " + string.Join("；", problems) + "。请在软件「AI 设置」里修正后再调用 ai_write。");

        return ToolResult.Ok(sb.ToString());
    }

    private static string ConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");

    private static ApiProfileManager LoadProfiles()
    {
        var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
        mgr.Load();
        return mgr;
    }

    private static bool IsAnthropic(ApiConfig c)
    {
        var s = (c.Provider ?? "") + " " + (c.ApiUrl ?? "");
        return s.Contains("anthropic", StringComparison.OrdinalIgnoreCase);
    }
}
