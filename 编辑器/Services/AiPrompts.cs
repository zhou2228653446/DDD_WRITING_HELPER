using System;
using System.Collections.Generic;
using System.Text;

namespace 编辑器.Services
{
    /// <summary>
    /// 文学创作系统提示词库。
    ///
    /// 设计参照 Claude Code / Codex 这类 Agent 的分层做法，把提示词拆成四层：
    ///
    ///   ① 身份层 <see cref="Base"/>      —— 恒定，所有功能共用。回答"我是谁、我守什么规矩"。
    ///   ② 上下文层 <see cref="BuildContextBlock"/> —— 项目设定（人物/背景/大纲/文风/记忆）。
    ///   ③ 任务层 <see cref="Task"/>.*    —— 每个功能各自的任务说明。
    ///   ④ 输出契约 <see cref="CreativeOutput"/> —— 输出格式与边界（"只输出正文"这类硬约束）。
    ///
    /// 为什么必须用真正的 system role 而不是继续拼在 user 消息里：
    ///   · 身份稳定：模型始终知道自己在给小说作者当助手，不会把"帮我写个开头"当成
    ///     方法论咨询去回答；
    ///   · 权重分明：参考资料（上下文）与被处理的文本（正文）不再平级，模型能区分
    ///     "这是我该参考的"和"这是我该改的"；
    ///   · 缓存友好：system 段是最稳定的前缀，OpenAI / DeepSeek / Claude 的 prompt
    ///     caching 能直接命中，长上下文项目省下可观的 token 与延迟。
    ///
    /// ── 关于"可自定义"（2026-09-26） ──
    /// 本类的所有文本都是**两段式**：
    ///   · <see cref="Defaults"/> 里是出厂内置文本（const，编译期常量）；
    ///   · 本类（以及 <see cref="Task"/>）上的同名属性是**当前生效值**，优先返回用户在
    ///     「AI 设置 → 系统提示词」里改过的内容（存放于 <see cref="Store"/>）。
    /// 调用方照旧写 `AiPrompts.Task.Continue` 即可，不需要关心有没有被改过——覆写是透明的。
    /// 用户没改过的条目会在内置文本改进时自动跟随更新（因为覆写文件只保存差异项）。
    ///
    /// ── 关于"多方案"（2026-09-26 增补） ──
    /// 同一套槽位（<see cref="Keys"/>）可以有多份**方案**（<see cref="PromptPresets"/>）：
    /// 小说创作 / 学术论文 / 公文公告 / 通用写作。解析顺序是三层：
    ///
    ///     生效文本 = 当前方案下该条目的用户覆写
    ///               ?? 该方案所依据的内置方案的内置文本
    ///
    /// 也就是"方案决定基准，覆写决定偏差"。因为键名不变，代码里所有
    /// `AiPrompts.Task.Xxx` 的调用点一行都不用改——换方案对调用方完全透明。
    /// 自定义方案的"依据方案"由 <see cref="SystemPromptStore.ResolveBasePreset"/> 解析，
    /// 这样自定义方案里**没被改过的条目仍会跟随内置文本的改进**。
    /// </summary>
    public static class AiPrompts
    {
        // ==================================================================
        // 覆写层注入点
        // ==================================================================

        /// <summary>
        /// 用户覆写存储。由 MainWindow 在启动/切换配置目录时注入。为 null 时使用内置默认方案。
        /// </summary>
        public static SystemPromptStore? Store { get; set; }

        /// <summary>出厂默认方案：小说创作。存储不可用时一律回落到它。</summary>
        public const string DefaultPresetId = PromptPresets.IdNovel;

        /// <summary>当前生效的方案 Id。用户在「AI 设置 → 系统提示词」里切换。</summary>
        public static string ActivePresetId => Store?.ActivePresetId ?? DefaultPresetId;

        /// <summary>当前方案的显示名（内置名或自定义名）。</summary>
        public static string ActivePresetName => Store?.PresetName(ActivePresetId)
            ?? PromptPresets.Find(DefaultPresetId)?.Name
            ?? "默认";

