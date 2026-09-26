using System;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    public interface IApiService
    {
        /// <summary>
        /// 发送一次补全请求。
        /// </summary>
        /// <param name="prompt">user 消息内容（作者的实际请求 + 待处理文本）。</param>
        /// <param name="systemPrompt">
        /// system 消息内容（身份 / 项目设定 / 任务说明 / 输出契约）。
        /// 为空表示不发送 system 消息，退回单纯 user 消息的老行为。
        /// </param>
        /// <param name="options">采样参数与取消令牌。</param>
        Task<AiResult> CompleteTextAsync(string prompt, string? systemPrompt = null, CompletionOptions? options = null);
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

    public class ApiConfig
    {
        public string ApiKey { get; set; } = "";

        public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";

        /// <summary>
        /// 模型名。**不要改大小写** —— 部分服务商的模型名区分大小写
        /// （硅基流动的 deepseek-ai/DeepSeek-V3、MiniMax 的 MiniMax-Text-01），
        /// 早期的代码在这里做了 ToLowerInvariant()，会直接把请求打成 404。
        /// </summary>
        public string Model { get; set; } = "gpt-4o-mini";

        /// <summary>
        /// 服务商标识（<see cref="ProviderPreset.Id"/>，如 deepseek / anthropic / openai）。
        /// 旧配置文件里存的是显示名（OpenAI / DeepSeek / Claude），查找时同样认。
        /// </summary>
        public string Provider { get; set; } = ApiProviders.IdOpenAi;

        /// <summary>
        /// 认证方式覆盖，空表示按服务商预设自动决定。
        /// 取值：空 / "Bearer" / "XApiKey" / "ApiKeyHeader"。
        /// </summary>
        public string AuthOverride { get; set; } = "";
    }
}