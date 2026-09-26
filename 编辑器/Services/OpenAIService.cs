using System;
using System.Collections.Generic;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>
    /// OpenAI 兼容协议（/v1/chat/completions）。
    ///
    /// 只实现"这个协议长什么样"：请求体怎么拼、SSE 事件什么语义。
    /// 发送、超时、重试、取消保留等全部由 <see cref="StreamingApiServiceBase"/> 负责。
    /// </summary>
    public class OpenAIService : StreamingApiServiceBase
    {
        /// <summary>
        /// 收到 finish_reason 之后，最多再读多少行继续捞 usage 收尾包。
        /// 官方在 finish_reason 之后会补一个只带 usage 的包；某些网关什么都不补、
        /// 直接关流。给个小上限，两种情况都不会卡住。
        /// </summary>
        protected override int GraceLinesAfterFinish => 20;

        public OpenAIService(ApiConfig config) : base(config) { }

        // 兼容旧版：仅传 API Key 时使用默认配置
        public OpenAIService(string apiKey) : base(new ApiConfig { ApiKey = apiKey }) { }

        protected override string BuildRequestBody(
            string prompt, SystemPrompt? systemPrompt, CompletionOptions options,
            IReadOnlyList<ChatMessage>? history, string? assistantSoFar = null)
        {
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : Config.Model;

            // 消息顺序固定为 system → 历史多轮 → 本轮 user。system 段是跨请求最稳定的前缀，
            // 放在最前面才能命中服务端的 prompt caching（长上下文项目省 token 也省延迟）。
            //
            // ⚠ 本协议**不支持** cache_control（它的缓存是自动的、纯按前缀匹配），
            //   所以这里把分段的 system 压平回一条消息。顺序不变即可命中前缀缓存；
            //   真正需要显式打标记的是 Anthropic，见 AnthropicService。
            var messages = new List<object>(4 + (history?.Count ?? 0));
            if (systemPrompt != null && !systemPrompt.IsEmpty)
                messages.Add(new { role = "system", content = systemPrompt.Flatten() });

            ChatMessage.AppendTo(messages, history);
            messages.Add(new { role = "user", content = prompt });

            // 截断后的续写：把已经拿到的内容作为 assistant 消息放回去，
            // 再明确要求"接着写"。不这样做的话模型会从头重来一遍。
            if (!string.IsNullOrWhiteSpace(assistantSoFar))
            {
                messages.Add(new { role = "assistant", content = assistantSoFar });
                messages.Add(new { role = "user", content = ContinuationPrompt });
            }

            var request = new
            {
                model,
                stream = true,
                stream_options = new { include_usage = true },
                messages,
                max_tokens = options.MaxTokens,
                temperature = options.Temperature
            };

            return JsonSerializer.Serialize(request);
        }

        protected override void OnStreamEvent(JsonElement evt, StreamState state, CompletionOptions options)
        {
            ReadUsage(evt, state, options);
            ReadDelta(evt, state, options);
            ReadFinish(evt, state);
            ReadError(evt, state);
        }

        private static void ReadUsage(JsonElement evt, StreamState state, CompletionOptions options)
        {
            if (!evt.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return;

            if (TryInt(usage, "prompt_tokens", out var prompt))
                state.InputTokens = prompt;
            if (TryInt(usage, "completion_tokens", out var completion))
                state.OutputTokens = completion;

            // 命中缓存的那部分输入。OpenAI 把它藏在 prompt_tokens_details 里，
            // 且**已经计入 prompt_tokens** —— 不单独读出来，用户就看不出缓存有没有起作用。
            if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object &&
                TryInt(details, "cached_tokens", out var cached))
            {
                state.CachedInputTokens = cached;
            }

            state.UsageSeen = true;
            options.OnProgress?.Invoke(state.InputTokens, state.OutputTokens);
        }

        private static void ReadDelta(JsonElement evt, StreamState state, CompletionOptions options)
        {
            if (!evt.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return;

            if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return;

            // 思考内容：DeepSeek 用 reasoning_content，OpenRouter / Qwen 用 reasoning。
            // 不解析它，界面在推理模型上会长时间一动不动，用户以为卡死了。
            var reasoning = "";
            if (!TryString(delta, "reasoning_content", out reasoning))
                TryString(delta, "reasoning", out reasoning);

            if (reasoning.Length > 0)
            {
                state.Reasoning.Append(reasoning);
                options.OnNotice?.Invoke("AI 正在思考…");
            }

            if (TryString(delta, "content", out var token) && token.Length > 0)
            {
                state.Text.Append(token);

                // 流里还没报用量时先用估算值把进度推起来（中文约 1.5 字 / token）
                int estimated = (int)(state.Text.Length / 1.5);
                if (estimated > state.OutputTokens)
                    options.OnProgress?.Invoke(state.InputTokens, estimated);
            }
        }

        private static void ReadFinish(JsonElement evt, StreamState state)
        {
            if (!evt.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return;

            if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                state.FinishReason = fr.GetString();
                // "length" = 撞到 max_tokens 被截断。上层据此决定要不要覆盖原文。
                state.Truncated = string.Equals(state.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
                state.Finished = true;
            }
        }

        private static void ReadError(JsonElement evt, StreamState state)
        {
            // 部分网关会在流中途吐一个 {"error":{...}} 而不是断开
            if (evt.TryGetProperty("error", out var err))
            {
                state.ServerError = err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : (TryString(err, "message", out var m) ? m : err.ToString());
            }
        }

        private static bool TryInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                value = el.GetInt32();
                return true;
            }
            return false;
        }

        private static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                value = el.GetString() ?? "";
                return value.Length > 0;
            }
            return false;
        }
    }
}
