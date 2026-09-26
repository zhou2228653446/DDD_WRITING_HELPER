using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    /// <summary>这次预算检查最后走到了哪一步。</summary>
    public enum CompactOutcome
    {
        /// <summary>装得下，什么都没做。</summary>
        NotNeeded,

        /// <summary>只做了本地微压缩（省略旧回复），没花钱。</summary>
        MicroOnly,

        /// <summary>做了全量压缩，旧对话已变成摘要。</summary>
        Summarized,

        /// <summary>需要压缩，但条件不满足（轮次不够 / 熔断 / 没配置服务）。</summary>
        Aborted,

        /// <summary>压缩过程失败了，本轮按原样发送。</summary>
        Failed
    }

    public readonly record struct CompactResult(
        IReadOnlyList<ChatMessage> History,
        CompactOutcome Outcome,
        bool MicroApplied,
        int MicroClearedCount,
        int MicroTokensSaved,
        bool SummaryApplied,
        int SummarizedRounds,
        int PreservedRounds,
        string Message)
    {
        /// <summary>历史是否被改动过。</summary>
        public bool Changed => MicroApplied || SummaryApplied;
    }

    /// <summary>
    /// 发请求前的上下文预算检查与压缩。
    ///
    /// ★ 机制移植自 ZCode（github.com/zai-org/ZCode）的 compact 体系，见
    ///   <see cref="CompactPolicy"/>（阈值）、<see cref="MicroCompactor"/>（本地清理）、
    ///   <see cref="ContextSummarizer"/>（摘要）各自的类注释。
    ///
    /// 这里只负责"编排"：定阈值 → 判断 → 按**代价从低到高**依次出手 → 返回最终历史。
    /// 具体算法不在这里，这样每一层都能单独断言（见 .workbuddy/compact 的验证 harness）。
    ///
    /// 为什么不做成 MainWindow 里的私有方法：那样只能靠"起一个真窗口再反射调用"来验证，
    /// 而压缩是本项目里**唯一会主动改写用户记忆**的逻辑，必须能离线跑断言。
    /// </summary>
    public sealed class ChatContextCompactor
    {
        /// <summary>聊天功能的输出预算，参与"给输出预留空间"的计算。</summary>
        public const int DefaultMaxOutputTokens = 2_000;

        /// <summary>摘要请求的输出上限。摘要要覆盖多轮，给少了会丢掉后半段。</summary>
        public const int SummaryMaxOutputTokens = 1_500;

        /// <summary>摘要请求本身超长时，最多丢掉几轮重试（ZCode: MAX_COMPACT_PROMPT_TOO_LONG_RETRIES）。</summary>
        public const int MaxAttempts = 3;

        /// <summary>摘要调用的 system 提示词。任务说明全在 user 侧的提示词里。</summary>
        public const string CompactorSystemPrompt =
            "你是一个对话压缩器。你的唯一任务是把给定的对话压缩成摘要，"
            + "不回答其中的问题、不续写正文、不做任何其他事。";

        private readonly ChatSessionStore _store;
        private readonly Func<string> _modelName;
        private readonly CompactPolicyConfig _config;

        private bool _busy;

        public ChatContextCompactor(
            ChatSessionStore store,
            Func<string>? modelName = null,
            CompactPolicyConfig? config = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _modelName = modelName ?? (() => "");
            _config = config ?? new CompactPolicyConfig { MaxOutputTokens = DefaultMaxOutputTokens };
        }

        /// <summary>是否允许自动压缩。关掉就等于回到"撞到服务端报错"的老路子。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>状态栏文案回调。</summary>
        public Action<string>? OnStatus { get; set; }

        /// <summary>
        /// 上一轮请求服务端报回的**真实**输入 token。null = 还没拿到过。
        ///
        /// ★ ZCode 的 provider_usage 优先策略：本地估算与真实值可能差一倍以上
        ///   （不同服务商对中文、对 system 的计费口径都不同）。拿到过真实值之后
        ///   就该以它为准，估算只负责"还没有真实值"的第一轮。
        /// </summary>
        public int? LastRequestInputTokens { get; set; }

        /// <summary>最后一次决策，供界面解释"为什么压/不压"。</summary>
        public CompactDecision? LastDecision { get; private set; }

        /// <summary>
        /// 确保这条请求装得进窗口，返回**最终要发送的历史**。
        ///
        /// 摘要一旦生成就写进了 store，调用方随后取 <c>BuildSummaryBlock()</c> 拼进
        /// systemPrompt 即可 —— 本方法**不碰 systemPrompt**（它只在估算时把摘要块算进去）。
        /// </summary>
        /// <param name="systemPrompt">不含摘要块的 system。</param>
        /// <param name="userPrompt">本轮的 user 内容（含当前章节正文）。</param>
        /// <param name="completer">"发一次请求"的能力（prompt, system, options, ct → AiResult）。</param>
        public async Task<CompactResult> EnsureBudgetAsync(
            string systemPrompt,
            string userPrompt,
            Func<string, string?, CompletionOptions, CancellationToken, Task<AiResult>> completer,
            CancellationToken ct = default)
        {
            var history = _store.BuildHistory();
            if (history.Count == 0)
                return Unchanged(history, CompactOutcome.NotNeeded, "");

            string model = _modelName();
            int autoThreshold = CompactPolicy.ResolveThreshold(_config, model);
            int microThreshold = CompactPolicy.ResolveMicroCompactThreshold(autoThreshold);

            int rounds = ContextSummarizer.GroupByRounds(history).Count;
            bool hasReply = history.Any(h =>
                string.Equals(h.Role, "assistant", StringComparison.OrdinalIgnoreCase));

            // 估算"这一轮完整请求"：system + 已有摘要 + 历史 + 本轮 user。
            // 摘要块由调用方稍后才拼，这里必须自己加，否则会漏算上一轮压出来的摘要。
            int estimatedTotal = TokenEstimator.Estimate(systemPrompt)
                                 + TokenEstimator.Estimate(_store.BuildSummaryBlock())
                                 + TokenEstimator.EstimateAll(history.Select(h => h.Content))
                                 + TokenEstimator.Estimate(userPrompt);

            int? providerTokens = LastRequestInputTokens is > 0
                ? LastRequestInputTokens.Value + TokenEstimator.Estimate(userPrompt)
                : null;

            var decision = CompactPolicy.Decide(
                currentTokens: providerTokens,
                estimatedTokens: estimatedTotal,
                rounds: rounds,
                hasAssistantReply: hasReply,
                config: WithEnabled(),
                model: model,
                consecutiveFailures: _store.ConsecutiveCompactFailures,
                fromProviderUsage: providerTokens.HasValue);

            LastDecision = decision;

            bool microApplied = false;
            int microCleared = 0, microSaved = 0;

            // ── 顺序很关键：两步的**输入必须分开** ──────────────────────
            // 微压缩清掉的正是"AI 之前写过什么"，而全量压缩要提炼的恰恰是这些内容。
            // 若先清理再摘要，摘要就是基于残缺材料写出来的 —— 同一轮里把信息丢了两次。
            // 所以：需要摘要就直接摘（材料用**未清理的原始历史**）；不需要摘要，才做本地清理。

            // ── 微压缩：纯本地、不花钱，因此**不受熔断影响**（熔断只挡要调用模型的那一步）
            if (!decision.ShouldCompact
                && decision.Reason != CompactReason.Disabled
                && decision.TokenCount >= microThreshold)
            {
                var micro = MicroCompactor.Compact(history, new MicroCompactOptions
                {
                    // ★ 该不该清理已经由外层按**整条请求**的规模判过了（其中含 system 与本轮正文），
                    //   这里不能再按"历史自身的 token"判一次 —— 两者差的可能是十倍，
                    //   那样永远不满足触发条件，微压缩等于没接上。
                    ThresholdTokens = 1,
                    AutoCompactThreshold = autoThreshold,
                    LastActivityUtc = _store.LastActivityUtc
                });

                if (micro.Applied)
                {
                    // 内容变了，上轮那个真实值不再对应当前历史
                    LastRequestInputTokens = null;

                    OnStatus?.Invoke($"上下文偏大：已省略 {micro.ClearedCount} 条较早回复"
                                     + $"（省约 {FormatTokens(micro.TokensSaved)} token）");

                    return new CompactResult(micro.Messages, CompactOutcome.MicroOnly,
                        true, micro.ClearedCount, micro.TokensSaved, false, 0, 0,
                        $"已省略 {micro.ClearedCount} 条较早回复，省约 {FormatTokens(micro.TokensSaved)} token");
                }
            }

            // 本该压缩、但被熔断挡下：必须报出来。当成"不需要"会让用户以为一切正常，
            // 而实际上压缩早就罢工了（只是在等连续失败计数被重置）。
            if (decision.Reason == CompactReason.CircuitBreaker)
            {
                return Unchanged(history, CompactOutcome.Aborted,
                    $"上下文压缩已连续失败 {_store.ConsecutiveCompactFailures} 次，暂时停手不再尝试"
                    + "（本轮按原样发送；连续失败会累加到下次清理对话或重启时重置）");
            }

            if (!decision.ShouldCompact)
                return Unchanged(history, CompactOutcome.NotNeeded, "");

            // ── ③ 全量压缩：调一次模型把旧对话写成摘要 ────────────────────
            var plan = ContextSummarizer.Plan(history, new SummarizePlanOptions
            {
                KeepRecentRounds = ChatSessionStore.KeepRecentRoundsOnCompact
            });

            if (!plan.IsViable)
                return Unchanged(history, CompactOutcome.Aborted, "可压缩的轮次不足，保持原样");

            if (completer == null)
                return Unchanged(history, CompactOutcome.Aborted, "没有可用的模型服务，未压缩");

            if (_busy)
                return Unchanged(history, CompactOutcome.Aborted, "上一次压缩还在进行，本次跳过");

            _busy = true;
            try
            {
                var material = BuildMaterial(_store.Summary, plan.ToSummarize);
                var attemptPlan = plan;

                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    var result = await completer(
                        ContextSummarizer.BuildPrompt(material),
                        CompactorSystemPrompt,
                        new CompletionOptions
                        {
                            MaxTokens = SummaryMaxOutputTokens,
                            Temperature = 0.3,
                            CancellationToken = ct
                        },
                        ct);

                    if (result == null || !result.IsUsable)
                    {
                        // 连摘要请求本身都装不下 → 丢掉最早一轮再试
                        //（ZCode 的 truncateCompactSummaryRequestEntriesAfterPromptTooLong）
                        if (result != null && IsContextTooLongError(result.Text))
                        {
                            var shrunk = ContextSummarizer.ShrinkPlan(attemptPlan);
                            if (shrunk.HasValue)
                            {
                                attemptPlan = shrunk.Value;
                                material = BuildMaterial(_store.Summary, attemptPlan.ToSummarize);
                                continue;
                            }
                        }

                        _store.RecordCompactFailure();
                        var why = result == null ? "调用方未返回结果" : result.Text;
                        return new CompactResult(history, CompactOutcome.Failed,
                            microApplied, microCleared, microSaved, false, 0, 0,
                            $"对话压缩失败（{Shorten(why)}），本轮按原样发送；"
                            + "连续失败会自动停手，不再反复尝试");
                    }

                    var summary = ContextSummarizer.FormatSummary(result.Text);
                    if (summary.Length == 0)
                    {
                        _store.RecordCompactFailure();
                        return new CompactResult(history, CompactOutcome.Failed,
                            microApplied, microCleared, microSaved, false, 0, 0,
                            "对话压缩返回了空摘要，本轮按原样发送");
                    }

                    _store.ApplySummary(summary, attemptPlan);
                    _store.RecordCompactSuccess();
                    LastRequestInputTokens = null;

                    return new CompactResult(
                        _store.BuildHistory(),
                        CompactOutcome.Summarized,
                        microApplied, microCleared, microSaved,
                        true,
                        attemptPlan.RoundsToSummarize,
                        attemptPlan.RoundsPreserved,
                        $"上下文已压缩：{attemptPlan.RoundsToSummarize} 轮对话 → 摘要，"
                        + $"保留最近 {attemptPlan.RoundsPreserved} 轮原文");
                }

                return Unchanged(history, CompactOutcome.Aborted, "压缩重试次数用尽，保持原样");
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// 拼出交给"压缩器"的材料。
        ///
        /// ★ 摘要是**滚动**的：再次压缩时要把上一版摘要一并交给模型，让它产出"合并后"
        ///   的新摘要。否则每压一次就丢一层，聊到后面 AI 会连最初的设定都记不住。
        /// </summary>
        public static List<ChatMessage> BuildMaterial(string? previousSummary, IReadOnlyList<ChatMessage> turns)
        {
            var material = new List<ChatMessage>();

            if (!string.IsNullOrWhiteSpace(previousSummary))
            {
                var text = ContextSummarizer.FormatSummary(previousSummary);
                if (text.Length > 0)
                {
                    material.Add(ChatMessage.User(
                        "（以下是你**上一次**压缩产出的摘要。请把它与新对话合并成一份新摘要，"
                        + "不要丢掉其中仍然有效的设定与要求。）\n\n" + text));
                    material.Add(ChatMessage.Assistant("好的，我会把这份摘要与新对话合并。"));
                }
            }

            material.AddRange(turns);
            return material;
        }

        /// <summary>
        /// 判断失败文案是不是"上下文超长"。用关键词而不是状态码 ——
        /// 各家协议、各家网关给的形态都不一样（见 <see cref="ApiErrors"/>）。
        /// </summary>
        public static bool IsContextTooLongError(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            var d = message.ToLowerInvariant();
            bool mentionsContext = d.Contains("context") || d.Contains("prompt is too long")
                                   || d.Contains("too many tokens") || d.Contains("maximum context");
            bool mentionsOverflow = d.Contains("length") || d.Contains("exceed")
                                    || d.Contains("too long") || d.Contains("larger than");
            return mentionsContext && mentionsOverflow;
        }

        public static string FormatTokens(int tokens) =>
            tokens >= 1000 ? $"{tokens / 1000.0:0.#}K" : tokens.ToString();

        private static string Shorten(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "未知原因";
            var oneLine = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return oneLine.Length > 120 ? oneLine[..120] + "…" : oneLine;
        }

        private CompactPolicyConfig WithEnabled() => new()
        {
            Enabled = Enabled,
            ContextWindow = _config.ContextWindow,
            MaxOutputTokens = _config.MaxOutputTokens,
            BufferTokens = _config.BufferTokens,
            MaxConsecutiveFailures = _config.MaxConsecutiveFailures
        };

        private static CompactResult Unchanged(IReadOnlyList<ChatMessage> history, CompactOutcome outcome, string message) =>
            new(history, outcome, false, 0, 0, false, 0, 0, message);
    }
}