        /// <summary>
        /// 可选的方案清单（内置在前，自定义在后），供设置页与提示显示。
        /// </summary>
        /// <param name="store">
        /// 取哪个存储的自定义方案。**设置页必须显式传自己手上的那个**（它同一时刻只编辑
        /// 一个存储），不能依赖 <see cref="Store"/>：后者是"当前生效"的全局引用，在
        /// 设置页打开期间可能因配置目录变更等原因被换掉，届时下拉会列出另一份方案清单。
        /// 传 null 才回落到全局，用于"只是显示一下"的场景。
        /// </param>
        public static IReadOnlyList<PresetOption> PresetOptions(SystemPromptStore? store = null)
        {
            var list = new List<PresetOption>();
            foreach (var d in PromptPresets.All)
                list.Add(new PresetOption(d.Id, d.Name, true, d.Description));

            var source = store ?? Store;
            if (source != null)
                foreach (var c in source.CustomPresets)
                    list.Add(new PresetOption(c.Id, c.Name, false,
                        $"基于「{PromptPresets.Find(c.BasedOn)?.Name ?? "通用写作"}」的自定义方案"));

            return list;
        }

        /// <summary>设置页下拉里的一项方案。</summary>
        public sealed record PresetOption(string Id, string Name, bool IsBuiltIn, string Description);

        /// <summary>
        /// 某个方案下某条目的**内置**文本（不含用户覆写）。
        /// 自定义方案会回溯到它所依据的内置方案——这正是"没改过的条目仍跟随内置改进"的实现。
        /// </summary>
        public static string DefaultFor(string presetId, string key)
        {
            var baseId = Store?.ResolveBasePreset(presetId) ?? PromptPresets.IdNovel;
            return PromptPresets.TextOf(baseId, key);
        }

        /// <summary>某个方案下某条目的生效文本（覆写优先）。设置页编辑时用它取当前值。</summary>
        public static string TextFor(string presetId, string key)
        {
            var fallback = DefaultFor(presetId, key);
            return Store?.Get(presetId, key, fallback) ?? fallback;
        }

        /// <summary>当前方案下某条目的生效文本。</summary>
        private static string Resolve(string key) => TextFor(ActivePresetId, key);

        /// <summary>
        /// 当前方案的上下文标签（参考资料块里的字段名）。随方案变，
        /// 否则论文方案的 system 里会冒出"### 主要人物设定"这种小说字段名。
        /// </summary>
        public static string Label(string labelKey)
        {
            var baseId = Store?.ResolveBasePreset(ActivePresetId) ?? DefaultPresetId;
            return PromptPresets.LabelOf(baseId, labelKey);
        }

        // ==================================================================
        // 条目键名：设置页、覆写文件、本类三者共用同一套键
        // ==================================================================

        public static class Keys
        {
            public const string Base = "Base";
            public const string CreativeOutput = "Output.Creative";
            public const string StructuredOutput = "Output.Structured";

            public const string Continue = "Task.Continue";
            public const string Polish = "Task.Polish";
            public const string Name = "Task.Name";
            public const string Character = "Task.Character";
            public const string Outline = "Task.Outline";
            public const string Background = "Task.Background";
            public const string ChapterOutline = "Task.ChapterOutline";
            public const string WriteStyle = "Task.WriteStyle";
            public const string Expand = "Task.Expand";
            public const string Chat = "Task.Chat";
        }

        // ==================================================================
        // 生效值（覆写优先）
        // ==================================================================

        /// <summary>① 身份层：所有功能的共同人设（随当前方案变）。</summary>
        public static string Base => Resolve(Keys.Base);

        /// <summary>④ 输出契约：要"直接产出可入稿正文"的功能用这套。</summary>
        public static string CreativeOutput => Resolve(Keys.CreativeOutput);

        /// <summary>④ 输出契约：结构化产出（大纲 / 人物 / 背景设定）用这套。</summary>
        public static string StructuredOutput => Resolve(Keys.StructuredOutput);

        /// <summary>③ 任务层：每个功能一段。每个方案都会给出全部 10 段。</summary>
        public static class Task
        {
            public static string Continue => Resolve(Keys.Continue);
            public static string Polish => Resolve(Keys.Polish);
            public static string Name => Resolve(Keys.Name);
            public static string Character => Resolve(Keys.Character);
            public static string Outline => Resolve(Keys.Outline);
            public static string Background => Resolve(Keys.Background);
            public static string ChapterOutline => Resolve(Keys.ChapterOutline);
            public static string WriteStyle => Resolve(Keys.WriteStyle);
            public static string Expand => Resolve(Keys.Expand);
            public static string Chat => Resolve(Keys.Chat);
        }

        // ==================================================================
        // 条目目录：设置页据此生成列表，不硬编码
        // ==================================================================

