using System;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    public interface IApiService
    {
        Task<AiResult> CompleteTextAsync(string prompt, CompletionOptions? options = null);
        Task<AiResult> PolishTextAsync(string text, string? style = null, CancellationToken ct = default, Action<int, int>? onProgress = null);
        Task<AiResult> ContinueWritingAsync(string context, string? direction = null, CancellationToken ct = default, Action<int, int>? onProgress = null);
        Task<AiResult> GenerateCharacterAsync(string description, CancellationToken ct = default, Action<int, int>? onProgress = null);
        Task<AiResult> GeneratePlotAsync(string theme, string genre, CancellationToken ct = default, Action<int, int>? onProgress = null);
        Task<AiResult> GenerateDialogueAsync(string character1, string character2, string situation, CancellationToken ct = default, Action<int, int>? onProgress = null);
    }

    public class AiResult
    {
        public string Text { get; set; } = "";
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    public class CompletionOptions
    {
        public int MaxTokens { get; set; } = 1000;
        public double Temperature { get; set; } = 0.7;
        public string Model { get; set; } = "";
        public CancellationToken CancellationToken { get; set; } = default;
        public Action<int, int>? OnProgress { get; set; }
    }

    public static class KnownProviders
    {
        public const string OpenAI = "OpenAI";
        public const string DeepSeek = "DeepSeek";
        public const string Claude = "Claude";
        public const string Mimo = "Mimo";
        public const string SiliconFlow = "SiliconFlow";
        public const string Custom = "Custom";

        public static (string Name, string DefaultUrl, string DefaultModel)[] All => new[]
        {
            (OpenAI, "https://api.openai.com/v1/chat/completions", "gpt-3.5-turbo"),
            (DeepSeek, "https://api.deepseek.com/v1/chat/completions", "deepseek-chat"),
            (Claude, "https://api.anthropic.com/v1/messages", "claude-3-haiku-20240307"),
            (Mimo, "https://token-plan-cn.xiaomimimo.com/anthropic/v1/messages", "mimo-v2.5-pro"),
            (SiliconFlow, "https://api.siliconflow.cn/v1/chat/completions", "deepseek-llm-67b-chat"),
            (Custom, "", ""),
        };

        public static bool UsesAnthropicFormat(string provider) =>
            provider is Claude or Mimo;
    }

    public class ApiConfig
    {
        public string ApiKey { get; set; } = "";
        public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
        public string Model { get; set; } = "gpt-3.5-turbo";
        public string Provider { get; set; } = KnownProviders.OpenAI;
    }
}