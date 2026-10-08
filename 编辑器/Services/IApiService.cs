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
        /// system 消息内容（身份 / 项目设定 / 任务说明 / 输出契约）。支持**分段**，见
        /// <see cref="SystemPrompt"/> —— 分段是为了让 Anthropic 的 prompt caching 能
        /// 在"稳定段"上命中。为空表示不发送 system 消息，退回单纯 user 消息的老行为。
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
            SystemPrompt? systemPrompt = null,
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

    /// <summary>
    /// system 提示词的一段。<paramref name="Cacheable"/> = 这一段是否值得打缓存断点。
    /// </summary>
    /// <param name="Text">本段正文。</param>
    /// <param name="Cacheable">
    /// 是否在本段末尾打 prompt-caching 断点。判据是**这一段会不会跨请求复用**
    /// （身份、项目设定这类每轮都一样 → true；勾选章节正文这类每次都在变 → false）。
    /// 打多了无害（Anthropic 对不足最小长度的段会静默跳过），打错了只是白花钱。
    /// </param>
    public readonly record struct SystemPromptPart(string Text, bool Cacheable);

    /// <summary>
    /// 分段组织的 system 提示词。
    ///
    /// ★ 为什么要分段：Anthropic 的 prompt caching 是**前缀缓存** ——
    ///   只有显式在某个 block 上打 <c>cache_control: {"type":"ephemeral"}</c>，
    ///   服务端才会把「从开头到该 block 为止」的内容缓存下来。仅仅把稳定内容放在前面
    ///   **是不够的**（这是本类出现前的误解），不打标记等于完全不缓存。
    ///   分段之后，只要前面的段没变，后面的段变了也不影响前面命中。
    ///
    /// 从 <see cref="string"/> 有隐式转换，所以既有调用点（传一个字符串）
    /// 一行都不用改 —— 它们会得到"单段、可缓存"，语义与改造前一致。
    /// </summary>
    public sealed class SystemPrompt
    {
        public IReadOnlyList<SystemPromptPart> Parts { get; }

        public SystemPrompt(IReadOnlyList<SystemPromptPart> parts)
        {
            Parts = parts ?? Array.Empty<SystemPromptPart>();
        }

        /// <summary>没有任何有效内容（全是空白）→ 调用方应不发送 system。</summary>
        public bool IsEmpty => Parts.All(p => string.IsNullOrWhiteSpace(p.Text));

        /// <summary>
        /// 拼成一整块纯文本。给**不支持分段**的协议（OpenAI 兼容）与本地 token 估算用。
        /// 段间用空行连接，与改造前"一整块 system"的形态保持一致。
        /// </summary>
        public string Flatten() => string.Join("\n\n",
            Parts.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim()));

        public static implicit operator SystemPrompt?(string? text) => FromText(text);

        public static SystemPrompt? FromText(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? null
                : new SystemPrompt(new[] { new SystemPromptPart(text, true) });

        /// <summary>追加一段。返回新实例（本类型当作不可变用）。</summary>
        public SystemPrompt Append(string? text, bool cacheable = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return this;
            var list = new List<SystemPromptPart>(Parts) { new(text, cacheable) };
            return new SystemPrompt(list);
        }
    }

    public class AiResult
    {
        public string Text { get; set; } = "";
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }

        /// <summary>
        /// 输入里**命中缓存**的那部分 token（Anthropic 的 <c>cache_read_input_tokens</c>、
        /// OpenAI 的 <c>prompt_tokens_details.cached_tokens</c>）。
        /// 它已经包含在 <see cref="InputTokens"/> 里，单独列出来是为了让用户看得见缓存有没有起作用
        /// —— 缓存命中部分只按 0.1 倍计价，这是长篇小说场景下最可观的一项省钱。
        /// </summary>
        public int CachedInputTokens { get; set; }

        /// <summary>
        /// 思考内容（推理模型的 reasoning / thinking 块）。**不写入正文**，
        /// 只在生成过程中给用户一个"它在思考"的信号，避免界面看上去像卡死了。
        /// </summary>
        public string Reasoning { get; set; } = "";

        /// <summary>
        /// 这次调用**失败**了（网络异常 / 401 / 400 / 空响应 …），<see cref="Text"/> 里是错误说明而不是正文。
        ///
        /// ★ 两个 Service 把失败包成 AiResult 返回（而不是抛异常），所以"结果非 null"
        ///   并不等于成功。凡是要把 Text 写回章节正文或设定的地方，必须先判这个标记 ——
        ///   否则一次 401 就能把整章正文/整份大纲换成"API调用失败: ..."。
        /// </summary>
        public bool IsError { get; set; }

        /// <summary>被用户主动停止。</summary>
        public bool IsCanceled { get; set; }

        /// <summary>
        /// 输出撞到了 max_tokens 上限（finish_reason=length / stop_reason=max_tokens），
        /// 正文被**截断**。改写类任务（润色）此时覆盖原文等于丢内容。
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// 用户停止时**保住了已经生成的那部分正文**（<see cref="Text"/> 非空）。
        ///
        /// 停止不等于丢弃：已经流式收到的内容在用户眼里就是"写出来的东西"，
        /// 直接丢掉等于让他白等一场。调用方看到这个标记应当把 Text 留给用户
        /// （写进正文或至少留在回复框），而不是当失败处理。
        /// </summary>
        public bool CanceledWithPartial => IsCanceled && !string.IsNullOrWhiteSpace(Text);

        /// <summary>正文可直接使用（非失败、非取消）。截断与否由调用方按任务性质自己决定。</summary>
        public bool IsUsable => !IsError && !IsCanceled;
    }

    public class CompletionOptions
    {
        public int MaxTokens { get; set; } = 8192;
        public double Temperature { get; set; } = 0.7;
        public string Model { get; set; } = "";
        public CancellationToken CancellationToken { get; set; } = default;
        public Action<int, int>? OnProgress { get; set; }

        /// <summary>
        /// 流空闲超时：多久没收到**任何字节**就认为连接卡死。
        /// 为 <see cref="TimeSpan.Zero"/> 时用 <c>StreamingApiServiceBase.DefaultIdleTimeout</c>。
        ///
        /// ★ 为什么需要它：HttpClient 那个 Timeout 是**整个请求**的上限，对长文生成来说
        ///   设短了会误杀（写 3000 字超过一分钟很正常），设长了又会让真卡死的连接干等。
        ///   正确的做法是关掉/放宽总超时，改用"多久没数据"来判死。
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// 允许服务端返回空正文。默认**不允许** —— 空正文一律按失败处理。
        ///
        /// ★ 这是数据安全阀：服务端可能返回 200 + finish_reason=stop 却没有任何内容
        ///   （内容审查拦截、网关异常、流被静默切断）。此时若当成成功，
        ///   调用方拿空字符串写回就是**把整章正文清空**。
        /// </summary>
        public bool AllowEmptyResponse { get; set; }

        /// <summary>给用户看的阶段性提示（重试中、思考中等）。</summary>
        public Action<string>? OnNotice { get; set; }

        /// <summary>流式生成过程中每次收到正文增量后的累积文本回调（供实时预览）。</summary>
        public Action<string>? OnStreamText { get; set; }

        /// <summary>
        /// 改写类任务（原文进、同量文本出）的输出预算。
        ///
        /// 不能写死小数值：中文约 1 字 ≈ 1 token，一份 3000 字的章节要 3000 左右输出，
        /// 再加上思考模型（GLM / DeepSeek-R1 / Kimi 等）动辄 2000~4000 token 的思考链，
        /// 这里按原文长度留 3 倍余量，并夹在 [floor, 16384]。
        /// </summary>
        public static int BudgetForRewrite(string? source, int floor = 8192)
        {
            long estimate = (long)Math.Ceiling((source?.Length ?? 0) * 3.0);
            return (int)Math.Clamp(estimate, floor, 16384);
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