        /// <summary>设置页里的一条可编辑提示词（元数据）。</summary>
        /// <param name="Key">覆写文件中的键；所有方案共用同一套键</param>
        /// <param name="Group">分组显示名（身份 / 输出契约 / 任务说明）</param>
        /// <param name="Title">条目标题</param>
        /// <param name="Description">这条提示词管什么、什么时候会被用上</param>
        public sealed record EntryMeta(
            string Key,
            string Group,
            string Title,
            string Description);

        /// <summary>
        /// 全部可编辑槽位，顺序即设置页列表顺序。
        /// 注意这里**不含文本**——文本随方案变，用 <see cref="DefaultFor"/> / <see cref="TextFor"/> 取。
        /// </summary>
        public static IReadOnlyList<EntryMeta> Entries { get; } = new List<EntryMeta>
        {
            new(Keys.Base, "身份", "写作助手人设",
                "所有 AI 功能共用的人设与准则。改这里会同时影响续写、润色、大纲等全部功能；"
                + "不同方案的差别主要就在这一段。"),

            new(Keys.CreativeOutput, "输出契约", "创作类输出要求",
                "用于「续写」「润色」等需要直接产出成稿的功能。"),

            new(Keys.StructuredOutput, "输出契约", "结构化输出要求",
                "用于「命名」「对象设定」「大纲」等允许分条组织的功能。"),

            new(Keys.Continue, "任务说明", "续写",
                "点击 AI 面板「续写」时追加的任务说明。"),

            new(Keys.Polish, "任务说明", "润色",
                "点击 AI 面板「润色」时追加的任务说明。"),

            new(Keys.Name, "任务说明", "命名（人名生成）",
                "点击「人名生成」时使用。论文方案下对应概念与术语命名，公文方案下对应事项名称拟定。"),

            new(Keys.Character, "任务说明", "对象设定（人物设定）",
                "生成对象设定（首次生成时使用）。已有内容后再次点击会自动改用「扩展完善」。"),

            new(Keys.Outline, "任务说明", "全文大纲",
                "生成全文大纲（首次生成时使用）。"),

            new(Keys.Background, "任务说明", "背景设定",
                "生成背景设定（首次生成时使用）。论文方案下对应研究背景与理论基础。"),

            new(Keys.ChapterOutline, "任务说明", "章节大纲",
                "生成章节大纲（首次生成时使用）。"),

            new(Keys.WriteStyle, "任务说明", "文风设定",
                "生成全书统一的叙述视角、语言风格与禁忌（首次生成时使用）。"),

            new(Keys.Expand, "任务说明", "扩展已有内容",
                "在已有大纲 / 对象 / 背景 / 章节大纲的基础上修改完善时使用，替代对应的首次生成说明。"),

            new(Keys.Chat, "任务说明", "通用写作请求",
                "「万能聊天」使用，作者直接描述需求时的任务说明。"),
        };

        /// <summary>按分组归类，供设置页做分组显示。</summary>
        public static IEnumerable<(string Group, List<EntryMeta> Items)> EntriesByGroup()
        {
            var order = new List<string>();
            var map = new Dictionary<string, List<EntryMeta>>(StringComparer.Ordinal);
            foreach (var entry in Entries)
            {
                if (!map.TryGetValue(entry.Group, out var list))
                {
                    list = new List<EntryMeta>();
                    map[entry.Group] = list;
                    order.Add(entry.Group);
                }
                list.Add(entry);
            }
            foreach (var g in order) yield return (g, map[g]);
        }

        // ==================================================================
        // 内置默认文本（出厂值）。用户点"恢复默认"就是回到这里。
        // ⚠ 这 13 段是**小说创作方案**的出厂文本，被 PromptPresets.Novel 直接引用，
        //   是它的唯一真源。其余三个方案（学术 / 公文 / 通用）的文本写在 PromptPresets 里。
        //   不要随意改动这些文本——没被用户覆写的条目会立刻跟随变化。
        // ==================================================================

