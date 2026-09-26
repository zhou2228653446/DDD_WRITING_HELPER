using System;

namespace 编辑器.Services
{
    /// <summary>什么都不做的策略配置（用于关闭压缩 / 测试）。</summary>
    public sealed class CompactPolicyConfig
    {
        public bool Enabled { get; init; } = true;

        /// <summary>上下文窗口（token）。null → 由 <see cref="ModelContextCatalog"/> 按模型名解析。</summary>
        public int? ContextWindow { get; init; }

        /// <summary>本次请求允许的最大输出 token，用于给输出侧预留空间。</summary>
        public int? MaxOutputTokens { get; init; }

        /// <summary>提前量：距阈值还差这么多 token 时就开始压缩。</summary>
        public int? BufferTokens { get; init; }

        /// <summary>连续失败多少次后放弃自动压缩（熔断）。</summary>
        public int? MaxConsecutiveFailures { get; init; }
    }

    /// <summary>不压缩的原因，用于日志与界面提示。取值与 ZCode 的 reason 一一对应。</summary>
    public enum CompactReason
    {
        /// <summary>被配置关掉了。</summary>
        Disabled,

        /// <summary>内容太少，压了也没意义。</summary>
        NotEnoughMessages,

        /// <summary>连续压缩失败，熔断中。</summary>
        CircuitBreaker,

        /// <summary>还没到阈值。</summary>
        BelowThreshold,

        /// <summary>超过阈值，应当压缩。</summary>
        AboveThreshold
    }

    /// <summary>一次压缩决策的完整依据（把中间量都带出来，方便在状态栏解释"为什么压/不压"）。</summary>
    public readonly record struct CompactDecision(
        bool ShouldCompact,
        CompactReason Reason,
        int TokenCount,
        int EstimatedTokenCount,
        int ContextWindow,
        int EffectiveContextWindow,
        int OutputReserveTokens,
        int BufferTokens,
        int Threshold)
    {
        /// <summary>剩余可用空间（相对阈值，负数表示已超）。</summary>
        public int Headroom => Threshold - TokenCount;

        /// <summary>token 数是否来自服务端真实用量（而非本地估算）。</summary>
        public bool FromProviderUsage { get; init; }

        /// <summary>距窗口上限的使用率（0~1+），用于给用户看进度条。</summary>
        public double UsageRatio =>
            ContextWindow <= 0 ? 0 : Math.Clamp((double)TokenCount / ContextWindow, 0, 1.5);
    }

    /// <summary>
    /// 上下文压缩的阈值策略。
    ///
    /// ★ 移植自 ZCode（github.com/zai-org/ZCode）：
    ///   apps/zcode-cli/packages/core/src/compact/policy.ts
    ///   apps/zcode-cli/packages/core/src/compact/microcompact.ts（阈值推算部分）
    ///
    /// 核心几条（原文照搬）：
    ///
    /// 1. **窗口是 input 和 output 共享的**，所以分母必须先扣掉本次请求预留的输出空间，
    ///    不能拿完整窗口当阈值分母 —— 否则会在"还剩一点空间但不够写完回复"的位置
    ///    才开始压缩，请求照样会失败。ZCode 原注释：
    ///    "provider 的 context window 是 input + output 共享窗口；自动压缩只能让出输入侧"。
    ///
    /// 2. **提前量（buffer）**：不是顶到天花板才动，而是距阈值还有一段就开始。
    ///
    /// 3. **真实用量优先**：服务端返回的 input_tokens 才是准确值，本地估算只在
    ///    "本轮还没发出去过"时兜底。ZCode 用 tokenOverride.source 区分两者。
    ///
    /// 4. **熔断**：连续失败 N 次就停手，不要在坏掉的链路上反复烧钱。
    ///
    /// 与 ZCode 的一处**有意不同**：ZCode 只服务 200K 窗口的模型，buffer 取固定 13K；
    /// 本项目面对 8K ~ 1M 的各种模型，固定 13K 在 8K 窗口上会把阈值压到 0（等于每轮都压）。
    /// 因此这里把 buffer 再按有效窗口的比例收一次（见 <see cref="ResolveBufferTokens"/>）。
    /// </summary>
    public static class CompactPolicy
    {
        /// <summary>ZCode 默认窗口，本项目兜底改用 <see cref="ModelContextCatalog.FallbackContextWindow"/>。</summary>
        public const int DefaultContextWindow = ModelContextCatalog.FallbackContextWindow;

        /// <summary>ZCode 的 DEFAULT_AUTOCOMPACT_OUTPUT_RESERVE_TOKENS。</summary>
        public const int DefaultOutputReserveTokens = 32_000;

