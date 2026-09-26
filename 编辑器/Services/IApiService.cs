using System;
using System.Collections.Generic;
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
        /// <param name="history">
        /// 此前的多轮对话（user / assistant 交替，按时间正序）。
        /// 会插在 system 之后、本轮 <paramref name="prompt"/> 之前。
        ///
        /// ★ 大模型 API 是**无状态**的：服务端不记得上一轮说过什么，
        ///   所谓"连续对话"就是客户端每次都把完整历史重发一遍。
        ///   不传 history 时每次请求都是全新的一问一答（这也是本参数存在的原因）。
        /// </param>
        Task<AiResult> CompleteTextAsync(
            string prompt,
            string? systemPrompt = null,
            CompletionOptions? options = null,
            IReadOnlyList<ChatMessage>? history = null);
    }

    /// <summary>
    /// 把服务端的错误响应压成一行**可读**文案。
    ///
    /// 两个 Service 都走这里，避免"同一个错误、两个协议、两种体验"。
    /// 大意：能从 JSON 里捞出 error.message 就捞（各家的字段名不统一，多试几个），
    /// 捞不到就退化成原始正文，最后统一压成单行并截断，交给弹窗/回复框显示。
    /// </summary>
    public static class ApiErrors
    {
        private const int MaxBodyChars = 500;

        public static string Describe(System.Net.HttpStatusCode status, string? body)
        {
            var detail = ExtractMessage(body);
            var hint = Hint(status, detail);

            // 有些网关失败时返回空 body（或只有一行 HTML 注释），
            // 这时别让上层拼出 "API错误 (500): " 这种没有下文的残句。
            if (string.IsNullOrWhiteSpace(detail))
                return string.IsNullOrWhiteSpace(hint) ? "服务端未返回错误详情" : hint;

            var text = $"{detail}{(string.IsNullOrEmpty(hint) ? "" : $"（{hint}）")}";
            return text.Length > MaxBodyChars ? text[..MaxBodyChars] + "…" : text;
        }

        /// <summary>
        /// 从错误体里取人话。OpenAI 风格是 {"error":{"message":"…"}}，
        /// 但也有 {"message":"…"} / {"error":"…"} 甚至纯文本（网关的 HTML 错误页）。
        /// </summary>
        private static string ExtractMessage(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var err))
                    {
                        if (err.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            err.TryGetProperty("message", out var em) &&
                            em.ValueKind == System.Text.Json.JsonValueKind.String)
                            return Collapse(em.GetString());

                        if (err.ValueKind == System.Text.Json.JsonValueKind.String)
                            return Collapse(err.GetString());
                    }

                    if (root.TryGetProperty("message", out var m) &&
                        m.ValueKind == System.Text.Json.JsonValueKind.String)
                        return Collapse(m.GetString());
                }
            }
            catch
            {
                // 不是 JSON（HTML 错误页、纯文本），走下面的兜底
            }

            return Collapse(body);
        }

        /// <summary>按状态码 / 关键词补一句人话提示，帮助用户知道该去改哪一项。</summary>
        private static string Hint(System.Net.HttpStatusCode status, string detail)
        {
            var d = detail.ToLowerInvariant();

            if (status == System.Net.HttpStatusCode.Unauthorized || status == System.Net.HttpStatusCode.Forbidden)
                return "API Key 无效或没有该模型的权限";
            if (status == System.Net.HttpStatusCode.NotFound)
                return "地址或模型名不对";
            if ((int)status == 429)
                return "触发限流，稍后再试";
            if ((int)status == 402 || d.Contains("insufficient") || d.Contains("balance") || d.Contains("quota"))
                return "账户余额 / 额度不足";

            if (d.Contains("context") && (d.Contains("length") || d.Contains("exceed") || d.Contains("too long")))
                return "上下文超出模型窗口，请取消勾选部分参考章节或缩短正文";
            if (d.Contains("model") && (d.Contains("not found") || d.Contains("not exist") || d.Contains("invalid")))
                return "模型名不对，可在 AI 设置里点「拉取列表」选真实型号";

            return "";
        }

        private static string Collapse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>一轮对话消息。Role 取 "user" / "assistant"（system 单独走 systemPrompt 参数）。</summary>
    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";

        public ChatMessage() { }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public static ChatMessage User(string content) => new("user", content);
        public static ChatMessage Assistant(string content) => new("assistant", content);

        /// <summary>
        /// 把历史轮次按原样追加到 messages —— 两个协议共用，保证行为一致。
        ///
        /// 只做两件必要的清理：丢掉空内容、把 role 归一到 user/assistant。
        /// **不要在这里裁剪长度**：裁多少轮由上层（ChatSessionStore）决定，那里才知道预算；
        /// 而且截断后如果首条变成 assistant，Anthropic 会直接 400。
        /// </summary>
        public static void AppendTo(List<object> messages, IReadOnlyList<ChatMessage>? history)
        {
            if (history == null) return;
            foreach (var m in history)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Content)) continue;
                var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "assistant" : "user";
                messages.Add(new { role, content = m.Content });
            }
        }
    }

    public class AiResult
    {
        public string Text { get; set; } = "";
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }

        /// <summary>
        /// 这次调用**失败**了（网络异常 / 401 / 400 …），<see cref="Text"/> 里是错误说明而不是正文。
        ///
        /// ★ 两个 Service 把失败包成 AiResult 返回（而不是抛异常），所以"结果非 null"
        ///   并不等于成功。凡是要把 Text 写回章节正文或设定的地方，必须先判这个标记 ——
        ///   否则一次 401 就能把整章正文/整份大纲换成"API调用失败: ..."。
        /// </summary>
        public bool IsError { get; set; }

        /// <summary>被用户主动停止。<see cref="Text"/> 里是占位文案，同样不可写回。</summary>
        public bool IsCanceled { get; set; }

        /// <summary>
        /// 输出撞到了 max_tokens 上限（finish_reason=length / stop_reason=max_tokens），
        /// 正文被**截断**。改写类任务（润色）此时覆盖原文等于丢内容。
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>正文可直接使用（非失败、非取消）。截断与否由调用方按任务性质自己决定。</summary>
        public bool IsUsable => !IsError && !IsCanceled;
    }

    public class CompletionOptions
    {
        public int MaxTokens { get; set; } = 1000;
        public double Temperature { get; set; } = 0.7;
        public string Model { get; set; } = "";
        public CancellationToken CancellationToken { get; set; } = default;
        public Action<int, int>? OnProgress { get; set; }

        /// <summary>
        /// 改写类任务（原文进、同量文本出）的输出预算。
        ///
        /// 不能写死小数值：中文约 1 字 ≈ 1 token，一份 3000 字的章节要 3000 左右输出，
        /// 而"润色"是**整章覆盖**，给 1000 就会被截断后覆盖掉原文。
        /// 这里按原文长度留 2 倍余量（润色后可能变长），并夹在 [floor, 8000]
        /// —— 多数服务商单次输出硬上限就在 4k~8k，要更多也没用。
        /// </summary>
        public static int BudgetForRewrite(string? source, int floor = 1200)
        {
            long estimate = (long)Math.Ceiling((source?.Length ?? 0) * 2.0);
            return (int)Math.Clamp(estimate, floor, 8000);
        }
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