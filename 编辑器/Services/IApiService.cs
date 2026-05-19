using System;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    public interface IApiService
    {
        Task<string> CompleteTextAsync(string prompt, CompletionOptions? options = null);
        Task<string> PolishTextAsync(string text, string? style = null);
        Task<string> ContinueWritingAsync(string context, string? direction = null);
        Task<string> GenerateCharacterAsync(string description);
        Task<string> GeneratePlotAsync(string theme, string genre);
        Task<string> GenerateDialogueAsync(string character1, string character2, string situation);
    }

    public class CompletionOptions
    {
        public int MaxTokens { get; set; } = 1000;
        public double Temperature { get; set; } = 0.7;
        public string Model { get; set; } = "";
    }

    public static class KnownProviders
    {
        public const string OpenAI = "OpenAI";
        public const string DeepSeek = "DeepSeek";
        public const string Claude = "Claude";
        public const string SiliconFlow = "SiliconFlow";
        public const string Custom = "Custom";

        public static (string Name, string DefaultUrl, string DefaultModel)[] All => new[]
        {
            (OpenAI, "https://api.openai.com/v1/chat/completions", "gpt-3.5-turbo"),
            (DeepSeek, "https://api.deepseek.com/v1/chat/completions", "deepseek-chat"),
            (Claude, "https://api.anthropic.com/v1/messages", "claude-3-haiku-20240307"),
            (SiliconFlow, "https://api.siliconflow.cn/v1/chat/completions", "deepseek-llm-67b-chat"),
            (Custom, "", ""),
        };
    }

    public class ApiConfig
    {
        public string ApiKey { get; set; } = "";
        public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
        public string Model { get; set; } = "gpt-3.5-turbo";
        public string Provider { get; set; } = KnownProviders.OpenAI;
    }
}