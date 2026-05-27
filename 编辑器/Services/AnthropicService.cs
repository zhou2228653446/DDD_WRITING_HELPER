using System;
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
            // Mimo Token Plan 使用 api-key 头认证，不发送 anthropic-version
            if (config.Provider == KnownProviders.Mimo)
            {
                _httpClient.DefaultRequestHeaders.Add("api-key", config.ApiKey);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
                _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
        }

        public async Task<AiResult> CompleteTextAsync(string prompt, CompletionOptions? options = null)
        {
            options ??= new CompletionOptions();
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : _config.Model;

            var request = new
            {
                model,
                stream = true,
                max_tokens = options.MaxTokens,
                temperature = options.Temperature,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            return await SendStreamingRequestAsync(request, options.CancellationToken, options.OnProgress);
        }

        public async Task<AiResult> PolishTextAsync(string text, string? style = null, CancellationToken ct = default, Action<int, int>? onProgress = null)
        {
            string prompt = $"请润色以下文本，使其更加流畅和生动{(string.IsNullOrEmpty(style) ? "" : $"，风格为：{style}")}：\n\n{text}";
            return await CompleteTextAsync(prompt, new CompletionOptions { CancellationToken = ct, OnProgress = onProgress });
        }

        public async Task<AiResult> ContinueWritingAsync(string context, string? direction = null, CancellationToken ct = default, Action<int, int>? onProgress = null)
        {
            string prompt = $"基于以下内容继续写作，保持风格一致{(string.IsNullOrEmpty(direction) ? "" : $"，发展方向：{direction}")}：\n\n{context}";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000, CancellationToken = ct, OnProgress = onProgress });
        }

        public async Task<AiResult> GenerateCharacterAsync(string description, CancellationToken ct = default, Action<int, int>? onProgress = null)
        {
            string prompt = $"根据以下描述生成一个详细的角色设定，包括姓名、年龄、外貌、性格、背景故事等：\n\n{description}";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 1500, CancellationToken = ct, OnProgress = onProgress });
        }

        public async Task<AiResult> GeneratePlotAsync(string theme, string genre, CancellationToken ct = default, Action<int, int>? onProgress = null)
        {
            string prompt = $"基于主题'{theme}'和类型'{genre}'，生成一个详细的故事情节大纲，包括主要冲突、转折点、高潮和结局：";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000, CancellationToken = ct, OnProgress = onProgress });
        }

        public async Task<AiResult> GenerateDialogueAsync(string character1, string character2, string situation, CancellationToken ct = default, Action<int, int>? onProgress = null)
        {
            string prompt = $"生成{character1}和{character2}在以下情境中的对话：\n\n{situation}";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 1500, CancellationToken = ct, OnProgress = onProgress });
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