        public static class Defaults
        {
            /// <summary>① 身份层。恒定人设，所有请求共享，且刻意保持稳定以命中 prompt 缓存。</summary>
            public const string Base = """
                你是一位资深的中文小说创作助手，长期与同一位作者合作。你的职责是帮他写出可
                直接放进书稿的文字——不是提建议，不是写范文，而是生产成品。

                与作者协作时，你遵循以下准则：

                【文笔】
                - 用具体的感官细节（动作、对话、环境、身体反应）承载情绪，不要直接陈述
                  "他很愤怒""场面很紧张"这类结论。
                - 句式长短交错。该短的地方短到三四字一句，该铺开的地方用长句推进节奏。
                - 克制形容词和成语堆砌。一个精准的动词胜过三个漂亮的形容词。
                - 避免"不禁""竟然""仿佛""宛如"这类高频虚词反复出现。

                【连贯】
                - 严格遵守已给出的人物设定、世界观与既定情节，不自行推翻或另起设定。
                - 保持与前文一致的人称、时态、语气和称谓。
                - 涉及已有角色时，行为与语言必须贴合其既定性格，不得为推进情节而扭曲人物。

                【判断】
                - 作者的要求优先于你的偏好。他指定的方向、风格、篇幅都要照办。
                - 设定之间出现矛盾时，选择更有利于叙事张力的那个，并在正文后单独说明。
                - 信息不足时，不要编造关键设定（如人物姓名、关键道具、地名），改为在正文后
                  用一句话点出需要作者补充什么。

                【边界】
                - 不写露骨的色情、暴力细节。
                - 不使用"作为AI""以下是我为你写的"这类元叙述。
                """;

            /// <summary>④ 输出契约：需要"直接产出可入稿正文"的功能共用。</summary>
            public const string CreativeOutput = """
                【输出要求】
                - 直接输出正文本身。不要写开场白、不要总结、不要解释你做了什么。
                - 不要用 Markdown 标题、代码块或加粗来包装正文。
                - 除对话外，正文中不要出现任何说明性文字。
                """;

            /// <summary>④ 输出契约：结构化产出（大纲 / 人物 / 背景设定）：允许分条，但仍不许寒暄。</summary>
            public const string StructuredOutput = """
                【输出要求】
                - 直接输出结果，不要开场白，不要解释你做了什么。
                - 可以用分条、序号或小标题组织内容，但不要用代码块。
                - 不要重复我已经给你的设定原文，只输出新增/修改后的完整结果。
                """;

            /// <summary>③ 任务层：每个功能一段。</summary>
            public static class Task
            {
                public const string Continue = """
                    【本次任务】续写
                    接着作者提供的正文往下写。你的产出会被直接追加到章节末尾。

                    - 从断句处自然接续，不要重复已有内容的结尾。
                    - 保持原有的叙事视角、语气节奏与段落长度习惯。
                    - 推进行情而非原地打转：让这一段发生一些具体的事。
                    - 长度控制在 400~800 字，除非作者另有要求。
                    """;

                public const string Polish = """
                    【本次任务】润色
                    作者提供了一段已成稿的正文，你的产出会整段替换原文。

                    - 保留原有的情节、信息量、人物言行和段落结构，只提升表达。
                    - 不做增删情节的改动。如果原文有逻辑漏洞，最多做最小幅度的衔接修补。
                    - 尊重作者原本的语言风格，不要改成你自己的腔调。
                    - 输出完整的修改后正文，不要标注哪里改了、不要写修改说明。
                    """;

                public const string Name = """
                    【本次任务】角色命名
                    为小说生成角色名，产出会追加到章节内容中供作者挑选。

                    - 名字要贴合小说的时代背景与世界观（古代不用现代感的名字，科幻不用乡土味的
                      名字）。
                    - 同一批名字之间风格统一，且彼此容易区分（避免一堆同音、同字的名字）。
                    - 每个名字附一行简短说明：含义、气质，或它适合什么样的角色。
                    - 输出 8~12 个候选，按适合度排序。
                    """;

                public const string Character = """
                    【本次任务】人物设定
                    产出会整段写入项目的"主要人物设定"字段，成为后续所有创作的依据。

                    - 每个角色给出：姓名、年龄、性别、外貌、性格、背景故事、在故事中的定位。
                    - 性格要写成可执行的言行倾向（"遇事先动手再解释"），不要写"善良坚强"这类
                      没有戏剧价值的标签。
                    - 背景故事要埋下能在情节中回收的钩子，而不是履历罗列。
                    - 主要角色的性格之间要形成张力（互补、对立或误解），便于后续情节展开。
                    """;

                public const string Outline = """
                    【本次任务】全文大纲
                    产出会整段写入项目的"全文大纲"字段，作为整部作品的骨架。

                    - 包含：核心设定、主要情节线、关键冲突、转折点、高潮与结局。
                    - 情节推进要有因果，后一个事件由前一个事件引出，不要罗列互不相干的桥段。
                    - 冲突要逐级升级，中段不能松懈。
                    - 结局需回应开篇埋下的主要悬念。
                    """;

