namespace 编辑器.Services
{
    /// <summary>
    /// 按任务给的一组采样参数。
    ///
    /// 为什么要有它：**"AI 味"首先是采样问题，其次才是提示词问题。**
    /// 之前全项目只有 Temperature 一个旋钮，续写用 0.4~0.7 —— 温度低意味着模型
    /// 每次都挑"最可能的下一句"，而最可能的下一句往往就是套话。同时没有任何
    /// 重复惩罚，同一个词在同一章里可以反复出现。
    ///
    /// 两条硬约束：
    ///   1. 温度与惩罚必须同时调。只提温度不加惩罚，会把"套话"换成"胡话"。
    ///   2. 审稿/结构化任务必须保持低温度——那里要的是稳定，不是文采。
    /// </summary>
    public static class SamplingPresets
    {
        /// <summary>一组采样参数。</summary>
        public readonly record struct Setting(
            double Temperature, double? TopP, double Frequency, double Presence);

        /// <summary>任务 → 参数。任务键与 <see cref="AiPrompts.Keys"/> 保持一致。</summary>
        public static Setting For(string? task)
        {
            return (task ?? "").Trim().ToLowerInvariant() switch
            {
                // 创作类：要的是"不像模型"，宁可略散
                "continue" or "expand" or "character" or "background" or "write_style"
                    => new Setting(0.95, 0.95, 0.25, 0.15),

                // 润色：骨架已经定了，只需要换表达，别把语气改飞
                "polish" => new Setting(0.75, 0.90, 0.10, 0.00),

                // 结构化：大纲 / 起名 / 设定集，稳定优先
                "outline" or "chapter_outline" or "name" or "setting_book"
                    => new Setting(0.70, 0.95, 0.10, 0.00),

                // 审稿：要的是可复现的判定，不给发挥空间
                "review" => new Setting(0.25, 0.90, 0.00, 0.00),

                // 自由问答：介于两者之间
                "chat" => new Setting(0.80, 0.95, 0.10, 0.05),

                _ => new Setting(0.85, 0.95, 0.15, 0.10),
            };
        }

        /// <summary>
        /// 旧版参数（改造前）：创意任务 0.7、审稿 0.3，没有 top_p、没有任何重复惩罚。
        /// 只给 A/B 对照用——留一个能"回到从前"的开关，才能证明改的这几行真的有差别，
        /// 而不是拿不同章节、不同心情的产出互相比。
        /// </summary>
        public static Setting Legacy(string? task)
        {
            var t = (task ?? "").Trim().ToLowerInvariant();
            return t == "review" ? new Setting(0.30, null, 0, 0) : new Setting(0.70, null, 0, 0);
        }

        /// <summary>链式版本：`new CompletionOptions { ... }.SampleFor("continue")`。</summary>
        public static CompletionOptions SampleFor(this CompletionOptions options, string? task)
            => Apply(options, task);

        /// <summary>链式版本（直接给一组参数，A/B 对照时用）。</summary>
        public static CompletionOptions SampleFor(this CompletionOptions options, Setting setting)
        {
            options.Temperature = setting.Temperature;
            options.TopP = setting.TopP;
            options.FrequencyPenalty = setting.Frequency;
            options.PresencePenalty = setting.Presence;
            return options;
        }

        /// <summary>把某个任务的采样参数套到 options 上（就地修改并返回，便于链式调用）。</summary>
        public static CompletionOptions Apply(CompletionOptions options, string? task)
            => Apply(options, For(task));

        /// <summary>直接套一组参数（A/B 对照时用）。</summary>
        public static CompletionOptions Apply(CompletionOptions options, Setting setting)
        {
            options.Temperature = setting.Temperature;
            options.TopP = setting.TopP;
            options.FrequencyPenalty = setting.Frequency;
            options.PresencePenalty = setting.Presence;
            return options;
        }
    }
}