        /// <summary>ZCode 的 PREFLIGHT_AUTOCOMPACT_OUTPUT_RESERVE_TOKENS，输出预留的上限。</summary>
        public const int MaxOutputReserveTokens = 21_000;

        /// <summary>输出预留的下限：压缩后的这一轮总还得写得下内容。</summary>
        public const int MinOutputReserveTokens = 4_000;

        /// <summary>ZCode 的 AUTOCOMPACT_BUFFER_TOKENS。</summary>
        public const int DefaultBufferTokens = 13_000;

        /// <summary>ZCode 的 MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES。</summary>
        public const int MaxConsecutiveFailures = 3;

        /// <summary>ZCode 的 DEFAULT_MICROCOMPACT_THRESHOLD_RATIO。</summary>
        public const double MicroCompactThresholdRatio = 0.9;

        /// <summary>ZCode 的 DEFAULT_MICROCOMPACT_THRESHOLD_BUFFER_TOKENS。</summary>
        public const int MicroCompactThresholdBufferTokens = 2_000;

        /// <summary>ZCode 的 DEFAULT_MICROCOMPACT_MIN_TOKEN_SAVINGS，省不下这么多就不值得动。</summary>
        public const int MicroCompactMinTokenSavings = 256;

        /// <summary>ZCode 的 DEFAULT_MICROCOMPACT_KEEP_RECENT_TOOL_RESULTS，最近若干条不动。</summary>
        public const int MicroCompactKeepRecentItems = 5;

        /// <summary>ZCode 的 DEFAULT_MICROCOMPACT_IDLE_THRESHOLD_MINUTES，闲置这么久也值得压一次。</summary>
        public const int MicroCompactIdleThresholdMinutes = 60;

        /// <summary>可供压缩的最小轮数：一组什么都压不出来。</summary>
        public const int MinRoundsToCompact = 2;

        /// <summary>
        /// 有效窗口 = 窗口 − 输出预留。
        /// ZCode: <c>getEffectiveContextWindowSize</c>。
        /// </summary>
        public static int ResolveEffectiveContextWindow(CompactPolicyConfig? config = null)
        {
            config ??= new CompactPolicyConfig();
            int contextWindow = ResolveContextWindow(config);
            int reserve = Math.Min(ResolveOutputReserveTokens(config), contextWindow);
            return Math.Max(0, contextWindow - reserve);
        }

        /// <summary>窗口大小：显式配置 > 内置表 > 兜底。</summary>
        public static int ResolveContextWindow(CompactPolicyConfig? config = null, string? model = null)
        {
            config ??= new CompactPolicyConfig();

            if (config.ContextWindow is > 0)
                return Math.Max(ModelContextCatalog.MinContextWindow, config.ContextWindow.Value);

            return ModelContextCatalog.Resolve(model);
        }

        /// <summary>
        /// 输出预留。ZCode: <c>getAutoCompactOutputReserveTokens</c> =
        /// <c>min(maxOutputTokens ?? 32000, 21000)</c>；这里再加一个 4000 的下限，
        /// 因为本项目的 max_tokens 通常只有 1000~2000，全按实际值留会让阈值贴到天花板。
        /// </summary>
        public static int ResolveOutputReserveTokens(CompactPolicyConfig? config = null)
        {
            config ??= new CompactPolicyConfig();
            int requested = config.MaxOutputTokens is > 0
                ? config.MaxOutputTokens.Value
                : DefaultOutputReserveTokens;

            return Math.Clamp(requested, MinOutputReserveTokens, MaxOutputReserveTokens);
        }

        /// <summary>
        /// 提前量。ZCode: <c>min(config.bufferTokens ?? 13000, ...)</c>；
        /// 额外按有效窗口的 1/4 收一次，避免小窗口模型被 13K 的固定提前量压到负数阈值。
        /// </summary>
        public static int ResolveBufferTokens(CompactPolicyConfig? config, int effectiveContextWindow)
        {
            config ??= new CompactPolicyConfig();
            int requested = config.BufferTokens is > 0 ? config.BufferTokens.Value : DefaultBufferTokens;
            int capped = Math.Max(0, effectiveContextWindow / 4);
            return Math.Min(requested, capped);
        }

        /// <summary>压缩阈值。ZCode: <c>getAutoCompactThreshold</c>。</summary>
        public static int ResolveThreshold(CompactPolicyConfig? config = null, string? model = null)
        {
            int effective = ResolveEffectiveContextWindow(Normalize(config, model));
            int buffer = ResolveBufferTokens(config, effective);
            return Math.Max(0, effective - buffer);
        }