                public const string Background = """
                    【本次任务】世界观与背景设定
                    产出会整段写入项目的"主要背景设定"字段。严格按以下五类逐条列出，
                    每条简洁明确、有具体细节支撑：

                    1. 时代背景（年代、历史阶段）
                    2. 地理环境（主要场景、地域特征）
                    3. 社会体系（政治、经济、阶层）
                    4. 文化风俗（宗教、信仰、禁忌）
                    5. 特殊设定（若涉及魔法/科技体系，说明规则与限制）

                    设定之间必须逻辑自洽。规则类设定一定要写出"代价"或"限制"——
                    没有限制的设定无法产生冲突。不要天马行空，每条都要能落到具体场景里。
                    """;

                public const string ChapterOutline = """
                    【本次任务】章节大纲
                    产出会整段写入项目的"章节大纲"字段，用来规划每一章的推进。

                    - 逐章列出：章节标题、主要事件、推进方向、本章结束时读者的悬念。
                    - 标注每章的关键转折与出场人物。
                    - 保证章与章之间有递进关系，避免连续几章做同一件事。
                    """;

                public const string WriteStyle = """
                    【本次任务】文风设定
                    产出会整段写入项目的"文风设定"字段，供后续所有正文创作遵循。

                    - 叙述视角与人称（第一/第三人称，限知还是全知）。
                    - 语言质地：偏口语还是书面，句子大致长什么样，用词的疏密。
                    - 节奏与场景处理：对话与描写的配比、场面切换的偏好。
                    - 禁忌：明确列出要避免的写法（如滥用形容词、频繁心理独白、网络流行语等）。

                    写成写作时能直接对照执行的条目，不要写成"文笔优美"这类空泛评价。
                    若作者已提供作品片段或偏好描述，以它为准提炼，不要另起一套风格。
                    """;

                public const string Expand = """
                    【本次任务】在已有内容上扩展完善
                    作者已提供了一版结果，要求你在其基础上修改，而非另起一稿。

                    - 保留原版中仍然成立的部分，只改动需要改的地方。
                    - 吸收作者本次提出的新要求，但不要推翻原版的整体基调。
                    - 输出修改后的完整结果（不是补丁，也不是差异说明）。
                    """;

                public const string Chat = """
                    【本次任务】通用写作请求
                    作者会直接描述他要什么。按他的原话办。

                    - 这是一次**连续对话**：前几轮你说过什么、作者要求过什么，都在上文里。
                      接着往下说就行 —— 不要重新自我介绍，不要把上一轮已经给过的内容再抄一遍。
                    - 作者的当前要求若与前文冲突，以最新的为准，并简单说一句你改了什么。
                    - 如果他要的是成稿内容（写一段、改一段、补一段），直接给可入稿的正文。
                    - 如果他问的是创作问题（某段怎么处理更好、这个人物该怎么立），
                      给出判断和具体做法，必要时附一小段示例，但不要写成长篇评论。
                    - 他给的上下文设定是你的依据，不要当成需要评论的文本。
                    """;
            }
        }

        // ==================================================================
        // ④ 上下文层：把项目设定组织成结构清晰、带权重标记的块。
        // ==================================================================

        /// <summary>
        /// 组装 system 提示词（压平成一段纯文本）。
        /// 新代码建议用 <see cref="BuildSections"/> —— 它能保住分段，让 prompt caching 生效。
        /// </summary>
        /// <param name="task">任务层说明（<see cref="Task"/> 中的属性）。</param>
        /// <param name="contextBlock">由 <see cref="BuildContextBlock"/> 生成的项目设定块，可为空。</param>
        /// <param name="outputContract">输出契约，默认使用 <see cref="CreativeOutput"/>。</param>
        public static string Build(string task, string? contextBlock = null, string? outputContract = null)
            => BuildSections(task, contextBlock, null, outputContract).Flatten();

