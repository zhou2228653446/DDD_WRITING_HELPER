using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace 编辑器.Services
{
    public class AiMemoryManager
    {
        private readonly string _memoryFile;
        private string _memoryContent = "";
        private int _pendingCount;
        private readonly List<string> _pendingInteractions = new();

        private const int UpdateThreshold = 5;

        public AiMemoryManager(string projectFilePath)
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
            var memoryDir = Path.Combine(projectDir, ".ai_memory");
            Directory.CreateDirectory(memoryDir);
            _memoryFile = Path.Combine(memoryDir, "memory.md");
        }

        /// <summary>加载记忆文件</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(_memoryFile))
                    _memoryContent = File.ReadAllText(_memoryFile);
                else
                    _memoryContent = "";
            }
            catch
            {
                _memoryContent = "";
            }
        }

        /// <summary>获取记忆内容，用于注入 AI 上下文</summary>
        public string GetMemoryContext()
        {
            if (string.IsNullOrWhiteSpace(_memoryContent))
                return "";

            return "===== AI 记忆（写作经验和偏好） =====\n\n"
                + _memoryContent.Trim()
                + "\n\n========================================\n\n";
        }

        /// <summary>记录一次交互</summary>
        public void RecordInteraction(string type, string input, string result)
        {
            var summary = $"[{type}] 输入：{(string.IsNullOrWhiteSpace(input) ? "（无）" : input)} → AI回复摘要：{Truncate(result, 100)}";
            _pendingInteractions.Add(summary);
            _pendingCount++;
        }

        /// <summary>是否需要触发记忆更新</summary>
        public bool ShouldUpdateMemory() => _pendingCount >= UpdateThreshold;

        /// <summary>构建记忆更新 prompt</summary>
        public string BuildMemoryUpdatePrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一个写作助手的记忆管理器。请根据以下最近的交互记录，总结出对后续写作有用的经验和用户偏好。");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(_memoryContent))
            {
                sb.AppendLine("【已有记忆】");
                sb.AppendLine(_memoryContent.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("【最近交互记录】");
            foreach (var interaction in _pendingInteractions)
                sb.AppendLine($"- {interaction}");

            sb.AppendLine();
            sb.AppendLine("请输出更新后的完整记忆内容（直接输出内容，不要额外说明）。");
            sb.AppendLine("要求：");
            sb.AppendLine("1. 保留仍然有效的旧记忆");
            sb.AppendLine("2. 融入新发现的经验和偏好");
            sb.AppendLine("3. 去除重复或已过时的内容");
            sb.AppendLine("4. 使用简洁的条目式记录");

            return sb.ToString();
        }

        /// <summary>保存 AI 生成的新记忆</summary>
        public void SaveMemory(string newContent)
        {
            try
            {
                _memoryContent = newContent.Trim();
                File.WriteAllText(_memoryFile, _memoryContent);
                _pendingInteractions.Clear();
                _pendingCount = 0;
            }
            catch { }
        }

        /// <summary>手动设置记忆内容</summary>
        public void SetMemory(string content)
        {
            _memoryContent = content ?? "";
            try
            {
                File.WriteAllText(_memoryFile, _memoryContent);
            }
            catch { }
        }

        /// <summary>获取原始记忆内容</summary>
        public string GetRawMemory() => _memoryContent;

        /// <summary>清空记忆</summary>
        public void ClearMemory()
        {
            _memoryContent = "";
            _pendingInteractions.Clear();
            _pendingCount = 0;
            try
            {
                if (File.Exists(_memoryFile))
                    File.Delete(_memoryFile);
            }
            catch { }
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLen ? text : text[..maxLen] + "...";
        }
    }
}
