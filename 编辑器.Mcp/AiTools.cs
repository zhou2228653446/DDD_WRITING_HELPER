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
    public static async Task<ToolResult> WriteAsync(
        Session s, NovelProject p, JsonElement args, JsonElement? progressToken = null)
    {
        // 生成是几十秒级的长任务。客户端给了 progressToken 就沿途推进度，
        // 否则用户那边全程黑屏——只能干等着，和卡死没有区别。
        void Report(int progress, int total, string message) =>
            McpServer.NotifyProgress(progressToken, progress, total, message);

        Report(0, 100, "正在组装上下文…");
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
        //
        // ★ 这一段刻意照抄 MainWindow 的三步：技能解析 → 上下文块 → 分段组装。
        // 早期版本在这里直接 `AiPrompts.Build(taskPrompt, contextBlock, contract)`，
        // 一次漏掉了三样东西：AI 写作记忆、参考文献库、技能。结果就是 README 承诺的
        // 「和界面里点按钮完全同源」是空的——agent 拿到的其实是一个少了记忆、
        // 少了文献约束、不带手法的裸调用。改这里时请对照 MainWindow.BuildSystemPrompt。

        // ★ 生成类（outline / character / background / chapter_outline / write_style）
        // 是"从零把一本书立起来"的那一步，界面有、MCP 原本没有——
        // agent 只能退化成 chat 随手编一段再塞进设定，不走专门提示词也不走结构化契约。
        // 这几条现在是 agent 独自完成一本小说的起点。
        //
        // 已有内容时改用 Expand 任务（在已有基础上完善而不是推翻），
        // 与界面 RequestAiForSettingAsync 的 isExpand 分支一致。
        var existing = ExistingSettingFor(p, task);
        var isExpand = !string.IsNullOrWhiteSpace(existing);

        var (taskKey, taskFallback, contractFallback) = task switch
        {
            "continue" => (AiPrompts.Keys.Continue, AiPrompts.Task.Continue, (string?)null),
            "polish" => (AiPrompts.Keys.Polish, AiPrompts.Task.Polish, null),
            "expand" => (AiPrompts.Keys.Expand, AiPrompts.Task.Expand, null),
            "review" => (AiPrompts.Keys.Review, AiPrompts.Task.Review, "只输出问题清单，不要改写正文。"),
            "setting_book" => (AiPrompts.Keys.SettingBook, AiPrompts.Task.SettingBook, null),
            "name" => (AiPrompts.Keys.Name, AiPrompts.Task.Name, null),
            "chat" => (AiPrompts.Keys.Chat, AiPrompts.Task.Chat, null),

            "outline" => (AiPrompts.Keys.Outline, AiPrompts.Task.Outline, AiPrompts.StructuredOutput),
            "chapter_outline" => (AiPrompts.Keys.ChapterOutline, AiPrompts.Task.ChapterOutline, AiPrompts.StructuredOutput),
            "character" => (AiPrompts.Keys.Character, AiPrompts.Task.Character, AiPrompts.StructuredOutput),
            "background" => (AiPrompts.Keys.Background, AiPrompts.Task.Background, AiPrompts.StructuredOutput),
            "write_style" => (AiPrompts.Keys.WriteStyle, AiPrompts.Task.WriteStyle, AiPrompts.StructuredOutput),
            _ => ("", "", null),
        };

        if (taskKey.Length == 0)
            return ToolResult.Fail(
                $"未知任务「{task}」。可选：continue（续写）/ polish（润色）/ expand（扩写）/ " +
                "review（一致性审稿）/ name（起名）/ chat（自由问答）/ " +
                "outline（全文大纲）/ chapter_outline（章节大纲）/ character（人物设定）/ " +
                "background（背景设定）/ write_style（文风）/ setting_book（补设定集）");

        // 界面在已有设定时走 Expand（完善），而不是让它重写一遍
        if (isExpand && taskKey is AiPrompts.Keys.Outline or AiPrompts.Keys.ChapterOutline
                              or AiPrompts.Keys.Character or AiPrompts.Keys.Background
                              or AiPrompts.Keys.WriteStyle)
        {
            taskKey = AiPrompts.Keys.Expand;
            taskFallback = AiPrompts.Task.Expand;
        }

        // 技能：界面在 AI 面板选一个，命中功能的生成就用该手法。MCP 侧同样要生效，
        // 否则「黄金三章」这类手法只在界面里有效，agent 写出来的是另一种调性。
        var skill = ResolveSkill(args, out var skillErr);
        if (skillErr != null) return ToolResult.Fail(skillErr);

        var taskPrompt = NovelSkillStore.ResolveTaskPrompt(skill, taskKey, taskFallback);
        var outputContract = NovelSkillStore.ResolveContract(skill, taskKey, contractFallback);

        // AI 记忆：界面注入 <项目目录>/.ai_memory/memory.md，MCP 早期传 null——
        // 作者沉淀过的偏好（「别写死主角」这类）agent 完全看不到。
        string? memory = null;
        try
        {
            var mem = new AiMemoryManager(p.FilePath);
            mem.Load();
            memory = mem.GetRawMemory();
        }
        catch (Exception ex)
        {
            // 记忆读不到不该让整次生成失败，退化成没有记忆
            Console.Error.WriteLine($"[mcp] 读取 AI 记忆失败（本次不带记忆）：{ex.Message}");
        }

        var effectiveCharacters = p.BuildEffectiveCharacterSettings();
        var stable = AiPrompts.BuildContextBlock(
            p.FullOutline, p.ChapterOutline, effectiveCharacters,
            p.BackgroundSettings, p.WritingStyle, memory, p.NarrativeViewpoint);

        // 设定集：已勾选导出的设定集章节（世界观、年表、专有名词、伏笔等）自动拼进稳定前缀，
        // 避免 agent 写设定集后在常规续写/扩写时完全看不见。（审稿与设定集生成任务本身已单独处理，不重复拼）
        var sbBlock = task is "review" or "setting_book" ? "" : p.BuildSettingsBookContextBlock();
        if (!string.IsNullOrWhiteSpace(sbBlock))
            stable = (stable.Length > 0 ? stable + "\n\n" : "") +
                     AiPrompts.Section("设定集（已确认设定）", sbBlock).TrimEnd();

        // 参考文献库：界面在 BuildSystemPrompt 这个唯一收口点追加，论文方案下
        // AI 引用 [n] 只能出自库内、禁止编造。MCP 不追加的话，agent 让 AI 写的
        // 论文段落会凭空编造文献与 DOI——「零编造」在 MCP 侧就失效了。
        if (p.LiteratureLibrary != null && p.LiteratureLibrary.Count > 0)
            stable = (stable.Length > 0 ? stable + "\n\n" : "") +
                     LiteratureFormatter.BuildContextBlock(p.LiteratureLibrary);

        // ★ 必须用 BuildSections 而不是 Build：
        // Build 会 Flatten 成单段字符串，再经 SystemPrompt.FromText 隐式转换时**整段**
        // 被标成可缓存；而任务段每次请求都可能不同，前缀永远对不上——缓存命中率≈0，
        // 还要按 1.25 倍付写入价。分段后只有「身份 + 设定」那段打断点，才真的能命中。
        var system = AiPrompts.BuildSections(taskPrompt, stable, null, outputContract);

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

        // 续写与审稿：注入之前章节的梗概（Chapter.Summary 优先）+ 紧邻上一章末尾 800 字，
        // 解决此前只截上章开头 800 字导致丢失上章结尾悬念、前情梗概字段形同虚设的问题。
        if (chapter != null && (task == "continue" || task == "review"))
        {
            var priorBrief = p.BuildPriorChapterBrief(chapter.ChapterNumber, includePrevTail: true);
            if (!string.IsNullOrWhiteSpace(priorBrief))
            {
                user.AppendLine(AiPrompts.Section("前情梗概与上章末尾（供衔接与跨章比对）", priorBrief));
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

        // 生成设定时把已有内容一起给过去——没有它，"完善"就无从谈起，
        // AI 只能凭空另写一份，把作者已经定好的东西推翻。
        if (isExpand && !string.IsNullOrWhiteSpace(existing))
            user.AppendLine(AiPrompts.Section("已有内容（在其基础上完善，不要推翻）", existing!));

        if (!string.IsNullOrWhiteSpace(instruction))
            user.AppendLine(AiPrompts.Section("作者的额外要求", instruction));

        if (user.Length == 0)
            user.AppendLine(instruction);

        // ---- 4. 调模型 ----
        // 输出预算：续写/扩写/对话/设定集默认给足 16384（适配推理模型思维链 + 3000~5000 字长章直出），
        // 润色/审稿按原文字数动态算（8192 ~ 16384），其余设定任务默认 8192。
        if (maxTokens <= 0)
        {
            maxTokens = task switch
            {
                "continue" or "expand" or "chat" or "setting_book" => 16384,
                "polish" or "review" => Math.Clamp((chapter?.WordCount ?? 2000) * 3, 8192, 16384),
                _ => 8192,
            };
        }

        IApiService service = IsAnthropic(config)
            ? new AnthropicService(config)
            : new OpenAIService(config);

        var taskLabel = TaskDisplayLabel(task);
        var defaultWriteMode = task == "polish" ? "replace" : "append";
        var effectiveWriteMode = NovelTools.Str(args, "writeMode", defaultWriteMode).ToLowerInvariant();
        int latestInTokens = 0;
        int latestOutTokens = 0;
        string latestStreamText = "";
        long lastStreamPublishTick = 0;

        void PublishStream(string statusMsg, bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastStreamPublishTick < 180) return;
            lastStreamPublishTick = now;

            McpLiveBridge.Publish(new McpLiveEvent
            {
                EventType = "stream",
                ToolName = "ai_write",
                TaskName = task,
                ProjectPath = s.Path,
                ChapterNumber = number > 0 ? number : 0,
                SettingField = SettingTargetFor(task),
                WriteBack = writeBack,
                WriteMode = effectiveWriteMode,
                InputTokens = latestInTokens,
                OutputTokens = latestOutTokens,
                PreviewText = latestStreamText,
                Summary = statusMsg,
            });
        }

        PublishStream($"🤖 MCP AI 正在{taskLabel}{(number > 0 ? $"（第 {number} 章）" : "")}…", force: true);

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
                    // 阶段性提示（"连接不稳定，正在重试（1/4）"、"正在思考…"）直接推给客户端与桌面界面
                    OnNotice = msg =>
                    {
                        Report(0, 100, msg);
                        PublishStream($"🤖 MCP AI {taskLabel}：{msg}");
                    },
                    // 流式生成中：已产出的 token 数就是最自然的进度
                    OnProgress = (inTok, outTok) =>
                    {
                        latestInTokens = inTok;
                        latestOutTokens = outTok;
                        Report(outTok, maxTokens, $"已生成 {outTok} token");
                        PublishStream($"🤖 MCP AI 正在{taskLabel}{(number > 0 ? $"第 {number} 章" : "")}（已生成 {outTok} token · {latestStreamText.Length} 字）");
                    },
                    OnStreamText = text =>
                    {
                        latestStreamText = text;
                        PublishStream($"🤖 MCP AI 正在{taskLabel}{(number > 0 ? $"第 {number} 章" : "")}（已生成 {latestOutTokens} token · {text.Length} 字）");
                    },
                });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"调用模型失败：{ex.Message}");
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

        // 头部把"这次到底带了什么"摊开给 agent：技能/记忆/人物卡/设定集/文献有没有生效，一眼能看出来，
        // 不用猜——尤其是技能静默不生效这类问题，不显示就永远发现不了。
        var head = $"[task={task} · model={config.Model}" +
                   $" · 技能={(skill == null ? "无" : skill.Name)}" +
                   $" · 人物卡={(p.Characters?.Count ?? 0)}张" +
                   $" · 设定集={(string.IsNullOrWhiteSpace(sbBlock) ? "无" : "已注入")}" +
                   $" · 记忆={(string.IsNullOrWhiteSpace(memory) ? "无" : "有")}" +
                   $" · 文献库={(p.LiteratureLibrary?.Count > 0 ? p.LiteratureLibrary.Count + " 篇" : "无")}" +
                   $" · 输入 {result.InputTokens} / 输出 {result.OutputTokens} token" +
                   (result.CachedInputTokens > 0 ? $" · 缓存命中 {result.CachedInputTokens}" : "") + "]\n\n";

        var body = head + result.Text;

        // ---- 5. 可选写回 ----
        // 默认一律关闭：正文是作者的稿子，绝不能在 agent 没明确要求时替他改。
        // 真写回时同样要过防覆盖检查（Session.IsStale），和 chapter_write 一个规矩。

        // 设定类任务写回对应的设定字段（outline → 全文大纲，character → 人物设定 …）
        var settingTarget = SettingTargetFor(task);
        if (writeBack && settingTarget != null)
        {
            if (s.IsStale(out var staleSetting))
                return ToolResult.Fail(staleSetting.Text + "\n\n本次生成的内容**没有写入**（如下）：\n\n" + body);

            var appendSetting = NovelTools.Str(args, "writeMode", "replace")
                .Equals("append", StringComparison.OrdinalIgnoreCase);
            var oldText = ExistingSettingFor(p, task);
            var finalText = appendSetting && !string.IsNullOrWhiteSpace(oldText)
                ? oldText!.TrimEnd() + "\n" + result.Text
                : result.Text;

            s.Snapshot($"MCP ai_write({task})写入设定前");
            if (!NovelTools.ApplySettingField(p, settingTarget, finalText))
                return ToolResult.Fail($"设定字段「{settingTarget}」写不进去（未知字段）。");
            s.Save();

            return ToolResult.Ok(body +
                $"\n\n✅ 已{(appendSetting ? "追加到" : "覆盖")}「{settingTarget}」（{finalText.Length} 字）。");
        }

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

            // 同 NovelTools.WriteChapter：必须在改 chapter.Content 之前存，
            // 否则这份快照记的已经是 AI 改写后的内容，回滚不回原稿。
            s.Snapshot($"MCP ai_write({task})写回第{chapter.ChapterNumber}章前");

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

        var hint = settingTarget != null
            ? $"（只返回文本，未写入设定。要落盘：再加 writeBack=true，" +
              $"或自行整理后用 settings_set 写入「{settingTarget}」。)"
            : "（只返回文本，未改动正文。要落盘：再调一次加 writeBack=true，" +
              "或自行整理后用 chapter_write 写入。）";

        return ToolResult.Ok(body + "\n\n" + hint);
    }

    /// <summary>
    /// 解析本次要用哪个技能。
    ///
    /// 两种情况：
    ///   · agent 显式传 skill（名字/id，或 "none" 表示不用）→ 按名字找，找不到就报错并
    ///     列出当前方案可用的——静默回退到"没技能"会让 agent 以为手法生效了其实没有；
    ///   · 没传 → 用当前提示词方案记住的那个（界面里每个方案各记一个，切方案技能跟着切）。
    ///
    /// 只在**适用当前方案**的技能里挑：创作手法不该出现在论文方案下。
    /// </summary>
    private static NovelSkill? ResolveSkill(JsonElement args, out string? error)
    {
        error = null;
        var presetId = AiPrompts.Store?.ActivePresetId;
        var pool = NovelSkillStore.AvailableFor(NovelSkillStore.Load(ConfigDir()), presetId);

        var want = NovelTools.Str(args, "skill").Trim();
        if (want.Length == 0)
        {
            // 没指定就用方案记住的那个（与界面一致）
            var remembered = presetId == null ? null : AiPrompts.Store?.GetSkillFor(presetId);
            if (string.IsNullOrWhiteSpace(remembered)) return null;
            return pool.FirstOrDefault(x => string.Equals(x.Id, remembered, StringComparison.OrdinalIgnoreCase));
        }

        if (want.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        var hit = pool.FirstOrDefault(x =>
            string.Equals(x.Id, want, StringComparison.OrdinalIgnoreCase) ||
            x.Name.Contains(want, StringComparison.OrdinalIgnoreCase));

        if (hit == null)
        {
            error = $"没有匹配「{want}」的技能。" +
                    (pool.Count == 0
                        ? "当前提示词方案下没有可用技能。"
                        : $"当前方案可用：{string.Join("、", pool.Select(x => x.Name))}。") +
                    "（传 none 表示不用技能）";
        }
        return hit;
    }

    /// <summary>该生成任务对应的"已有内容"。非空表示这次是在已有基础上完善，而不是从零生成。</summary>
    private static string? ExistingSettingFor(NovelProject p, string task) => task switch
    {
        "outline" => p.FullOutline,
        "chapter_outline" => p.ChapterOutline,
        "character" => p.CharacterSettings,
        "background" => p.BackgroundSettings,
        "write_style" => p.WritingStyle,
        _ => null,
    };

    /// <summary>生成任务 → 要写回的设定字段（与 settings_set 同一套名字，走同一条写入路径）。</summary>
    internal static string? SettingTargetFor(string task) => task switch
    {
        "outline" => "full_outline",
        "chapter_outline" => "chapter_outline",
        "character" => "characters",
        "background" => "background",
        "write_style" => "writing_style",
        _ => null,
    };

    internal static string TaskDisplayLabel(string task) => task switch
    {
        "continue" => "续写",
        "polish" => "润色",
        "expand" => "扩写",
        "review" => "审稿",
        "setting_book" => "生成设定集",
        "name" => "生成角色名",
        "chat" => "写作问答",
        "outline" => "生成全文大纲",
        "chapter_outline" => "生成章节大纲",
        "character" => "生成人物设定",
        "background" => "生成背景设定",
        "write_style" => "生成文风设定",
        _ => task,
    };

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
                    new CompletionOptions { MaxTokens = 256, Temperature = 0, Model = cfg.Model ?? "" });

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

    private static string ConfigDir() => NovelTools.ResolveConfigDirectory();

    private static ApiProfileManager LoadProfiles()
    {
        var mgr = new ApiProfileManager(Path.Combine(ConfigDir(), "api_profiles.json"));
        mgr.Load();
        return mgr;
    }

    private static bool IsAnthropic(ApiConfig c) =>
        ApiProviders.ResolveWire(c) == ApiWire.AnthropicMessages;
}
