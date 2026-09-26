using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>
    /// 「万能聊天」的多轮对话记忆，按项目保存在 &lt;项目目录&gt;/.chat/session.json。
    ///
    /// ★ 为什么需要它：大模型 API 是**无状态**的，服务端不记得上一轮说过什么。
    ///   所谓"连续对话"，本质是客户端每次请求都把此前所有轮次重发一遍。
    ///   在此之前，每次调用只发 system + 本轮 user，所以每问一句都相当于开了一个
    ///   全新对话 —— 这不是"上下文长度怎么定"的问题，而是压根没有上下文。
    ///
    /// 只保留最近若干轮，两道闸门（谁先到算谁）：
    ///   · <see cref="MaxRounds"/>      —— 轮数上限，越往前越容易失真；
    ///   · <see cref="MaxHistoryChars"/> —— 字符上限，防止"让 AI 写长文"几个来回就把窗口撑爆。
    /// 注意这里裁的是**对话**，不含每轮都会带上的设定与正文，那些另有各自主的预算。
    /// </summary>
    public class ChatSessionStore
    {
        /// <summary>最多保留的对话轮数（1 轮 = 1 问 + 1 答）。</summary>
        public const int MaxRounds = 12;

        /// <summary>历史文本总量上限（字符）。超出就从最早的一轮开始丢。</summary>
        public const int MaxHistoryChars = 24000;

        private readonly string _file;
        private readonly List<ChatMessage> _turns = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public ChatSessionStore(string projectFilePath)
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
            var chatDir = Path.Combine(projectDir, ".chat");
            Directory.CreateDirectory(chatDir);
            _file = Path.Combine(chatDir, "session.json");
            Load();
        }

        /// <summary>完整的问答对数（用户已经问过多少句）。</summary>
        public int RoundCount => _turns.Count / 2;

        public bool IsEmpty => _turns.Count == 0;

        /// <summary>
        /// 交给 API 的历史（已裁剪）。首条保证是 user —— Anthropic 要求 messages
        /// 以 user 开头且同角色不连续，裁掉半轮会直接 400。
        /// </summary>
        public IReadOnlyList<ChatMessage> BuildHistory()
        {
            var list = new List<ChatMessage>(_turns);
            Trim(list);
            return list;
        }

        /// <summary>记录一轮完整问答。只有拿到可用回复时才该调用（失败/取消不入历史）。</summary>
        public void Add(string userInput, string assistantReply)
        {
            if (string.IsNullOrWhiteSpace(userInput) || string.IsNullOrWhiteSpace(assistantReply))
                return;

            _turns.Add(ChatMessage.User(userInput));
            _turns.Add(ChatMessage.Assistant(assistantReply));
            Trim(_turns);
            Save();
        }

        /// <summary>清空对话（同时删除磁盘上的记录）。</summary>
        public void Clear()
        {
            _turns.Clear();
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
                var saved = JsonSerializer.Deserialize<List<ChatMessage>>(json, _jsonOptions);
                if (saved == null) return;

                _turns.AddRange(saved.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content)));
                Trim(_turns);
            }
            catch
            {
                // 会话记录损坏不该影响主流程，当作空对话继续
                _turns.Clear();
            }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(_turns, _jsonOptions));
            }
            catch { }
        }
    }
}
