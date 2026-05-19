using System;
using System.IO;

namespace 编辑器.Services
{
    public class ChatLogger
    {
        private readonly string _filePath;

        public ChatLogger(string projectFilePath)
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
            var logDir = Path.Combine(projectDir, ".chat");
            Directory.CreateDirectory(logDir);
            _filePath = Path.Combine(logDir, "ai_log.md");

            if (!File.Exists(_filePath))
                File.WriteAllText(_filePath, "# AI 对话记录\n\n");
        }

        public void Log(string functionType, string userInput, string aiResponse)
        {
            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var inputPreview = string.IsNullOrWhiteSpace(userInput) ? "（无）" : userInput;

            var entry = $"""
## [{time}] {functionType}

**用户输入：**
{inputPreview}

**AI 回复：**
{aiResponse}

---
""";
            File.AppendAllText(_filePath, entry);
        }
    }
}
