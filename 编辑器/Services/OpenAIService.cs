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

        public async Task<AiResult> CompleteTextAsync(string prompt, string? systemPrompt = null, CompletionOptions? options = null)
        {
            options ??= new CompletionOptions();
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : _config.Model;

            // 消息顺序固定为 system → user。system 段是跨请求最稳定的前缀，
            // 放在最前面才能命中服务端的 prompt caching（长上下文项目省 token 也省延迟）。
            var messages = new List<object>(2);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
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
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();
                int inputTokens = 0, outputTokens = 0;
                bool done = false;

                while (!done)
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
                        onProgress?.Invoke(inputTokens, outputTokens);
                        done = true;
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
                            done = true;
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
                    TotalTokens = inputTokens + outputTokens
                };
            }
            catch (OperationCanceledException)
            {
                return new AiResult { Text = "[已停止生成]" };
            }
            catch (Exception ex)
            {
                return new AiResult { Text = $"API调用失败: {ex.Message}" };
            }
        }
    }
}
