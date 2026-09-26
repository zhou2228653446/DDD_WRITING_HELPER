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
    public class AnthropicService : IApiService
    {
        private readonly HttpClient _httpClient;
        private readonly ApiConfig _config;

        public AnthropicService(ApiConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new ArgumentException("API Key 不能为空", nameof(config));

            _httpClient = new HttpClient();
            // 认证头由 ApiProviders 按服务商决定：
            // Anthropic 官方是 x-api-key + anthropic-version，
            // 小米 Mimo Token Plan 是 api-key 且不带 anthropic-version，
            // 中转服务也可能要求 Authorization: Bearer。
            ApiProviders.ApplyHeaders(_httpClient.DefaultRequestHeaders, config);
            ApiProviders.ApplyExtraHeaders(_httpClient.DefaultRequestHeaders, config);
        }

        public async Task<AiResult> CompleteTextAsync(string prompt, string? systemPrompt = null, CompletionOptions? options = null)
        {
            options ??= new CompletionOptions();
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : _config.Model;

            // 注意：Anthropic 的 system 是**顶层字段**，不是 messages 里的一条消息
            //（messages 只接受 user / assistant 两种 role，塞 system 进去会被 400 拒绝）。
            var request = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["stream"] = true,
                ["max_tokens"] = options.MaxTokens,
                ["temperature"] = options.Temperature,
                ["messages"] = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            if (!string.IsNullOrWhiteSpace(systemPrompt))
                request["system"] = systemPrompt;

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
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    return new AiResult { Text = $"API错误 ({(int)response.StatusCode}): {errBody}" };
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();
                int inputTokens = 0, outputTokens = 0;

                while (true)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line[6..].Trim();

                    JsonElement jsonObj;
                    try { jsonObj = JsonSerializer.Deserialize<JsonElement>(data); }
                    catch { continue; }

                    if (!jsonObj.TryGetProperty("type", out var typeElement))
                        continue;

                    var eventType = typeElement.GetString();

                    switch (eventType)
                    {
                        // message_start → 包含 input_tokens
                        case "message_start":
                            if (jsonObj.TryGetProperty("message", out var msg) &&
                                msg.TryGetProperty("usage", out var startUsage))
                            {
                                inputTokens = startUsage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                                onProgress?.Invoke(inputTokens, 0);
                            }
                            break;

                        // content_block_delta → 文本增量
                        case "content_block_delta":
                            if (jsonObj.TryGetProperty("delta", out var delta) &&
                                delta.TryGetProperty("text", out var textElement) &&
                                textElement.ValueKind == JsonValueKind.String)
                            {
                                var token = textElement.GetString();
                                if (!string.IsNullOrEmpty(token))
                                {
                                    sb.Append(token);
                                    // 估算输出 token（中文约 1~2 字/token，取 1.5）
                                    int estimated = (int)(sb.Length / 1.5);
                                    if (estimated > outputTokens)
                                    {
                                        outputTokens = estimated;
                                        onProgress?.Invoke(inputTokens, outputTokens);
                                    }
                                }
                            }
                            break;

                        // message_delta → 包含最终 output_tokens
                        case "message_delta":
                            if (jsonObj.TryGetProperty("usage", out var deltaUsage) &&
                                deltaUsage.TryGetProperty("output_tokens", out var ot))
                            {
                                outputTokens = ot.GetInt32();
                                onProgress?.Invoke(inputTokens, outputTokens);
                            }
                            break;
                    }
                }

                // 如果没有从流中获得 output_tokens，用估算值
                if (outputTokens == 0)
                    outputTokens = (int)(sb.Length / 1.5);

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