        /// <summary>
        /// 微压缩阈值。ZCode: <c>buildDefaultMicrocompactThreshold</c> ——
        /// 取「全量阈值 × 0.9」与「全量阈值 − 2000」中较小的那个，先于全量压缩出手。
        /// </summary>
        public static int ResolveMicroCompactThreshold(int autoCompactThreshold)
        {
            int ratioThreshold = (int)Math.Floor(autoCompactThreshold * MicroCompactThresholdRatio);
            int bufferThreshold = autoCompactThreshold - MicroCompactThresholdBufferTokens;
            return Math.Max(0, Math.Min(ratioThreshold, bufferThreshold));
        }

        /// <summary>
        /// 判断现在该不该压缩。判定次序与 ZCode 完全一致：
        /// 关掉了？→ 太少？→ 熔断？→ 没到阈值？→ 压。
        /// </summary>
        /// <param name="currentTokens">当前内容规模（token）。</param>
        /// <param name="estimatedTokens">本地估算值，仅在 <paramref name="currentTokens"/> 缺省时使用。</param>
        /// <param name="rounds">可压缩的轮数。</param>
        /// <param name="hasAssistantReply">其中是否含有 AI 的回复（只有提问没有回复时压不出东西）。</param>
        /// <param name="consecutiveFailures">连续压缩失败次数。</param>
        /// <param name="fromProviderUsage">currentTokens 是否来自服务端真实 usage。</param>
        public static CompactDecision Decide(
            int? currentTokens,
            int estimatedTokens,
            int rounds,
            bool hasAssistantReply,
            CompactPolicyConfig? config = null,
            string? model = null,
            int consecutiveFailures = 0,
            bool fromProviderUsage = false)
        {
            config ??= new CompactPolicyConfig();

            // ★ 必须先把 model 解析进 config 再往后走。
            //   否则 ResolveEffectiveContextWindow(config) 拿不到 model（它没有 model 参数），
            //   会退回兜底窗口 —— 结果同一个决策里 contextWindow 是按 8K 算的、
            //   阈值却是按 128K 算的，小窗口模型永远不会触发压缩。
            config = Normalize(config, model);

            int contextWindow = ResolveContextWindow(config, model);
            int effective = ResolveEffectiveContextWindow(config);
            int outputReserve = Math.Min(ResolveOutputReserveTokens(config), contextWindow);
            int buffer = ResolveBufferTokens(config, effective);
            int threshold = Math.Max(0, effective - buffer);

            int tokenCount = currentTokens ?? estimatedTokens;

            CompactDecision Make(bool should, CompactReason reason) => new(
                ShouldCompact: should,
                Reason: reason,
                TokenCount: tokenCount,
                EstimatedTokenCount: estimatedTokens,
                ContextWindow: contextWindow,
                EffectiveContextWindow: effective,
                OutputReserveTokens: outputReserve,
                BufferTokens: buffer,
                Threshold: threshold)
            {
                FromProviderUsage = currentTokens.HasValue && fromProviderUsage
            };

            if (!config.Enabled)
                return Make(false, CompactReason.Disabled);

            if (!HasEnoughToCompact(rounds, hasAssistantReply))
                return Make(false, CompactReason.NotEnoughMessages);

            int maxFailures = config.MaxConsecutiveFailures is > 0
                ? config.MaxConsecutiveFailures.Value
                : MaxConsecutiveFailures;
            if (consecutiveFailures >= maxFailures)
                return Make(false, CompactReason.CircuitBreaker);

            if (tokenCount < threshold)
                return Make(false, CompactReason.BelowThreshold);

            return Make(true, CompactReason.AboveThreshold);
        }

        /// <summary>
        /// 内容够不够压一次。ZCode: <c>hasEnoughMessagesToCompact</c> ——
        /// 至少 2 组轮次，且其中得有一条 AI 回复。
        /// </summary>
        public static bool HasEnoughToCompact(int rounds, bool hasAssistantReply) =>
            rounds >= MinRoundsToCompact && hasAssistantReply;

        private static CompactPolicyConfig Normalize(CompactPolicyConfig? config, string? model)
        {
            if (config == null) return new CompactPolicyConfig();
            if (config.ContextWindow is > 0 || model == null) return config;

            return new CompactPolicyConfig
            {
                Enabled = config.Enabled,
                ContextWindow = ModelContextCatalog.Resolve(model),
                MaxOutputTokens = config.MaxOutputTokens,
                BufferTokens = config.BufferTokens,
                MaxConsecutiveFailures = config.MaxConsecutiveFailures
            };
        }
    }
}
