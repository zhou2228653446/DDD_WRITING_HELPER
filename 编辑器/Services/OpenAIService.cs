using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    public class OpenAIService : IApiService
    {
        /// <summary>
        /// 收到 finish_reason 之后，最多再读多少行继续捞 usage 收尾包。
        /// 官方在 finish_reason 之后会补一个只带 usage 的包；某些网关什么都不补、
        /// 直接关流。给个小上限，两种情况都不会卡住。
        /// </summary>
        private const int GraceLinesAfterFinish = 20;

        private readonly HttpClient _httpClient;
        private readonly ApiConfig _config;

        public OpenAIService(ApiConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new ArgumentException("API Key 不能为空", nameof(config));

            _httpClient = new HttpClient();
            // 认证头交给 ApiProviders 统一装配：Bearer / x-api-key / api-key 三种风格，
            // 加上服务商额外要求的头（OpenRouter 的 HTTP-Referer 等）。
            ApiProviders.ApplyHeaders(_httpClient.DefaultRequestHeaders, config);
            ApiProviders.ApplyExtraHeaders(_httpClient.DefaultRequestHeaders, config);
        }

        // 兼容旧版：仅传 API Key 时使用默认配置
        public OpenAIService(string apiKey) : this(new ApiConfig { ApiKey = apiKey })
        {
        }

        public async Task<AiResult> CompleteTextAsync(
            string prompt,
            string? systemPrompt = null,
            CompletionOptions? options = null,
            IReadOnlyList<ChatMessage>? history = null)
        {
            options ??= new CompletionOptions();
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : _config.Model;

            // 消息顺序固定为 system → 历史多轮 → 本轮 user。system 段是跨请求最稳定的前缀，
            // 放在最前面才能命中服务端的 prompt caching（长上下文项目省 token 也省延迟）。
            var messages = new List<object>(2 + (history?.Count ?? 0));
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            ChatMessage.AppendTo(messages, history);
            messages.Add(new { role = "user", content = prompt });

            var request = new
            {
                model,
                stream = true,
                stream_options = new { include_usage = true },
                messages,
                max_tokens = options.MaxTokens,
                temperature = options.Temperature
            };

            return await SendStreamingRequestAsync(request, options.CancellationToken, options.OnProgress);
        }

        private async Task<AiResult> SendStreamingRequestAsync(object request, CancellationToken ct, Action<int, int>? onProgress)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, _config.ApiUrl) { Content = content };
                var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

                // 不要用 EnsureSuccessStatusCode()：它会把服务端返回的正文丢掉，
                // 用户只能看到一个裸状态码（"400 (Bad Request)"），而真正有用的
                // 说明恰好就在 body 里 —— "context length exceeded"、"model not found"、
                // "insufficient balance" 全都是这一类。Anthropic 分支一直是读 body 的，
                // 这里对齐，否则同一个错误在两个协议下体验完全不同。
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    return new AiResult
                    {
                        Text = $"API错误 ({(int)response.StatusCode}): {ApiErrors.Describe(response.StatusCode, errBody)}",
                        IsError = true
                    };
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();
                int inputTokens = 0, outputTokens = 0;
                bool finished = false;      // 已收到 finish_reason
                bool usageSeen = false;     // 已收到 usage 收尾包
                bool truncated = false;
                int graceLines = 0;

                // ★ 循环退出的条件不能是"收到 finish_reason"：官方在 finish_reason 之后
                //   还会再发一个只带 usage 的收尾包（stream_options.include_usage=true 时），
                //   一收到 finish_reason 就 break，input_tokens 会永远是 0 ——
                //   状态栏于是长年显示"输入 0 + 输出 N"，看着像模型没吃上下文。
                //   但也不能无限等：有些网关既不发 usage 也不发 [DONE]，只关连接。
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line[6..].Trim();
                    if (data == "[DONE]") break;

                    JsonElement jsonObj;
                    try { jsonObj = JsonSerializer.Deserialize<JsonElement>(data); }
                    catch { continue; }

                    // 提取 usage（OpenAI 在最后一个 chunk 返回）
                    if (jsonObj.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : inputTokens;
                        outputTokens = usage.TryGetProperty("completion_tokens", out var cpt) ? cpt.GetInt32() : outputTokens;
                        usageSeen = true;
                        onProgress?.Invoke(inputTokens, outputTokens);
                    }

                    // 提取 delta.content
                    if (jsonObj.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var contentElement) &&
                            contentElement.ValueKind == JsonValueKind.String)
                        {
                            var token = contentElement.GetString();
                            if (!string.IsNullOrEmpty(token))
                            {
                                sb.Append(token);
                                // 估算输出 token（中文约 1~2 字/token，取 1.5）
                                int estimated = (int)(sb.Length / 1.5);
                                if (estimated > outputTokens)
                                    onProgress?.Invoke(inputTokens, estimated);
                            }
                        }

                        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        {
                            // "length" = 撞到 max_tokens 被截断。上层据此决定要不要覆盖原文。
                            truncated = string.Equals(fr.GetString(), "length", StringComparison.OrdinalIgnoreCase);
                            finished = true;
                        }
                    }

                    if (finished)
                    {
                        if (usageSeen) break;
                        if (++graceLines > GraceLinesAfterFinish) break;
                    }
                }

                // 如果流中没有返回 usage，用估算值
                if (outputTokens == 0)
                    outputTokens = (int)(sb.Length / 1.5);
                if (inputTokens == 0 && outputTokens > 0)
                    onProgress?.Invoke(0, outputTokens);

                return new AiResult
                {
                    Text = sb.ToString(),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = inputTokens + outputTokens,
                    Truncated = truncated
                };
            }
            catch (OperationCanceledException)
            {
                return new AiResult { Text = "[已停止生成]", IsCanceled = true };
            }
            catch (Exception ex)
            {
                return new AiResult { Text = $"API调用失败: {ex.Message}", IsError = true };
            }
        }
    }
}
