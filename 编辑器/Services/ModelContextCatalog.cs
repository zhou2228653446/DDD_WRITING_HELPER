using System;

namespace 编辑器.Services
{
    /// <summary>
    /// 上下文窗口（token）的取值口径。
    ///
    /// ★ 2026-09-26 起改为**统一按 200K 做**，不再给每个模型分档。
    ///
    /// 为什么：窗口大小是压缩阈值的分母。原来按模型表分档（Claude 200K、DeepSeek/GLM/Qwen 128K、
    /// 初代 GPT-4 8K…），但表里那些小值全是**猜的**，而且实际使用的模型窗口都 ≥200K
    /// （Claude 200K、o3 200K、Gemini 1M、GPT-4.1 1M…）。按猜小的值算阈值，唯一后果是
    /// **过早压缩**：明明还有大半个窗口没用，就开始把对话摘要掉、丢上下文 —— 白白损失能力，
    /// 却换不来任何安全收益。所以现在一律取 200K 作下限：
    ///
    /// <code>生效窗口 = max(已知大窗口, AssumedContextWindow)</code>
    ///
    /// 已知**明显更大**的模型（Gemini / GPT-4.1 / qwen-long，1M）仍按各自的大窗口算 ——
    /// 那是向上取值，多出来的空间不用白不用，也不存在撞窗风险。
    ///
    /// ⚠ 代价（已知并接受）：哪天真的接了一个窗口 &lt;200K 的模型，阈值会算得偏大。
    ///   表现**不是报错而是"压得太晚"**，可能撞服务端 400。真遇到时在
    ///   <see cref="Resolve"/> 的 <c>userOverride</c>（设置页的上下文窗口）里显式填真实值即可。
    ///
    /// 另外要清楚：这张表/这个假定量只是"第一轮请求之前"的依据。一旦拿到一次真实 usage
    /// （provider 返回的 input_tokens），后续判断就以真实值为准 ——
    /// 那才是唯一准确的口径（见 <see cref="CompactPolicy.Decide"/> 的 tokenOverride）。
    /// </summary>
    public static class ModelContextCatalog
    {
        /// <summary>
        /// 统一假定的上下文窗口（200K）。所有窗口都**不会低于**这个值。
        ///
        /// 对应 ZCode 的 <c>AutoCompactPolicyConfig.contextWindow</c> 默认值 —— 它也只服务
        /// 200K 窗口的模型，本项目的差异在于把这个假定当成了全局下限。
        /// </summary>
        public const int AssumedContextWindow = 200_000;

        /// <summary>无匹配时的兜底窗口。<see cref="AssumedContextWindow"/> 的别名，保留旧名以免调用点散落。</summary>
        public const int FallbackContextWindow = AssumedContextWindow;

        /// <summary>窗口的最小可用值，防止用户把配置填成 0 之类的值把阈值算负。</summary>
        public const int MinContextWindow = 2_000;

        /// <summary>
        /// 解析某个模型名实际使用的上下文窗口 =
        /// <c>max(已知大窗口, <see cref="AssumedContextWindow"/>)</c>。
        /// </summary>
        /// <param name="model">模型名，大小写不敏感；允许带 <c>provider/</c> 前缀（如硅基流动）。</param>
        /// <param name="userOverride">用户在设置里显式填写的值，**优先于一切**（含 200K 下限）。</param>
        public static int Resolve(string? model, int? userOverride = null)
        {
            if (userOverride is > 0)
                return Math.Max(MinContextWindow, userOverride.Value);

            int known = Lookup(model) ?? 0;
            return Math.Max(known, AssumedContextWindow);
        }

        /// <summary>
        /// 该模型是否有"已知且明显更大"的窗口（1M 级）。用于界面提示取值来源。
        /// ⚠ 返回 false 不代表窗口未知 —— 只代表它走 200K 的统一假定。
        /// </summary>
        public static bool IsKnown(string? model) => Lookup(model).HasValue;

        /// <summary>
        /// 内置表里该模型**原始登记**的窗口；无匹配返回 null。
        ///
        /// ⚠ 这是表里的原值，不是生效值 —— 生效值一律过 <see cref="Resolve"/>（下限 200K）。
        ///   表里刻意只留"明显大于 200K"的条目，见 <see cref="LargerWindowRules"/>。
        /// </summary>
        public static int? Lookup(string? model)
        {
            var name = Normalize(model);
            if (name.Length == 0) return null;

            foreach (var rule in LargerWindowRules)
            {
                if (name.Contains(rule.Keyword, StringComparison.Ordinal))
                    return rule.Window;
            }
            return null;
        }

        private static string Normalize(string? model) =>
            (model ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>
        /// 关键词 → 窗口，**只登记窗口明显大于 200K 的模型**。
        ///
        /// 其余常见模型（Claude / o 系 / DeepSeek / GLM / Qwen / Kimi / 豆包 / 文心 / 混元 /
        /// Llama / Mistral / MiniMax …，公开文档里的常见值为 128K~200K）一律按
        /// <see cref="AssumedContextWindow"/> 处理，不需要也不应该出现在这里 ——
        /// 登记了也会被下限抬平，只是噪音。
        ///
        /// **顺序有意义**：先匹配到的先用，所以更具体的规则要排在前面
        /// （例如 <c>qwen-long</c> 必须排在 <c>qwen</c> 之类之前）。
        /// </summary>
        private static readonly (string Keyword, int Window)[] LargerWindowRules =
        {
            // ── OpenAI ────────────────────────────────────────────────
            ("gpt-4.1", 1_000_000),

            // ── Google ────────────────────────────────────────────────
            ("gemini", 1_000_000),

            // ── 阿里通义 ──────────────────────────────────────────────
            ("qwen-long", 1_000_000),  // 长文本专用版
        };
    }
}
