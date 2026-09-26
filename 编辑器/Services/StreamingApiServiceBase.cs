using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    /// <summary>
    /// 流式请求的公共骨架：发送 → 读流 → 收尾。
    ///
    /// 两个协议的真正差异只有两处 —— **请求体怎么拼**、**SSE 事件什么语义** ——
    /// 其余全部（超时、重试、取消保留、空响应拦截、用量累计）都在这里，
    /// 一处维护，避免"同一个问题在 OpenAI 分支修了、Anthropic 分支还留着"。
    ///
    /// ★ 这些兜底不是可选项，逐条对应一类真实故障：
    ///   · 空闲超时    —— HttpClient 的总超时对长文生成不是长就是短，改用"多久没数据"判死
    ///   · 瞬时重试    —— 网络抖一下、429 限流一次，不该让用户整次白等
    ///   · 取消保留    —— 用户点"停止"是嫌写得不好，不是要把已生成的几千字扔掉
    ///   · 空响应拦截  —— 服务端返 200 + stop 却空着内容时，当成功就会**清空整章**
    /// </summary>
    public abstract class StreamingApiServiceBase : IApiService
    {
        /// <summary>流空闲超时默认值：这么久没收到任何字节就认为连接卡死。</summary>
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(90);

        /// <summary>瞬时错误最多重试几次（不含首次）。</summary>
        private const int MaxRetries = 4;

        /// <summary>
        /// 退避序列。首次重试只等不到一秒 —— 多数抖动是瞬时的，
        /// 让用户为"早该成功"的请求多等十几秒，比直接失败更糟。
        /// </summary>
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        };

        protected readonly HttpClient Http;
        protected readonly ApiConfig Config;

        protected StreamingApiServiceBase(ApiConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new ArgumentException("API Key 不能为空", nameof(config));

            Http = new HttpClient
            {
                // 总超时只做最后的兜底。真正判"卡死"的是下面的流空闲超时 ——
                // HttpClient 的 Timeout 覆盖整个请求-响应周期（含读流），
                // 对长文生成设 100 秒（默认值）会误杀，设无限又可能永远挂着。
                Timeout = TimeSpan.FromMinutes(10)
            };

            // 认证头交给 ApiProviders 统一装配：Bearer / x-api-key / api-key 三种风格，
            // 加上服务商额外要求的头（OpenRouter 的 HTTP-Referer 等）。
            // 两个 Service + 测试连接三方共用同一处，否则会出现
            //「测试连接成功、真调用却 401」的假阳性。
            ApiProviders.ApplyHeaders(Http.DefaultRequestHeaders, config);
            ApiProviders.ApplyExtraHeaders(Http.DefaultRequestHeaders, config);
        }

        /// <summary>
        /// 撞到输出上限时，自动接着写的次数上限（不含首次）。
        ///
        /// ★ 为什么值得自动做：一次截断意味着"用户拿到的正文是半截的"，
        ///   而半截正文对小说来说基本不可用（尤其润色是整章覆盖）。手动再点一次
        ///   续写不仅麻烦，用户还未必知道该在提示词里交代"这是接着上次写的"。
        ///
        /// ⚠ 代价是 token：每次续写都要把已有内容重新作为输入发一遍，
        ///   所以不设大值。2 次意味着最坏情况 3 个请求。
        /// </summary>
        private const int MaxContinuations = 2;

        /// <summary>
        /// 续写时的提示词。要点是**别让它重来一遍** —— 模型天然倾向于
        /// "重新完整地写一次"，那样会把已经拿到的内容重复输出，反而更糟。
        /// </summary>
        protected const string ContinuationPrompt =
            "上一次回复因为到达输出长度上限被硬截断了，句子可能在中间断掉。"
            + "请**直接接着写下去**：不要道歉，不要总结前文，不要把已经写过的内容重写一遍，"
            + "从断掉的地方续上，保持同样的风格与语气。";

        /// <summary>
        /// 收到结束标记之后，最多再读多少行继续捞 usage 收尾包。
        /// OpenAI 在 finish_reason 之后还会补一个只带 usage 的包，Anthropic 则在
        /// 结束事件里就带齐了 —— 所以只有前者需要宽限（见子类覆写）。
        /// </summary>
        protected virtual int GraceLinesAfterFinish => 0;

        /// <summary>拼请求体。返回值是已序列化的 JSON（每次重试都要新发一份，不能复用流）。</summary>
        /// <param name="assistantSoFar">
        /// 非 null 表示这是**截断后的续写请求**：把它作为一条 assistant 消息放进
        /// messages，后面再跟一条"接着写"的 user 消息。
        /// </param>
        protected abstract string BuildRequestBody(
            string prompt, SystemPrompt? systemPrompt, CompletionOptions options,
            IReadOnlyList<ChatMessage>? history, string? assistantSoFar = null);

        /// <summary>处理一条 SSE 事件，把文本 / 用量 / 结束原因累加进 <paramref name="state"/>。</summary>
        protected abstract void OnStreamEvent(JsonElement evt, StreamState state, CompletionOptions options);

        public async Task<AiResult> CompleteTextAsync(
            string prompt,
            SystemPrompt? systemPrompt = null,
            CompletionOptions? options = null,
            IReadOnlyList<ChatMessage>? history = null)
        {
            options ??= new CompletionOptions();
            var idle = options.IdleTimeout > TimeSpan.Zero ? options.IdleTimeout : DefaultIdleTimeout;

            var accumulated = new StringBuilder();
            int totalInput = 0, totalOutput = 0, totalCached = 0;
            var reasoning = new StringBuilder();
            int continuations = 0;

            // 调用方想要的输出预算。每次请求都要拿它重新夹一次窗口 ——
            // 因为续写会让输入变长，留给输出的空间随之变小。
            int desiredMaxTokens = options.MaxTokens;

            while (true)
            {
                var assistantSoFar = accumulated.Length > 0 ? accumulated.ToString() : null;

                options.MaxTokens = ClampOutputBudget(
                    desiredMaxTokens, systemPrompt, prompt, history, assistantSoFar, options);

                var json = BuildRequestBody(prompt, systemPrompt, options, history, assistantSoFar);

                var result = await RunWithRetryAsync(json, idle, options).ConfigureAwait(false);

                totalInput += result.InputTokens;
                totalOutput += result.OutputTokens;
                totalCached += result.CachedInputTokens;
                if (result.Reasoning.Length > 0) reasoning.Append(result.Reasoning);

                if (result.IsError || result.IsCanceled)
                {
                    // 已经续过若干段的话，把前面的内容并回来 —— 用户看到的应当是
                    // 完整篇幅，而不是"最后那一段"。取消/失败只影响"还能不能继续"。
                    if (accumulated.Length > 0)
                    {
                        result.Text = accumulated + result.Text;
                        result.Truncated = false;
                    }

                    // 多轮累计的用量：续写时的输入包含前面所有内容，所以相加才对得上账单。
                    result.InputTokens = totalInput;
                    result.OutputTokens = totalOutput;
                    result.CachedInputTokens = totalCached;
                    result.TotalTokens = totalInput + totalOutput;
                    if (reasoning.Length > 0) result.Reasoning = reasoning.ToString();
                    return result;
                }

                accumulated.Append(result.Text);

                if (!result.Truncated) break;
                if (++continuations > MaxContinuations) break;

                options.OnNotice?.Invoke($"输出到达长度上限，正在接着写（{continuations}/{MaxContinuations}）…");
            }

            // 结果以**累计文本**为准：一次没截断时它等于首段，与改造前完全一致。
            return new AiResult
            {
                Text = accumulated.ToString(),
                Reasoning = reasoning.ToString(),
                InputTokens = totalInput,
                OutputTokens = totalOutput,
                CachedInputTokens = totalCached,
                TotalTokens = totalInput + totalOutput,
                Truncated = continuations > MaxContinuations,
            };
        }

        /// <summary>留给服务端消息包装（role / 分隔符 / 协议开销）的余量，算剩余窗口时先扣掉。</summary>
        private const int OutputBudgetReserve = 1000;

        /// <summary>
        /// 按"剩余窗口"收一次输出预算：<c>min(想要的, 窗口 − 估算输入 − 余量)</c>。
        ///
        /// ★ 为什么必须做：服务端拒的是「输入 + max_tokens 超窗」，**不是**「输入超窗」。
        ///   输入侧的压缩只解决了前半截 —— 如果输出侧仍按原样要一大截，
        ///   长上下文项目照样会撞上，而这一次是我们自己就能提前算出来的
        ///   （窗口表和估算器都在手上），没理由留给服务端报错。
        ///   对应 ZCode 的 <c>model-token-limits.ts</c>。
        ///
        /// ⚠ 估出来已经超了**不强行改**：本地估算永远不是权威判据，
        ///   按估算把预算压到 0 反而会把一个本来能成功的请求弄坏。这种情况保持原样，
        ///   让服务端给出权威错误，交给上层的压缩逻辑去处理。
        /// </summary>
        private int ClampOutputBudget(
            int desired, SystemPrompt? systemPrompt, string prompt,
            IReadOnlyList<ChatMessage>? history, string? assistantSoFar, CompletionOptions options)
        {
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : Config.Model;
            int window = ModelContextCatalog.Resolve(model);
            if (window <= 0) return desired;

            long used = TokenEstimator.Estimate(systemPrompt?.Flatten())
                      + TokenEstimator.Estimate(prompt)
                      + TokenEstimator.Estimate(assistantSoFar);

            if (history != null)
            {
                foreach (var m in history)
                    used += TokenEstimator.Estimate(m.Content);
            }

            long available = window - used - OutputBudgetReserve;
            if (available <= 0) return desired;

            // 至少给一点，否则请求会因为 max_tokens 太小而变成空响应。
            return (int)Math.Min(desired, Math.Max(available, 64));
        }

        /// <summary>
        /// 发一次（含瞬时错误重试与退避）。这是原本堆在 <c>CompleteTextAsync</c> 里的那层。
        /// </summary>
        private async Task<AiResult> RunWithRetryAsync(string json, TimeSpan idle, CompletionOptions options)
        {
            for (int attempt = 0; ; attempt++)
            {
                var state = new StreamState();
                var (result, retryable, serverHint) = await SendOnceAsync(json, idle, options, state)
                    .ConfigureAwait(false);

                if (!retryable || attempt >= MaxRetries)
                    return result;

                // 服务端给了 Retry-After 就听它的（429 限流时它比我们的退避更准）；
                // 否则按退避序列走。取两者里更长的那个，别在人家说"等 30 秒"时 800ms 就撞回去。
                var wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                if (serverHint.HasValue && serverHint.Value > wait) wait = serverHint.Value;

                // 退避期间用户按「停止」要能立刻中断，不能等满这一档。
                try
                {
                    await Task.Delay(wait, options.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return result;
                }

                options.OnNotice?.Invoke($"连接不稳定，正在重试（{attempt + 1}/{MaxRetries}）…");
            }
        }

        /// <summary>
        /// 发一次并读完流。后两个返回值表示"这次失败值得重试吗"与"服务端建议等多久"。
        /// </summary>
        private async Task<(AiResult Result, bool Retryable, TimeSpan? ServerHint)> SendOnceAsync(
            string json, TimeSpan idle, CompletionOptions options, StreamState state)
        {
            var ct = options.CancellationToken;

            // ★ 空闲超时用独立的 CTS，不直接用用户的令牌 ——
            //   否则"用户按了停止"和"服务器不发数据了"混成同一种异常，
            //   而这两件事的处置完全相反（前者保内容、后者该重试）。
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(idle);

            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Config.ApiUrl) { Content = content };

                using var response = await Http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, idleCts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);

                    // 不要在成功路径外使用 EnsureSuccessStatusCode()：它会把服务端返回的正文丢掉，
                    // 用户只能看到一个裸状态码（"400 (Bad Request)"），而真正有用的说明恰好就在
                    // body 里 —— "context length exceeded"、"model not found"、"insufficient balance"。
                    var text = $"API错误 ({(int)response.StatusCode}): {ApiErrors.Describe(response.StatusCode, body)}";

                    // 429 与 5xx 是"过一会儿可能就好了"，值得重试；
                    // 401/403/404/400 是配置或请求本身的问题，重试只是浪费时间。
                    bool retryable = IsTransientStatus(response.StatusCode);

                    return (new AiResult { Text = text, IsError = true },
                            retryable,
                            retryable ? RetryAfterDelay(response) : null);
                }

                using var stream = await response.Content.ReadAsStreamAsync(idleCts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                int graceLines = 0;

                while (true)
                {
                    var line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);

                    // 收到任何一行（哪怕只是心跳）就把空闲计时重置 —— 判据是"没数据"，不是"没内容"。
                    idleCts.CancelAfter(idle);

                    if (line == null) break;
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var data = line[5..].Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") break;

                    JsonElement evt;
                    try { evt = JsonSerializer.Deserialize<JsonElement>(data); }
                    catch { continue; }

                    OnStreamEvent(evt, state, options);

                    if (state.Finished)
                    {
                        // 已拿到用量就没什么可等的了；没拿到再宽限几行，
                        // 兜住"既不补 usage 也不发 [DONE]、只关连接"的网关。
                        if (state.UsageSeen || ++graceLines > GraceLinesAfterFinish) break;
                    }
                }

                FinalizeState(state);

                return (BuildResult(state, options), false, null);
            }
            catch (OperationCanceledException)
            {
                // ── 先分清是谁取消的 ─────────────────────────────────────
                if (ct.IsCancellationRequested)
                {
                    // 用户主动按了「停止」。★ 保住已经生成的部分 ——
                    // 用户停止通常是想换个说法或觉得后半段不对，前半段是有效的。
                    return (new AiResult
                    {
                        Text = state.Text.ToString(),
                        Reasoning = state.Reasoning.ToString(),
                        InputTokens = state.InputTokens,
                        OutputTokens = state.OutputTokens,
                        CachedInputTokens = state.CachedInputTokens,
                        TotalTokens = state.InputTokens + state.OutputTokens,
                        IsCanceled = true,
                    }, false, null);
                }

                // 空闲超时 / 总超时：服务器不发数据了。
                var partial = state.Text.ToString();
                if (partial.Length == 0)
                {
                    // 一个字都没吐出来 —— 换个连接重来是安全的（不会重复内容）。
                    return (new AiResult
                    {
                        Text = $"连接超时：服务器超过 {idle.TotalSeconds:0} 秒没有返回数据",
                        IsError = true,
                    }, true, null);
                }

                // 已经有内容了：**不重试**。重试会从头再生成一遍，
                // 用户拿到的要么是重复内容，要么得自己判断该留哪份 —— 比直接报错更糟。
                return (new AiResult
                {
                    Text = partial,
                    Reasoning = state.Reasoning.ToString(),
                    InputTokens = state.InputTokens,
                    OutputTokens = state.OutputTokens,
                    CachedInputTokens = state.CachedInputTokens,
                    TotalTokens = state.InputTokens + state.OutputTokens,
                    IsError = true,
                }, false, null);
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                var partial = state.Text.ToString();
                var retryable = partial.Length == 0;

                var result = partial.Length == 0
                    ? new AiResult { Text = $"网络异常：{ex.Message}", IsError = true }
                    : new AiResult
                    {
                        Text = partial,
                        Reasoning = state.Reasoning.ToString(),
                        InputTokens = state.InputTokens,
                        OutputTokens = state.OutputTokens,
                        CachedInputTokens = state.CachedInputTokens,
                        TotalTokens = state.InputTokens + state.OutputTokens,
                        IsError = true,
                    };

                return (result, retryable, null);
            }
            catch (Exception ex)
            {
                return (new AiResult { Text = $"API调用失败: {ex.Message}", IsError = true }, false, null);
            }
        }

        /// <summary>
        /// 收尾。子类可覆写补默认值（如流里没报用量时用估算值）。
        /// </summary>
        protected virtual void FinalizeState(StreamState state)
        {
            if (state.OutputTokens == 0 && state.Text.Length > 0)
                state.OutputTokens = (int)(state.Text.Length / 1.5);
        }

        private AiResult BuildResult(StreamState state, CompletionOptions options)
        {
            var text = state.Text.ToString();

            // 服务端在流中途报了错（Anthropic 的 error 事件、部分网关的 error 字段）。
            // 有半截内容就留着让用户看得见，但一律标成失败 —— 不能拿去覆盖原文。
            if (!string.IsNullOrWhiteSpace(state.ServerError))
            {
                return new AiResult
                {
                    Text = text.Length == 0 ? $"服务端中断：{state.ServerError}" : text,
                    Reasoning = state.Reasoning.ToString(),
                    InputTokens = state.InputTokens,
                    OutputTokens = state.OutputTokens,
                    CachedInputTokens = state.CachedInputTokens,
                    TotalTokens = state.InputTokens + state.OutputTokens,
                    IsError = true,
                };
            }

            // ★ 空响应 = 失败。服务端可能返回 200 + finish_reason=stop 却没有任何内容
            //   （内容审查拦截、网关异常、流被静默切断）。这时若当成功返回，
            //   调用方拿空字符串写回就是**把整章正文清空** —— 数据安全阀，别删。
            if (text.Length == 0 && !options.AllowEmptyResponse)
            {
                return new AiResult
                {
                    Text = DescribeEmptyResponse(state),
                    IsError = true,
                    InputTokens = state.InputTokens,
                    OutputTokens = state.OutputTokens,
                    CachedInputTokens = state.CachedInputTokens,
                    Reasoning = state.Reasoning.ToString(),
                };
            }

            return new AiResult
            {
                Text = text,
                Reasoning = state.Reasoning.ToString(),
                InputTokens = state.InputTokens,
                OutputTokens = state.OutputTokens,
                CachedInputTokens = state.CachedInputTokens,
                TotalTokens = state.InputTokens + state.OutputTokens,
                Truncated = state.Truncated,
            };
        }

        private static string DescribeEmptyResponse(StreamState state)
        {
            var reason = state.FinishReason?.ToLowerInvariant();
            if (reason is "content_filter" or "refusal" or "content_filtered")
                return "服务端没有返回内容（请求被内容安全策略拦截）";

            return "服务端返回了空内容：本次没有生成任何正文，原文未做改动。"
                 + "可以重试一次；若反复出现，换一个模型或检查该条请求是否触发了服务端的过滤。";
        }

        /// <summary>429 / 5xx / 408 属于"等会儿再试可能就好了"。</summary>
        private static bool IsTransientStatus(HttpStatusCode status) =>
            status == HttpStatusCode.RequestTimeout
            || (int)status == 429
            || (int)status >= 500;

        /// <summary>网络层异常 —— 连接被重置、DNS 失败、TLS 失败等。</summary>
        private static bool IsTransientException(Exception ex) =>
            ex is HttpRequestException or IOException or System.Net.Sockets.SocketException;

        /// <summary>取 Retry-After 头（429 时服务端会告诉你等多久）。没有就返回 null。</summary>
        private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null) return null;

            if (retryAfter.Delta.HasValue) return retryAfter.Delta.Value;
            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero) return delta;
            }
            return null;
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch { return ""; }
        }

        /// <summary>一条流解析过程中的累计状态。</summary>
        protected sealed class StreamState
        {
            public readonly StringBuilder Text = new();

            /// <summary>思考内容（推理模型的 reasoning / thinking）。**不写入正文**。</summary>
            public readonly StringBuilder Reasoning = new();

            public int InputTokens;
            public int OutputTokens;

            /// <summary>输入里命中缓存的那部分（Anthropic cache_read / OpenAI cached_tokens）。</summary>
            public int CachedInputTokens;

            public bool UsageSeen;
            public bool Finished;
            public bool Truncated;

            /// <summary>服务端给的结束原因原文（stop / length / content_filter / max_tokens …）。</summary>
            public string? FinishReason;

            /// <summary>服务端在流中途报的错误说明（有值即视为本次失败）。</summary>
            public string? ServerError;

            /// <summary>正文或思考内容是否已经有东西了（决定断线后能不能安全重试）。</summary>
            public bool HasOutput => Text.Length > 0 || Reasoning.Length > 0;
        }
    }
}
