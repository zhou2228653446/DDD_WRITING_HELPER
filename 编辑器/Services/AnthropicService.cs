using System;
using System.Collections.Generic;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>
    /// Anthropic Messages 协议（/v1/messages）。
    ///
    /// 与 OpenAI 兼容协议的三处结构性差异，都在这里处理：
    ///   ① system 是**顶层字段**而不是 messages 里的一条（塞进 messages 会被 400）
    ///   ② system 可以是 **block 数组** —— 这是唯一能显式打 prompt-caching 断点的地方
    ///   ③ SSE 事件自带 type 字段，按 type 分派（而不是像 OpenAI 那样按字段存在性猜）
    /// </summary>
    public class AnthropicService : StreamingApiServiceBase
    {
        public AnthropicService(ApiConfig config) : base(config) { }

        protected override string BuildRequestBody(
            string prompt, SystemPrompt? systemPrompt, CompletionOptions options,
            IReadOnlyList<ChatMessage>? history, string? assistantSoFar = null)
        {
            var model = !string.IsNullOrEmpty(options.Model) ? options.Model : Config.Model;

            // Anthropic 要求 messages 以 user 开头、且同角色不能连续 ——
            // 历史由 ChatSessionStore 按「一问一答」成对维护，天然满足。
            var messages = new List<object>(4 + (history?.Count ?? 0));
            ChatMessage.AppendTo(messages, history);
            messages.Add(new { role = "user", content = prompt });

            // 截断后的续写：把已经拿到的内容作为 assistant 消息放回去，
            // 再明确要求"接着写"。⚠ assistant 内容不能为空（会被 400 拒绝），
            // 好在只有正文非空时才会走到这里。
            if (!string.IsNullOrWhiteSpace(assistantSoFar))
            {
                messages.Add(new { role = "assistant", content = assistantSoFar });
                messages.Add(new { role = "user", content = ContinuationPrompt });
            }

            var request = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["stream"] = true,
                ["max_tokens"] = options.MaxTokens,
                ["temperature"] = options.Temperature,
                ["messages"] = messages
            };

            if (systemPrompt != null && !systemPrompt.IsEmpty)
                request["system"] = BuildSystemBlocks(systemPrompt);

            return JsonSerializer.Serialize(request);
        }

        /// <summary>
        /// 把分段的 system 变成 Anthropic 的 block 数组，并给该缓存的段打上断点。
        ///
        /// ★ 这里是 prompt caching 真正生效的地方。Anthropic 只缓存**显式标注**
        ///   <c>cache_control: {"type":"ephemeral"}</c> 的 block 及其之前的全部内容 ——
        ///   光把稳定内容排在前面（本项目改造前的做法）**不会**触发缓存。
        ///   打标之后，稳定段（身份、项目设定）跨请求复用，命中部分按 0.1 倍计价。
        ///
        /// ⚠ 两个已知约束：一个请求最多 4 个断点；不足最小长度（Sonnet 约 1024 token、
        ///   Haiku 2048）的段服务端会静默跳过。所以打多了不会报错，只是白打。
        /// </summary>
        private static List<object> BuildSystemBlocks(SystemPrompt systemPrompt)
        {
            var blocks = new List<object>(systemPrompt.Parts.Count);

            foreach (var part in systemPrompt.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Text)) continue;

                if (part.Cacheable)
                {
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = part.Text.Trim(),
                        ["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" }
                    });
                }
                else
                {
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = part.Text.Trim()
                    });
                }
            }

            return blocks;
        }

        protected override void OnStreamEvent(JsonElement evt, StreamState state, CompletionOptions options)
        {
            if (!evt.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return;

            switch (typeEl.GetString())
            {
                case "message_start":
                    if (evt.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("usage", out var startUsage))
                    {
                        if (TryInt(startUsage, "input_tokens", out var input))
                            state.InputTokens = input;

                        // 命中缓存的那部分输入（只按 0.1 倍计价）。不读它，
                        // 用户就无从判断 prompt caching 到底有没有起作用。
                        if (TryInt(startUsage, "cache_read_input_tokens", out var cacheRead))
                            state.CachedInputTokens = cacheRead;

                        options.OnProgress?.Invoke(state.InputTokens, state.OutputTokens);
                    }
                    break;

                case "content_block_delta":
                    ReadContentDelta(evt, state, options);
                    break;

                case "message_delta":
                    if (evt.TryGetProperty("delta", out var msgDelta) &&
                        msgDelta.TryGetProperty("stop_reason", out var sr) &&
                        sr.ValueKind == JsonValueKind.String)
                    {
                        state.FinishReason = sr.GetString();
                        // "max_tokens" = 撞到上限被截断，上层据此决定要不要覆盖原文。
                        state.Truncated = string.Equals(state.FinishReason, "max_tokens",
                                                        StringComparison.OrdinalIgnoreCase);
                    }

                    if (evt.TryGetProperty("usage", out var deltaUsage) &&
                        TryInt(deltaUsage, "output_tokens", out var output))
                    {
                        state.OutputTokens = output;
                        state.UsageSeen = true;
                        options.OnProgress?.Invoke(state.InputTokens, state.OutputTokens);
                    }
                    break;

                case "message_stop":
                    state.Finished = true;
                    break;

                case "error":
                    // 流中途的服务端错误（overloaded_error / api_error …）：
                    // 当成"服务端中断"报出来，而不是静默返回半截内容。
                    if (evt.TryGetProperty("error", out var err))
                    {
                        state.ServerError = err.ValueKind == JsonValueKind.String
                            ? err.GetString()
                            : (TryString(err, "message", out var m) ? m : err.ToString());
                    }
                    state.Finished = true;
                    break;
            }
        }

        private static void ReadContentDelta(JsonElement evt, StreamState state, CompletionOptions options)
        {
            if (!evt.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return;

            // 思考块（extended thinking）：与正文分开累计，绝不写进正文。
            // 不解析它的话，开着 thinking 的模型会先静默几秒到几十秒，界面像卡死。
            if (delta.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String &&
                string.Equals(dt.GetString(), "thinking", StringComparison.OrdinalIgnoreCase))
            {
                if (TryString(delta, "thinking", out var thinking))
                {
                    state.Reasoning.Append(thinking);
                    options.OnNotice?.Invoke("AI 正在思考…");
                }
                return;
            }

            if (TryString(delta, "text", out var token))
            {
                state.Text.Append(token);

                // 估算输出 token（中文约 1.5 字 / token），把进度推起来
                int estimated = (int)(state.Text.Length / 1.5);
                if (estimated > state.OutputTokens)
                    options.OnProgress?.Invoke(state.InputTokens, estimated);
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
