using System;
using System.Collections.Generic;

namespace 编辑器.Services
{
    /// <summary>微压缩不生效的原因，用于日志与界面提示（取值与 ZCode 的 decision.reason 一致）。</summary>
    public enum MicroCompactReason
    {
        /// <summary>被配置关掉了。</summary>
        Disabled,

        /// <summary>没到触发条件（token 未达阈值，也没闲置足够久）。</summary>
        NotTriggered,

        /// <summary>没有任何可清理的候选内容。</summary>
        NoCandidates,

        /// <summary>候选都在"最近若干条"的保护范围内，没得清。</summary>
        NothingToClear,

        /// <summary>能省的量低于最小门槛，不值得动（回滚，保持原样）。</summary>
        BelowMinSavings,

        /// <summary>已执行清理。</summary>
        Applied
    }

    public sealed class MicroCompactOptions
    {
        public bool Enabled { get; init; } = true;

        /// <summary>显式阈值；不填则用 <see cref="AutoCompactThreshold"/> 按 ZCode 的公式推算。</summary>
        public int? ThresholdTokens { get; init; }

        /// <summary>全量压缩的阈值，用于推算微压缩阈值（先于全量动手）。</summary>
        public int? AutoCompactThreshold { get; init; }

        /// <summary>最近这么多条 AI 回复不动（ZCode 默认保留最近 5 条工具结果）。</summary>
        public int KeepRecentReplies { get; init; } = CompactPolicy.MicroCompactKeepRecentItems;

        /// <summary>省不下这么多 token 就不折腾。</summary>
        public int MinTokenSavings { get; init; } = CompactPolicy.MicroCompactMinTokenSavings;

        /// <summary>闲置这么久也值得顺手清一次（ZCode 默认 60 分钟）。</summary>
        public int IdleThresholdMinutes { get; init; } = CompactPolicy.MicroCompactIdleThresholdMinutes;

        /// <summary>最后一次活动时间；与当前时间之差超过闲置阈值也会触发。</summary>
        public DateTimeOffset? LastActivityUtc { get; init; }

        public DateTimeOffset? NowUtc { get; init; }
    }

    public readonly record struct MicroCompactResult(
        IReadOnlyList<ChatMessage> Messages,
        MicroCompactReason Reason,
        int ClearedCount,
        int TokensBefore,
        int TokensAfter)
    {
        public int TokensSaved => Math.Max(0, TokensBefore - TokensAfter);
        public bool Applied => Reason == MicroCompactReason.Applied;
    }

    /// <summary>
    /// **微压缩**：不调用任何模型、不改变消息结构，只把"旧的大块内容"换成占位符。
    ///
    /// ★ 移植自 ZCode（github.com/zai-org/ZCode）：
    ///   apps/zcode-cli/packages/core/src/compact/microcompact.ts
    ///
    /// ZCode 的原始动机是"旧的工具执行结果" —— 那些 Read/Bash 的输出又大又只有一时用处，
    /// 却要在之后每一轮请求里重复发送。它们的共同特征是：**体积大、价值已过期、
    /// 内容本身别处还留存着**。清掉它们只留一行占位符，上下文立刻瘦一大圈，
    /// 而对话结构（谁说了什么、说了几轮）完全没变。
    ///
    /// 在本项目里，与之对应的就是**旧轮次里 AI 生成的长回复**：
    /// "把这段扩写成 3000 字"之后，那 3000 字会跟着之后每一次请求重发一遍，
    /// 而它其实已经写进章节正文了。清掉它，AI 并不会有实质损失。
    ///
    /// 逐条照搬的规则：
    ///
    /// 1. **两级出手**：先在"全量压缩阈值 × 0.9"这个位置做微压缩（本地、免费），
    ///    压不动了才动用模型做全量压缩（要花钱）。ZCode:
    ///    <c>buildDefaultMicrocompactThreshold</c>。
    ///
    /// 2. **保留最近 N 条**（默认 5）—— 正在聊的内容绝不能动。
    ///
    /// 3. **最小收益门槛**（默认 256 token）：省不下这么多就整体回滚，保持原样。
    ///    避免为了省 20 个 token 把历史改得面目全非。
    ///
    /// 4. **幂等**：已经清理过的条目会被识别出来跳过，不重复处理、也不重复计数。
    ///
    /// 5. **闲置也会触发**（默认 60 分钟）：ZCode 认为"放了很久的上下文"同样值得
    ///    顺手清一次，因为它多半已经不在当前话题上了。
    ///
    /// 与 ZCode 的一处**有意不同**：ZCode 清的是 tool 消息，本项目清的是 assistant
    /// 消息 —— 消息本身一个不删、role 一个不改，只替换 content。
    /// 因此"首条必须是 user""同角色不能连续"这类协议约束天然不会被破坏，
    /// 不需要 ZCode 那套"裁剪后补 marker"的补救逻辑。
    /// </summary>
    public static class MicroCompactor
    {
        /// <summary>替换后的占位文案。要一眼看出"这里有东西被省掉了"，而不是像正文。</summary>
        public const string ClearedPlaceholder = "[较早的回复内容已省略]";

