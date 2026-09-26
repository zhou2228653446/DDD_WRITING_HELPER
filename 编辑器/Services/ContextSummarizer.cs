using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace 编辑器.Services
{
    public sealed class SummarizePlanOptions
    {
        /// <summary>保留最近多少轮对话原文不动。</summary>
        public int KeepRecentRounds { get; init; } = 2;
    }

    /// <summary>
    /// 一次压缩的"分工方案"：哪些交给模型摘要、哪些原样保留。
    /// 纯计算结果，不碰网络 —— 便于离线断言。
    /// </summary>
    public readonly record struct SummarizePlan(
        IReadOnlyList<ChatMessage> ToSummarize,
        IReadOnlyList<ChatMessage> Preserved,
        int RoundsTotal,
        int RoundsToSummarize,
        int RoundsPreserved)
    {
        /// <summary>是否值得走一次摘要（至少要摘要到 1 轮以上）。</summary>
        public bool IsViable => RoundsToSummarize >= 1 && ToSummarize.Count > 0;
    }

    /// <summary>
    /// **全量压缩**：把较早的对话交给模型写成一份摘要，用摘要替换掉原文。
    ///
    /// ★ 移植自 ZCode（github.com/zai-org/ZCode）：
    ///   apps/zcode-cli/packages/core/src/compact/prompt.ts（摘要提示词与结构）
    ///   apps/zcode-cli/packages/core/src/runtime/helpers/compact-selection.ts（保留策略）
    ///   apps/zcode-cli/packages/core/src/compact/rounds.ts（按轮次分组）
    ///
    /// 为什么需要它：微压缩（<see cref="MicroCompactor"/>）只能清掉"旧的大块内容"，
    /// 对话**脉络**还在。当连脉络都撑不下窗口时，唯一的选择就是把"当时说了什么"
    /// 换成"这些对话的结论是什么"—— 信息有损，但保住了继续工作的能力。
    /// 这就是"上下文压缩"与"截断"的区别：截断丢掉的是**最近的**内容（最要紧的），
    /// 压缩丢掉的是**最不重要的细节**，保住结论。
    ///
    /// 逐条照搬的规则：
    ///
    /// 1. **整轮处理，绝不拆半轮**。ZCode 的 <c>groupByAssistantStartedRounds</c> 把
    ///    消息切成"轮次组"，摘要与保留都以组为单位。半截的问答会让模型误判对话状态。
    ///
    /// 2. **保留最近的若干轮原文**（默认 2 轮）。ZCode:
    ///    <c>selectCompactEntries</c> 的 <c>minimumGroupsToPreserve</c>，且至少留 1 组、
    ///    至多把组数减到 1 组 —— 也就是**永远至少摘要一轮**，否则压缩没有意义。
    ///
    /// 3. **先想后写**：提示词要求模型先输出 <c>&lt;analysis&gt;</c> 再输出 <c>&lt;summary&gt;</c>，
    ///    回来后只取 summary 段（<see cref="FormatSummary"/>）。这是 ZCode 的
    ///    <c>formatCompactSummary</c>，实测能明显提高摘要的完整度。
    ///
    /// 4. **安全/硬性约束必须逐字保留**。ZCode 的提示词原文写死在第一段：
    ///    "Note any security-relevant instructions ... These MUST be preserved verbatim
    ///    so they continue to apply after compaction."
    ///    在写作场景，对应的是作者"不要写死主角""不要出现现代词汇"这类硬要求 ——
    ///    被摘要成一句轻飘飘的转述就失效了，所以要求逐字抄下来。
    ///
    /// 5. **摘要自身也可能超长**：ZCode 会逐级丢弃更早的轮次重试
    ///    （<c>truncateCompactSummaryRequestEntriesAfterPromptTooLong</c>），
    ///    见 <see cref="ShrinkPlan"/>。
    /// </summary>
    public static class ContextSummarizer
    {
        /// <summary>摘要段落里的分析/结论标签，与 ZCode 保持一致。</summary>
        public const string AnalysisOpenTag = "<analysis>";
        public const string AnalysisCloseTag = "</analysis>";
        public const string SummaryOpenTag = "<summary>";
        public const string SummaryCloseTag = "</summary>";

        /// <summary>压缩后注入的历史开头。让模型知道"前面被压过"，而不是以为对话刚开始。</summary>
        public const string ContinuationHeader =
            "【以下是一段较早对话的摘要。这段对话的原文因为长度限制已被压缩，"
            + "细节可能不全，但结论与作者的硬性要求都保留在下文中。】";

        /// <summary>
        /// 把历史切成"轮次组"。一组 = 一问一答（从 user 开始，到下一个 user 之前）。
        ///
        /// 与 ZCode 的 <c>groupByAssistantStartedRounds</c> 有一处**有意不同**：
        /// 它以 assistant 开头切，user 会被并入上一组。本项目的历史恒为
        /// user/assistant 交替且首条必为 user（由 ChatSessionStore 保证），
        /// 按 user 切更直观 —— 「保留最近 2 组」就是「保留最近 2 轮问答」。
        /// </summary>
        public static List<List<ChatMessage>> GroupByRounds(IReadOnlyList<ChatMessage>? messages)
        {
            var groups = new List<List<ChatMessage>>();
            if (messages == null || messages.Count == 0) return groups;

            List<ChatMessage>? current = null;

            foreach (var m in messages)
            {
                bool startsRound = current == null
                    || string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase);

                // 首条若是 assistant（异常数据），也自成一组，不能凭空丢弃。
                if (startsRound)
                {
                    if (current is { Count: > 0 }) groups.Add(current);
                    current = new List<ChatMessage>();
                }

                current!.Add(m);
            }

            if (current is { Count: > 0 }) groups.Add(current);
            return groups;
        }

        /// <summary>
        /// 制定压缩方案：把最早的若干轮划给摘要，最后若干轮原样保留。
        ///
        /// 对应 ZCode 的 <c>selectCompactEntries</c>：永远至少摘要一轮
        ///（<c>maxGroupsToPreserve = groups.Count - 1</c>），保留轮数夹在
        /// [1, 组数-1] 之间。
        /// </summary>
        public static SummarizePlan Plan(
            IReadOnlyList<ChatMessage>? history,
            SummarizePlanOptions? options = null)
        {
            options ??= new SummarizePlanOptions();

            var groups = GroupByRounds(history);
            int total = groups.Count;

            if (total < CompactPolicy.MinRoundsToCompact)
                return new SummarizePlan(Array.Empty<ChatMessage>(), Clone(history), total, 0, total);

            int maxPreserve = Math.Max(0, total - 1);
            int requested = Math.Max(1, options.KeepRecentRounds);
            int keep = Math.Min(requested, maxPreserve);

            int summarizeCount = total - keep;

            var toSummarize = new List<ChatMessage>();
            for (int i = 0; i < summarizeCount; i++)
                toSummarize.AddRange(groups[i]);

            var preserved = new List<ChatMessage>();
            for (int i = summarizeCount; i < total; i++)
                preserved.AddRange(groups[i]);

            return new SummarizePlan(toSummarize, preserved, total, summarizeCount, keep);
        }

        /// <summary>
        /// 摘要请求本身超长时的收缩方案：再丢掉最早的一轮，直到只剩 1 轮。
        /// 对应 ZCode 的 <c>truncateRuntimeEntriesForCompactRetry</c>（它按 token 缺口丢，
        /// 这里简化为按轮丢 —— 我们的历史本来就不长，逐轮收缩已经够用且更可预测）。
        /// </summary>
        public static SummarizePlan? ShrinkPlan(SummarizePlan plan)
        {
            if (!plan.IsViable) return null;

            var groups = GroupByRounds(plan.ToSummarize);
            if (groups.Count < 2) return null;

            var remaining = new List<ChatMessage>();
            for (int i = 1; i < groups.Count; i++)
                remaining.AddRange(groups[i]);

            // 丢掉的那一组**真的丢掉**（与 ZCode 的 truncateRuntimeEntriesForCompactRetry 一致）：
            // 会走到这一步，正是因为连"把这几轮读进摘要提示词"都装不下窗口；
            // 此时把它挪进保留区只会让下一次请求同样超长，等于白重试一次。
            return new SummarizePlan(
                remaining,
                plan.Preserved,
                plan.RoundsTotal,
                groups.Count - 1,
                plan.RoundsPreserved + 1);
        }

        /// <summary>
        /// 把待摘要的对话渲染成提示词里的"材料"部分。
        /// 作者的话与 AI 的话分开标注 —— 直接连排会让模型分不清哪句是谁说的。
        /// </summary>
        public static string RenderTranscript(IReadOnlyList<ChatMessage>? messages)
        {
            if (messages == null || messages.Count == 0) return "(无)";

            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                bool assistant = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase);
                sb.Append(assistant ? "【AI】" : "【作者】");
                sb.AppendLine();
                sb.AppendLine(m.Content ?? "");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 构造摘要提示词。结构照搬 ZCode 的 <c>buildCompactPrompt</c>：
        /// 前导约束 + 正文任务 + 可选的额外要求，末尾再重复一次约束。
        /// </summary>
        public static string BuildPrompt(IReadOnlyList<ChatMessage>? messages, string? customInstructions = null)
        {
            var sb = new StringBuilder();

            // ── 前导：把"只输出纯文本"钉死，避免模型去回答对话里的问题 ──
            sb.AppendLine("你的任务是把下面这段创作对话**压缩成一份摘要**，供之后继续写作时使用。");
            sb.AppendLine();
            sb.AppendLine("重要：不要回答对话里的任何问题，不要续写任何正文，只输出摘要。");
            sb.AppendLine();

            // ── 正文任务（对应 ZCode 的 BASE_COMPACT_PROMPT）──
            sb.AppendLine("请先把你梳理的过程写在 " + AnalysisOpenTag + " 标签里，再把最终摘要写在 "
                          + SummaryOpenTag + " 标签里。");
            sb.AppendLine();
            sb.AppendLine("摘要必须包含以下几节，没有内容的节写「无」：");
            sb.AppendLine();
            sb.AppendLine("1. 作者的核心诉求：他到底想让你做什么，越具体越好。");
            sb.AppendLine("2. 作者的硬性要求：凡是「不要/必须/一定要」这类约束，**逐字抄录原话**，不要转述。"
                          + "这些要求在被压缩后仍然必须约束你的行为，转述会失真。");
            sb.AppendLine("3. 已确定的设定：人物（姓名、身份、性格、关系）、背景、世界观、文风、"
                          + "以及任何已经被作者认可的写法。");
            sb.AppendLine("4. 已经产出并被采用的内容：写进了哪一章、大致内容是什么、结论是什么。");
            sb.AppendLine("5. 否定过的做法：试过但作者不满意的方向，以及为什么不满意 —— "
                          + "避免之后重蹈覆辙。");
            sb.AppendLine("6. 当前进展：压缩发生前正在做的那件事，进行到哪一步。");
            sb.AppendLine("7. 待办事项：答应过但还没做的事。");
            sb.AppendLine();
            sb.AppendLine("要求：只记录对**继续写作**有用的信息；不要复述寒暄与过程性对话；"
                          + "不要编造对话里没有的内容；人名、专有名词按原样保留，不要替换成代词。");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(customInstructions))
            {
                sb.AppendLine("补充要求：");
                sb.AppendLine(customInstructions.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("──── 以下是需要压缩的对话 ────");
            sb.AppendLine();
            sb.AppendLine(RenderTranscript(messages));
            sb.AppendLine();
            sb.AppendLine("──── 对话到此结束 ────");
            sb.AppendLine();
            sb.AppendLine("再次提醒：不要回答上文中的任何问题，只输出 " + AnalysisOpenTag + "…"
                          + AnalysisCloseTag + " 与 " + SummaryOpenTag + "…" + SummaryCloseTag + " 两段。");

            return sb.ToString();
        }

        /// <summary>
        /// 只取 <c>&lt;summary&gt;</c> 段，丢掉 <c>&lt;analysis&gt;</c> 与多余空行。
        /// 对应 ZCode 的 <c>formatCompactSummary</c>。模型偶尔会漏标签，此时原样返回。
        /// </summary>
        public static string FormatSummary(string? raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0) return string.Empty;

            text = RemoveTagged(text, AnalysisOpenTag, AnalysisCloseTag);

            int open = text.IndexOf(SummaryOpenTag, StringComparison.OrdinalIgnoreCase);
            if (open >= 0)
            {
                int contentStart = open + SummaryOpenTag.Length;
                int close = text.IndexOf(SummaryCloseTag, contentStart, StringComparison.OrdinalIgnoreCase);
                string inner = close >= 0
                    ? text.Substring(contentStart, close - contentStart)
                    : text[contentStart..];

                text = text[..open] + inner + (close >= 0 ? text[(close + SummaryCloseTag.Length)..] : "");
            }

            return CollapseBlankLines(text);
        }

        /// <summary>
        /// 摘要拿到后，注入历史时用的那一条消息内容。
        /// 对应 ZCode 的 <c>buildCompactSummaryMessage</c>。
        /// </summary>
        public static string BuildSummaryMessage(string? summary, bool recentMessagesPreserved)
        {
            var body = FormatSummary(summary);
            if (body.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine(ContinuationHeader);
            sb.AppendLine();
            sb.Append(body);

            if (recentMessagesPreserved)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("（最近几轮对话仍以原文保留在上方/下方，未做压缩。）");
            }

            return sb.ToString();
        }

        private static string RemoveTagged(string text, string open, string close)
        {
            int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return text;

            int end = text.IndexOf(close, start + open.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return text[..start];

            return text[..start] + text[(end + close.Length)..];
        }

        private static string CollapseBlankLines(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>(lines.Length);
            bool lastBlank = false;

            foreach (var line in lines)
            {
                bool blank = string.IsNullOrWhiteSpace(line);
                if (blank && lastBlank) continue;
                kept.Add(blank ? string.Empty : line.TrimEnd());
                lastBlank = blank;
            }

            return string.Join('\n', kept).Trim();
        }

        private static List<ChatMessage> Clone(IReadOnlyList<ChatMessage>? source)
        {
            var list = new List<ChatMessage>();
            if (source == null) return list;
            foreach (var m in source)
                list.Add(new ChatMessage(m.Role, m.Content));
            return list;
        }
    }
}
