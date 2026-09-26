using System;

namespace 编辑器.Services
{
    /// <summary>
    /// 模型 → 上下文窗口（token）的查表。
    ///
    /// ★ 对应 ZCode 的 AutoCompactPolicyConfig.contextWindow（默认 200_000）。
    ///
    /// 窗口大小是**压缩阈值的分母**，估小了会过早压缩（浪费钱），估大了会撞服务端 400
    /// （硬失败）。两者相权，这里一律**取保守值**：拿不准就退回
    /// <see cref="FallbackContextWindow"/>，并且允许用户在设置里显式覆盖。
    ///
    /// 另外要清楚：这张表只是"第一轮请求之前"的依据。一旦拿到一次真实 usage
    /// （provider 返回的 input_tokens），后续判断就以真实值为准 ——
    /// 那才是唯一准确的口径（见 <see cref="CompactPolicy.Decide"/> 的 tokenOverride）。
    /// </summary>
    public static class ModelContextCatalog
    {
        /// <summary>无匹配时的兜底窗口。现代主流模型的常见下限。</summary>
        public const int FallbackContextWindow = 128_000;

        /// <summary>窗口的最小可用值，防止用户把配置填成 0 之类的值把阈值算负。</summary>
        public const int MinContextWindow = 2_000;

        /// <summary>
        /// 解析某个模型名的上下文窗口。
        /// </summary>
        /// <param name="model">模型名，大小写不敏感；允许带 <c>provider/</c> 前缀（如硅基流动）。</param>
        /// <param name="userOverride">用户在设置里显式填写的值，优先于内置表。</param>
        public static int Resolve(string? model, int? userOverride = null)
        {
            if (userOverride is > 0)
                return Math.Max(MinContextWindow, userOverride.Value);

            var name = Normalize(model);
            if (name.Length == 0) return FallbackContextWindow;

            foreach (var rule in Rules)
            {
                if (name.Contains(rule.Keyword, StringComparison.Ordinal))
                    return rule.Window;
            }

            return FallbackContextWindow;
        }

        /// <summary>返回该窗口是否来自内置表（用于界面提示"这个值是我们猜的"）。</summary>
        public static bool IsKnown(string? model)
        {
            var name = Normalize(model);
            if (name.Length == 0) return false;

            foreach (var rule in Rules)
            {
                if (name.Contains(rule.Keyword, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>内置表里该模型的窗口；无匹配返回 null（区别于"兜底值"）。</summary>
        public static int? Lookup(string? model)
        {
            var name = Normalize(model);
            if (name.Length == 0) return null;

            foreach (var rule in Rules)
            {
                if (name.Contains(rule.Keyword, StringComparison.Ordinal))
                    return rule.Window;
            }
            return null;
        }

        private static string Normalize(string? model) =>
            (model ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>
        /// 关键词 → 窗口。**顺序有意义**：先匹配到的先用，所以更具体的规则要排在前面
        /// （例如 <c>qwen-long</c> 必须排在 <c>qwen</c> 之前）。
        /// 数量取整便于阅读，均为各服务商公开文档中的常见值。
        /// </summary>
        private static readonly (string Keyword, int Window)[] Rules =
        {
            // ── Anthropic ──────────────────────────────────────────────
            ("claude", 200_000),

            // ── OpenAI ────────────────────────────────────────────────
            ("gpt-4o", 128_000),
            ("gpt-4.1", 1_000_000),
            ("gpt-4-turbo", 128_000),
            ("gpt-4", 8_192),          // 初代 GPT-4 只有 8K，这一条必须精确匹配在前
            ("gpt-3.5", 16_385),
            ("o1", 200_000),
            ("o3", 200_000),
            ("o4", 200_000),

            // ── Google ────────────────────────────────────────────────
            ("gemini", 1_000_000),

            // ── DeepSeek ──────────────────────────────────────────────
            ("deepseek", 128_000),

            // ── 智谱 ──────────────────────────────────────────────────
            ("glm", 128_000),

            // ── 阿里通义 ──────────────────────────────────────────────
            ("qwen-long", 1_000_000),  // 长文本专用版，须在 qwen 之前
            ("qwen", 128_000),
            ("qwq", 128_000),

            // ── 月之暗面 ──────────────────────────────────────────────
            ("moonshot", 128_000),
            ("kimi", 128_000),

            // ── 字节豆包 ──────────────────────────────────────────────
            ("doubao", 128_000),

            // ── 百度文心 ──────────────────────────────────────────────
            ("ernie", 128_000),

            // ── MiniMax ───────────────────────────────────────────────
            ("minimax", 200_000),
            ("abab", 200_000),

            // ── 零一万物 ──────────────────────────────────────────────
            ("yi-", 200_000),

            // ── 科大讯飞 ──────────────────────────────────────────────
            ("spark", 128_000),

            // ── 腾讯混元 ──────────────────────────────────────────────
            ("hunyuan", 128_000),

            // ── 硅基流动 / 开源系 ─────────────────────────────────────
            ("llama-4", 128_000),
            ("llama-3", 128_000),
            ("mistral", 128_000),
            ("qwen3", 128_000),
        };
    }
}
