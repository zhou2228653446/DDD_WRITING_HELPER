using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 编辑器.Services
{
    /// <summary>落盘格式。区分版本是为了让老存档（纯消息数组）还能读得进来。</summary>
    internal sealed class ChatSessionFile
    {
        public int Version { get; set; } = ChatSessionStore.FormatVersion;

        /// <summary>较早对话的摘要（滚动压缩的产物）。空表示尚未压缩过。</summary>
        public string Summary { get; set; } = "";

        /// <summary>摘要覆盖了多少轮（仅用于界面提示"这里少了几轮"）。</summary>
        public int SummarizedRounds { get; set; }

        /// <summary>摘要生成后新累积的对话轮次。</summary>
        public List<ChatMessage> Turns { get; set; } = new();

        /// <summary>最后一次交互时间（UTC），用于"闲置够久顺手清一次"的判断。</summary>
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    /// <summary>
    /// 「万能聊天」的对话记忆，按项目保存在 &lt;项目目录&gt;/.chat/session.json。
    ///
    /// ★ 为什么需要它：大模型 API 是**无状态**的，服务端不记得上一轮说过什么。
    ///   所谓"连续对话"，本质是客户端每次请求都把此前所有轮次重发一遍。
    ///
    /// 记忆由三段组成（对应 ZCode 压缩后的历史结构）：
    ///
    ///   ① <see cref="Summary"/>  —— 较早对话的摘要（滚动压缩的产物，会再次被摘要）
    ///   ② <see cref="Turns"/>    —— 摘要之后新累积的轮次，原样保留
    ///   ③ 每轮都会重新带上的设定与正文 —— 不在这里，由各自的预算管
    ///
    /// 三层闸门（由细到粗，前一层能挡住就轮不到后一层）：
    ///
    ///   · 微压缩（<see cref="MicroCompactor"/>）   —— 本地免费，把旧的长回复换成占位符
    ///   · 全量压缩（<see cref="ContextSummarizer"/>）—— 调一次模型，把旧对话写成摘要
    ///   · <see cref="MaxRounds"/> / <see cref="MaxHistoryChars"/>
    ///     —— 硬兜底。压缩机制万一没生效（用户关掉了、模型报错），这一层保证请求
    ///        不会被撑到必然失败。**故意设得比过去宽松**：正常路径现在由压缩接管，
    ///        这里只处理异常。
    ///
    /// ⚠ 摘要**不放进 messages**，而是拼进 systemPrompt（见 <see cref="BuildSummaryBlock"/>）。
    ///   原因：Anthropic 要求 messages 以 user 开头且**同角色不能连续**，
    ///   而摘要天然是一条 user 消息，插在保留轮次（首条也是 user）前面会直接 400。
    ///   放进 system 既避开这个坑，语义上也更顺 —— system 里本来就是"背景设定"。
    /// </summary>
    public class ChatSessionStore
    {
        /// <summary>落盘格式版本。1 = 纯消息数组（旧），2 = 带摘要的对象。</summary>
        public const int FormatVersion = 2;

        /// <summary>硬兜底：最多保留的对话轮数。正常路径由压缩接管，这里防异常。</summary>
        public const int MaxRounds = 40;

        /// <summary>硬兜底：历史文本总量上限（字符）。</summary>
        public const int MaxHistoryChars = 64_000;

        /// <summary>触发全量压缩时，保留最近多少轮原文不动。</summary>
        public const int KeepRecentRoundsOnCompact = 2;

        private readonly string _file;
        private readonly List<ChatMessage> _turns = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ChatSessionStore(string projectFilePath)
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
            var chatDir = Path.Combine(projectDir, ".chat");
            Directory.CreateDirectory(chatDir);
            _file = Path.Combine(chatDir, "session.json");
            Load();
        }

        /// <summary>较早对话的摘要。空表示还没压缩过。</summary>
        public string Summary { get; private set; } = "";

        /// <summary>摘要覆盖掉的轮数（仅供界面说明）。</summary>
        public int SummarizedRounds { get; private set; }

        /// <summary>摘要之后累积的完整问答对数。</summary>
        public int RoundCount => _turns.Count / 2;

        /// <summary>连摘要都没有、也没有轮次。</summary>
        public bool IsEmpty => _turns.Count == 0 && string.IsNullOrWhiteSpace(Summary);

        /// <summary>最后一次交互时间（用于闲置判断）。从未交互过则为 null。</summary>
        public DateTimeOffset? LastActivityUtc { get; private set; }

        /// <summary>连续压缩失败次数（熔断用，对应 ZCode 的 MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES）。</summary>
        public int ConsecutiveCompactFailures { get; private set; }

        /// <summary>历史（不含摘要）的估算 token。</summary>
        public int EstimatedTokens => TokenEstimator.EstimateAll(_turns.Select(t => t.Content));

        /// <summary>
        /// 交给 API 的历史（已裁剪，**不含摘要**）。
        /// 首条保证是 user —— Anthropic 要求 messages 以 user 开头且同角色不连续。
        /// </summary>
        public IReadOnlyList<ChatMessage> BuildHistory()
        {
            var list = new List<ChatMessage>(_turns);
            Trim(list);
            return list;
        }

        /// <summary>
        /// 该拼进 systemPrompt 的摘要块。没有摘要时返回空串。
        /// 拼在 system 末尾而不是塞进 messages，理由见类注释里的 ⚠。
        /// </summary>
        public string BuildSummaryBlock()
        {
            if (string.IsNullOrWhiteSpace(Summary)) return "";

            var text = ContextSummarizer.FormatSummary(Summary);
            if (text.Length == 0) return "";

            return ContextSummarizer.ContinuationHeader + "\n\n" + text;
        }

        /// <summary>记录一轮完整问答。只有拿到可用回复时才该调用（失败/取消不入历史）。</summary>
        public void Add(string userInput, string assistantReply)
        {
            if (string.IsNullOrWhiteSpace(userInput) || string.IsNullOrWhiteSpace(assistantReply))
                return;

            _turns.Add(ChatMessage.User(userInput));
            _turns.Add(ChatMessage.Assistant(assistantReply));
            LastActivityUtc = DateTimeOffset.UtcNow;
            Trim(_turns);
            Save();
        }

        /// <summary>
        /// 应用一次压缩结果：摘要换新、只留保留的轮次。
        ///
        /// 注意摘要是**滚动**的 —— 再次压缩时，被摘要的材料里应当包含
        /// "上一版摘要 + 上一版摘要之后累积的轮次"（由调用方拼好交给模型），
        /// 再把模型产出的合并摘要交到这里。这样信息不会每压一次就丢一层。
        /// </summary>
        public void ApplySummary(string? newSummary, SummarizePlan plan)
        {
            if (!string.IsNullOrWhiteSpace(newSummary))
                Summary = newSummary.Trim();

            SummarizedRounds += Math.Max(0, plan.RoundsToSummarize);

            _turns.Clear();

            foreach (var m in Filter(plan.Preserved))
                _turns.Add(new ChatMessage(m.Role, m.Content));

            Trim(_turns);
            Save();
        }

        /// <summary>丢掉摘要重新开始累积（摘要写坏了时的逃生口）。</summary>
        public void ResetSummary()
        {
            Summary = "";
            SummarizedRounds = 0;
            Save();
        }

        public void RecordCompactSuccess() => ConsecutiveCompactFailures = 0;

        public void RecordCompactFailure()
        {
            ConsecutiveCompactFailures++;
            Save();
        }

        /// <summary>清空对话（同时删除磁盘上的记录，含摘要）。</summary>
        public void Clear()
        {
            _turns.Clear();
            Summary = "";
            SummarizedRounds = 0;
            ConsecutiveCompactFailures = 0;
            LastActivityUtc = null;
            try
            {
                if (File.Exists(_file)) File.Delete(_file);
            }
            catch { }
        }

        private static void Trim(List<ChatMessage> turns)
        {
            // ① 轮数上限
            int maxMessages = MaxRounds * 2;
            if (turns.Count > maxMessages)
                turns.RemoveRange(0, turns.Count - maxMessages);

            // ② 字符上限：整轮整轮地丢，别在半轮上切
            int total = turns.Sum(m => m.Content?.Length ?? 0);
            while (total > MaxHistoryChars && turns.Count > 2)
            {
                int cut = Math.Min(2, turns.Count - 2);
                total -= turns.Take(cut).Sum(m => m.Content?.Length ?? 0);
                turns.RemoveRange(0, cut);
            }

            // ③ 裁剪后首条必须是 user（RemoveRange 成对进行，理论上已满足，兜一层）
            while (turns.Count > 0 &&
                   !string.Equals(turns[0].Role, "user", StringComparison.OrdinalIgnoreCase))
                turns.RemoveAt(0);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                var json = File.ReadAllText(_file);
                if (string.IsNullOrWhiteSpace(json)) return;

                if (json.TrimStart().StartsWith('{'))
                {
                    // 新格式：带摘要的对象
                    var saved = JsonSerializer.Deserialize<ChatSessionFile>(json, _jsonOptions);
                    if (saved == null) return;

                    Summary = saved.Summary ?? "";
                    SummarizedRounds = Math.Max(0, saved.SummarizedRounds);
                    LastActivityUtc = saved.UpdatedAt;
                    _turns.AddRange(Filter(saved.Turns));
                }
                else
                {
                    // 旧格式：纯消息数组。摘要没有，轮次照收 —— 不丢用户已有的对话。
                    var legacy = JsonSerializer.Deserialize<List<ChatMessage>>(json, _jsonOptions);
                    _turns.AddRange(Filter(legacy));
                }

                Trim(_turns);
            }
            catch
            {
                // 会话记录损坏不该影响主流程，当作空对话继续
                _turns.Clear();
                Summary = "";
            }
        }

        private static IEnumerable<ChatMessage> Filter(IEnumerable<ChatMessage>? items)
        {
            if (items == null) yield break;
            foreach (var m in items)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Content)) continue;
                yield return m;
            }
        }

        private void Save()
        {
            try
            {
                var payload = new ChatSessionFile
                {
                    Version = FormatVersion,
                    Summary = Summary,
                    SummarizedRounds = SummarizedRounds,
                    Turns = new List<ChatMessage>(_turns),
                    UpdatedAt = LastActivityUtc ?? DateTimeOffset.UtcNow
                };
                File.WriteAllText(_file, JsonSerializer.Serialize(payload, _jsonOptions));
            }
            catch { }
        }
    }
}