        public static MicroCompactResult Compact(
            IReadOnlyList<ChatMessage>? messages,
            MicroCompactOptions? options = null)
        {
            options ??= new MicroCompactOptions();

            var source = messages ?? Array.Empty<ChatMessage>();
            var copy = new List<ChatMessage>(source.Count);
            foreach (var m in source)
                copy.Add(new ChatMessage(m.Role, m.Content));

            int tokensBefore = TokenEstimator.EstimateAll(Contents(copy));

            if (!options.Enabled)
                return new MicroCompactResult(copy, MicroCompactReason.Disabled, 0, tokensBefore, tokensBefore);

            int? threshold = ResolveThreshold(options);
            if (!ShouldTrigger(options, threshold, tokensBefore))
                return new MicroCompactResult(copy, MicroCompactReason.NotTriggered, 0, tokensBefore, tokensBefore);

            // 候选 = 旧的 AI 回复。倒着收集，依次跳过：已清理的、最近 N 条保护的。
            var indices = new List<int>();
            int keep = Math.Max(1, options.KeepRecentReplies);
            int skippedRecent = 0;

            for (int i = copy.Count - 1; i >= 0; i--)
            {
                if (!IsCompactableReply(copy[i])) continue;

                if (skippedRecent < keep)
                {
                    skippedRecent++;
                    continue;
                }

                indices.Add(i);
            }

            if (indices.Count == 0)
            {
                // 区分两种"没得清"：压根没有可清理的，还是有但全在保护区内。
                bool anyCandidate = false;
                foreach (var m in copy)
                {
                    if (IsCompactableReply(m)) { anyCandidate = true; break; }
                }

                return new MicroCompactResult(
                    copy,
                    anyCandidate ? MicroCompactReason.NothingToClear : MicroCompactReason.NoCandidates,
                    0, tokensBefore, tokensBefore);
            }

            foreach (int i in indices)
                copy[i] = new ChatMessage(copy[i].Role, ClearedPlaceholder);

            int tokensAfter = TokenEstimator.EstimateAll(Contents(copy));
            int saved = tokensBefore - tokensAfter;

            // 省不下多少就别改（ZCode 的 DEFAULT_MICROCOMPACT_MIN_TOKEN_SAVINGS）
            if (saved < Math.Max(1, options.MinTokenSavings))
            {
                return new MicroCompactResult(
                    source.Count == 0 ? Array.Empty<ChatMessage>() : Clone(source),
                    MicroCompactReason.BelowMinSavings, 0, tokensBefore, tokensBefore);
            }

            return new MicroCompactResult(copy, MicroCompactReason.Applied, indices.Count, tokensBefore, tokensAfter);
        }

        /// <summary>这条消息的内容是不是"已被清理过"（用于幂等）。</summary>
        public static bool IsCleared(string? content) =>
            string.Equals(content, ClearedPlaceholder, StringComparison.Ordinal);

        /// <summary>
        /// 是不是可以清理的条目。当前只有 AI 的旧回复 —— 作者自己写的提问
        /// 通常很短，而且是理解后续对话的唯一线索，一律不动。
        /// </summary>
        private static bool IsCompactableReply(ChatMessage? m)
        {
            if (m == null) return false;
            if (!string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(m.Content)) return false;
            if (IsCleared(m.Content)) return false;
            return true;
        }

        private static bool ShouldTrigger(MicroCompactOptions options, int? threshold, int tokens)
        {
            // ① 闲置够久 → 触发（ZCode 的 TimeBased 触发）
            if (options.LastActivityUtc.HasValue)
            {
                var now = options.NowUtc ?? DateTimeOffset.UtcNow;
                var idle = now - options.LastActivityUtc.Value;
                if (idle > TimeSpan.FromMinutes(Math.Max(1, options.IdleThresholdMinutes)))
                    return true;
            }

            // ② 体积压力 → 触发（ZCode 的 TokenPressure 触发）
            return threshold.HasValue && tokens >= threshold.Value;
        }

        private static int? ResolveThreshold(MicroCompactOptions options)
        {
            if (options.ThresholdTokens is > 0)
                return options.ThresholdTokens;

            if (options.AutoCompactThreshold is > 0)
                return CompactPolicy.ResolveMicroCompactThreshold(options.AutoCompactThreshold.Value);

            return null;
        }

        private static IEnumerable<string?> Contents(List<ChatMessage> messages)
        {
            foreach (var m in messages)
                yield return m.Content;
        }

        private static List<ChatMessage> Clone(IReadOnlyList<ChatMessage> source)
        {
            var list = new List<ChatMessage>(source.Count);
            foreach (var m in source)
                list.Add(new ChatMessage(m.Role, m.Content));
            return list;
        }
    }
}
