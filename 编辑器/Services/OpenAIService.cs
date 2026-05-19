using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
        }

        // 兼容旧版：仅传 API Key 时使用默认配置
        public OpenAIService(string apiKey) : this(new ApiConfig { ApiKey = apiKey })
        {
        }

        public async Task<string> CompleteTextAsync(string prompt, CompletionOptions? options = null)
        {
            options ??= new CompletionOptions();
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : _config.Model;

            var request = new
            {
                model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                max_tokens = options.MaxTokens,
                temperature = options.Temperature
            };

            return await SendRequestAsync(request);
        }

        public async Task<string> PolishTextAsync(string text, string? style = null)
        {
            string prompt = $"请润色以下文本，使其更加流畅和生动{(string.IsNullOrEmpty(style) ? "" : $"，风格为：{style}")}：\n\n{text}";
            return await CompleteTextAsync(prompt);
        }

        public async Task<string> ContinueWritingAsync(string context, string? direction = null)
        {
            string prompt = $"基于以下内容继续写作，保持风格一致{(string.IsNullOrEmpty(direction) ? "" : $"，发展方向：{direction}")}：\n\n{context}";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000 });
        }

        public async Task<string> GenerateCharacterAsync(string description)
        {
            string prompt = $"根据以下描述生成一个详细的角色设定，包括姓名、年龄、外貌、性格、背景故事等：\n\n{description}";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 1500 });
        }

        public async Task<string> GeneratePlotAsync(string theme, string genre)
        {
            string prompt = $"基于主题'{theme}'和类型'{genre}'，生成一个详细的故事情节大纲，包括主要冲突、转折点、高潮和结局：";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000 });
        }

        public async Task<string> GenerateDialogueAsync(string character1, string character2, string situation)
        {
            string prompt = $"生成{character1}和{character2}在以下情境中的对话：\n\n{situation}";
            return await CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 1500 });
        }

        private async Task<string> SendRequestAsync(object request)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_config.ApiUrl, content);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var responseObject = JsonSerializer.Deserialize<JsonElement>(responseContent);

                return responseObject.GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return $"API调用失败: {ex.Message}";
            }
        }
    }
}