        /// <summary>
        /// 组装**分段**的 system 提示词 —— 这是让 Anthropic 的 prompt caching 真正生效的前提。
        ///
        /// 分段依据是**这一块会不会跨请求复用**，而不是内容类别：
        ///   ① 身份 + 项目设定（含 AI 记忆）：只要还在同一个项目里就基本不变 → 打缓存断点
        ///   ② 勾选章节正文：作者勾哪几章、写了多少，每次都可能不同 → 不打
        ///   ③ 任务 + 输出契约：单个只有几百字，通常够不到服务端的最小缓存长度 → 不打
        ///
        /// ⚠ 为什么只给①打标记：打 cache_control 意味着**要写缓存**，而写入按 1.25 倍计价。
        ///   给一个从不复用的前缀打标记就是白亏 25%。① 是唯一"真的会重复"的大块
        ///   （长篇项目里五项设定动辄数千字，是 system 里最大的部分）。
        ///
        /// ⚠ 顺序刻意保持与改造前一致（身份 → 设定 → 章节 → 任务 → 契约）。
        ///   把"稳定的往前挪"确实能多缓存一段，但会改掉用户已经调好的提示词语义，
        ///   不值得为那点收益冒险。
        /// </summary>
        public static SystemPrompt BuildSections(
            string task, string? stableContext = null, string? volatileContext = null, string? outputContract = null)
        {
            var prompt = new SystemPrompt(Array.Empty<SystemPromptPart>());

            var head = new StringBuilder();
            head.AppendLine(Base);
            if (!string.IsNullOrWhiteSpace(stableContext))
            {
                head.AppendLine();
                head.AppendLine(stableContext.Trim());
            }
            prompt = prompt.Append(head.ToString().Trim(), cacheable: true);

            if (!string.IsNullOrWhiteSpace(volatileContext))
                prompt = prompt.Append(volatileContext.Trim(), cacheable: false);

            var tail = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(task))
                tail.AppendLine(task.Trim());
            tail.Append((outputContract ?? CreativeOutput).Trim());
            prompt = prompt.Append(tail.ToString().Trim(), cacheable: false);

            return prompt;
        }

        /// <summary>
        /// 把项目设定组织成"参考资料"块。
        ///
        /// 关键点：明确标注这是**参考资料**而不是待处理文本 —— 这正是原实现把设定和正文
        /// 平级拼在一起时最大的问题，模型分不清哪段该改、哪段该参考。
        /// </summary>
        /// <param name="fullOutline">全文大纲</param>
        /// <param name="chapterOutline">章节大纲</param>
        /// <param name="characters">主要人物设定</param>
        /// <param name="background">主要背景设定</param>
        /// <param name="writingStyle">文风设定</param>
        /// <param name="memory">AI 记忆（累积的写作偏好）</param>
        public static string BuildContextBlock(
            string? fullOutline,
            string? chapterOutline,
            string? characters,
            string? background,
            string? writingStyle,
            string? memory = null)
        {
            var parts = new List<string>();

            Add(parts, Label(PromptPresets.LabelKeys.Outline), fullOutline);
            Add(parts, Label(PromptPresets.LabelKeys.ChapterOutline), chapterOutline);
            Add(parts, Label(PromptPresets.LabelKeys.Characters), characters);
            Add(parts, Label(PromptPresets.LabelKeys.Background), background);
            Add(parts, Label(PromptPresets.LabelKeys.WritingStyle), writingStyle);
            Add(parts, Label(PromptPresets.LabelKeys.Memory), memory);

            if (parts.Count == 0) return "";

            var sb = new StringBuilder();
            // 开头说明也随方案变——论文/公文方案下的"设定"不是一个语义
            sb.AppendLine(Label(PromptPresets.LabelKeys.ReferenceIntro));
            sb.AppendLine();
            sb.Append(string.Join("\n\n", parts));
            return sb.ToString();

            static void Add(List<string> list, string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add($"### {label}\n{value.Trim()}");
            }
        }

        /// <summary>
        /// 组织 user 消息中的"待处理文本"部分。
        /// 用显式分隔标记把作者要你处理的内容框出来，与参考资料彻底分开。
        /// </summary>
        /// <param name="label">区段标题，如"待续写的正文"</param>
        /// <param name="content">正文内容</param>
        public static string Section(string label, string? content) =>
            string.IsNullOrWhiteSpace(content)
                ? ""
                : $"【{label}】\n{content.Trim()}\n";

        /// <summary>
        /// 组织"相关章节"参考内容（作者在 AI 面板里勾选的其他章节）。
        /// </summary>
        public static string BuildRelatedChapters(IEnumerable<(int Number, string Title, string Content)> chapters)
        {
            var parts = new List<string>();
            foreach (var (number, title, content) in chapters)
            {
                if (!string.IsNullOrWhiteSpace(content))
                    parts.Add($"### 第{number}章 {title}\n{content.Trim()}");
            }
            if (parts.Count == 0) return "";

            return "【参考章节】作者提供的前文，用于保持连贯性。同样不需要你处理。\n\n"
                   + string.Join("\n\n", parts);
        }
    }
}
