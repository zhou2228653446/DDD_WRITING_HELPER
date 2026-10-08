using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    /// <summary>服务商模型列表里的一项。</summary>
    public sealed class ModelInfo
    {
        /// <summary>请求时应该填进 <c>model</c> 字段的名字（已去掉 <c>models/</c> 前缀）。</summary>
        public string Id { get; init; } = "";

        /// <summary>人类可读名（OpenRouter 的 <c>name</c>、Anthropic 的 <c>display_name</c>），可能为空。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>是否像对话模型（用来把 embedding / tts / 图片模型排到后面）。</summary>
        public bool IsChatLike { get; init; } = true;

        public override string ToString() => Id;
    }

    /// <summary>拉取模型列表的结果。失败时 <see cref="Error"/> 是**给用户看的**原因，不是异常信息。</summary>
    public sealed class ModelCatalogResult
    {
        public bool Ok { get; init; }
        public IReadOnlyList<ModelInfo> Models { get; init; } = Array.Empty<ModelInfo>();

        /// <summary>失败原因（已翻译成人话 + 下一步该怎么做）。成功时为空串。</summary>
        public string Error { get; init; } = "";

        /// <summary>实际请求的地址（排查用，出错时一并显示）。</summary>
        public string Url { get; init; } = "";

        public static ModelCatalogResult Success(string url, IReadOnlyList<ModelInfo> models) =>
            new() { Ok = true, Url = url, Models = models };

        public static ModelCatalogResult Fail(string url, string error) =>
            new() { Ok = false, Url = url, Error = error };
    }

    /// <summary>
    /// 从服务商拉取**当前真实可用**的模型列表。
    ///
    /// 为什么必须有这个：内置的常用型号表只是「写代码那一刻的参考」，
    /// 服务商改型号名（尤其是带日期后缀的版本号）比程序发版快得多 ——
    /// 硬编码的名称迟早会变成用不了的死字符串。真实清单只能现场问服务商要。
    ///
    /// 绝大多数服务商都是 OpenAI 的 <c>GET /v1/models</c> 形状，地址能从对话地址推出来；
    /// 推不出来（或形状古怪）的用 <see cref="ProviderPreset.ModelsEndpoint"/> 显式声明。
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>对话端点的路径后缀，按长度从长到短排列（短的会误匹配长的尾部）。</summary>
        private static readonly string[] ChatSuffixes =
        {
            "/chat/completions",
            "/text/chatcompletion_v2",
            "/completions",
            "/messages",
            "/responses",
        };

        /// <summary>一眼就不是对话模型的标记，用来把它们排到列表末尾。</summary>
        private static readonly string[] NonChatMarkers =
        {
            "embed", "rerank", "whisper", "tts", "speech", "transcribe",
            "dall-e", "dalle", "moderation", "stable-diffusion", "sdxl",
            "flux", "bge-", "gte-", "ocr", "audio", "realtime", "sora",
            "seedream", "seedance",
        };

        /// <summary>把容器键都认一遍，不同服务商的壳不一样。</summary>
        private static readonly string[] ContainerKeys =
        {
            "data", "models", "model_list", "modelList", "items", "list", "result", "results",
        };

        // ---------------------------------------------------------------
        // 地址推断
        // ---------------------------------------------------------------

        /// <summary>
        /// 从对话端点推出模型列表地址。
        /// <c>.../v1/chat/completions</c> → <c>.../v1/models</c>；
        /// <c>.../v1/messages</c> → <c>.../v1/models</c>；
        /// <c>.../api/paas/v4/chat/completions</c> → <c>.../api/paas/v4/models</c>（智谱那类非 /v1 路径同样成立）。
        ///
        /// **推不出来就返回 null，不做瞎猜** —— 猜错会让用户拿着一个看着像 404 的地址去查文档，
        /// 不如直接说"请把 API 地址填成完整的对话端点"，再由调用方回落到服务商显式声明的地址。
        /// </summary>
        public static string? DeriveModelsUrl(string? chatUrl)
        {
            if (string.IsNullOrWhiteSpace(chatUrl)) return null;

            var raw = chatUrl.Trim();

            var query = "";
            var q = raw.IndexOf('?');
            if (q >= 0)
            {
                query = raw[q..];
                raw = raw[..q];
            }
            raw = raw.TrimEnd('/');

            var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
            var searchFrom = schemeEnd < 0 ? 0 : schemeEnd + 3;
            var slash = raw.IndexOf('/', searchFrom);

            var origin = slash < 0 ? raw : raw[..slash];
            var path = slash < 0 ? "" : raw[slash..];

            if (origin.Length == 0) return null;

            // 用户可能只写了 api.xxx.com/v1/... 而漏了协议
            if (!origin.Contains("://", StringComparison.Ordinal))
                origin = (IsLocalHost(origin) ? "http://" : "https://") + origin;

            foreach (var suffix in ChatSuffixes)
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return origin + path[..^suffix.Length] + "/models" + query;

            // 用户填的是火山方舟 Anthropic 兼容 Base URL（.../api/plan 或 .../api/coding）—— 补上 /v1/models
            if (path.EndsWith("/api/plan", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/api/coding", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return origin + path + "/v1/models" + query;
            }

            // 用户填的是 base 地址（.../v1、.../v1beta、.../api/plan/v3）—— 补上 /models
            if (IsVersionSegment(path))
                return origin + path + "/models" + query;

            return null;
        }

        /// <summary>最后一段像不像版本号（v1 / v2 / v3 / v1beta / v4alpha）。</summary>
        private static bool IsVersionSegment(string path)
        {
            if (path.Length < 2 || path[0] != '/') return false;

            var last = path[(path.LastIndexOf('/') + 1)..];
            if (last.Length < 2 || (last[0] != 'v' && last[0] != 'V')) return false;

            var i = 1;
            while (i < last.Length && char.IsDigit(last[i])) i++;
            if (i == 1) return false;                      // 后面必须紧跟数字
            while (i < last.Length && char.IsLetter(last[i])) i++;    // 允许 v1beta 这类后缀

            return i == last.Length;
        }

        /// <summary>
        /// 取这次配置应该请求的模型列表地址。
        ///
        /// 规则：**以对话地址推导为准**（用户可能把地址换成了中转），
        /// 推导不出来才用服务商预设显式声明的；
        /// 推导成功且仍在官方域名上时，把预设里的必要查询参数带上
        /// （Anthropic 不加 <c>?limit=1000</c> 只会返回前 20 个）。
        /// </summary>
        public static string? ResolveModelsUrl(ApiConfig? config)
        {
            if (config == null) return null;

            var preset = ApiProviders.Find(config.Provider);
            var explicitUrl = preset?.ModelsEndpoint;
            var derived = DeriveModelsUrl(config.ApiUrl);

            if (string.IsNullOrWhiteSpace(explicitUrl)) return derived;
            if (derived == null) return explicitUrl;

            if (preset != null && SameHost(config.ApiUrl, preset.Endpoint))
                return derived + QueryOf(explicitUrl);

            return derived;
        }

        private static string QueryOf(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var i = url.IndexOf('?');
            return i < 0 ? "" : url[i..];
        }

        private static bool SameHost(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(new Uri(a).Host, new Uri(b).Host, StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        public static bool IsLocalHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var s = url.ToLowerInvariant();
            return s.Contains("localhost") || s.Contains("127.0.0.1") || s.Contains("[::1]");
        }

        // ---------------------------------------------------------------
        // 请求
        // ---------------------------------------------------------------

        /// <summary>拉取模型列表。**不会抛异常**，失败也走返回值，错误信息已经是给用户看的。</summary>
        public static async Task<ModelCatalogResult> FetchAsync(ApiConfig config, CancellationToken ct = default)
        {
            var url = ResolveModelsUrl(config);
            if (string.IsNullOrWhiteSpace(url))
                return ModelCatalogResult.Fail("",
                    "无法从当前 API 地址推断出模型列表接口。\n"
                    + "请把「API 地址」填成完整的对话端点（…/v1/chat/completions 或 …/v1/messages）。");

            if (string.IsNullOrWhiteSpace(config.ApiKey) && !IsLocalHost(url))
                return ModelCatalogResult.Fail(url, "请先填写 API Key —— 拉取模型列表同样需要鉴权。");

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // 与真实调用共用同一份请求头逻辑，避免「测试能过、拉列表 401」
                ApiProviders.ApplyHeaders(request.Headers, config);
                ApiProviders.ApplyExtraHeaders(request.Headers, config);

                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    return ModelCatalogResult.Fail(url, DescribeHttpError(response.StatusCode, body));

                if (LooksLikeHtml(body))
                    return ModelCatalogResult.Fail(url,
                        "返回的不是 JSON（看起来是网页）。多半是地址被网关或门户挡住了，"
                        + "或 API 地址填成了控制台地址。\n实际请求：" + url);

                IReadOnlyList<ModelInfo> models;
                try
                {
                    models = Parse(body);
                }
                catch (JsonException)
                {
                    return ModelCatalogResult.Fail(url,
                        "返回内容无法解析成 JSON：" + Snippet(body) + "\n实际请求：" + url);
                }

                if (models.Count == 0)
                    return ModelCatalogResult.Fail(url,
                        "接口调用成功，但里面没有任何模型。该 Key 可能还没开通模型权限。");

                return ModelCatalogResult.Success(url, models);
            }
            catch (TaskCanceledException)
            {
                return ModelCatalogResult.Fail(url, "请求超时（20 秒）。检查网络或代理是否正常。");
            }
            catch (HttpRequestException ex)
            {
                return ModelCatalogResult.Fail(url, "无法连接：" + ex.Message + "\n实际请求：" + url);
            }
            catch (Exception ex)
            {
                return ModelCatalogResult.Fail(url, "拉取失败：" + ex.Message);
            }
        }

        /// <summary>把 HTTP 状态码翻译成「发生了什么 + 该怎么办」。带上响应体片段便于对证。</summary>
        public static string DescribeHttpError(HttpStatusCode status, string? body)
        {
            var code = (int)status;
            switch (code)
            {
                case 400:
                    return "服务商认为请求不合法（400）：" + Snippet(body);
                case 401:
                    return "API Key 无效或已失效（401）。请到控制台确认 Key 复制完整、没有多余空格。";
                case 403:
                    return "该 Key 没有访问模型列表的权限（403）。可以直接在下方手填模型名。";
                case 404:
                case 405:
                    return $"该服务商没有标准的模型列表接口（{code}）。请按官方文档手填模型名。"
                           + "\n实际请求：" + Snippet(body);
                case 429:
                    return "请求过于频繁（429），稍后再试。";
            }

            if (code >= 500)
                return $"服务商暂时不可用（{code}），稍后再试。";

            var snippet = Snippet(body);
            return snippet.Length > 0
                ? $"服务商返回错误（{code}）：{snippet}"
                : $"服务商返回错误（{code}）。";
        }

        private static bool LooksLikeHtml(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            var head = body.TrimStart();
            return head.StartsWith("<", StringComparison.Ordinal)
                   && (head.Contains("<html", StringComparison.OrdinalIgnoreCase)
                       || head.Contains("<!doctype", StringComparison.OrdinalIgnoreCase));
        }

        private static string Snippet(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            var s = body.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 200 ? s : s[..200] + "…";
        }

        // ---------------------------------------------------------------
        // 解析
        // ---------------------------------------------------------------

        /// <summary>
        /// 解析模型列表响应。兼容三种壳：
        /// OpenAI / Anthropic / OpenRouter 的 <c>{"data":[{"id":…}]}</c>、
        /// Google 与 Ollama 的 <c>{"models":[{"name":…}]}</c>、以及直接给数组的形式。
        /// </summary>
        public static IReadOnlyList<ModelInfo> Parse(string json)
        {
            var found = new List<ModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(json);
            Collect(doc.RootElement, found, seen, 0);

            return found;
        }

        private static void Collect(JsonElement element, List<ModelInfo> found, HashSet<string> seen, int depth)
        {
            if (depth > 4) return;

            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Collect(item, found, seen, depth + 1);
                    return;

                case JsonValueKind.String:
                    Add(element.GetString(), null, found, seen);
                    return;

                case JsonValueKind.Object:
                    // 先判容器：壳里带 data / models / items 这些数组键
                    foreach (var key in ContainerKeys)
                    {
                        if (!element.TryGetProperty(key, out var child)) continue;
                        if (child.ValueKind != JsonValueKind.Array && child.ValueKind != JsonValueKind.Object) continue;

                        Collect(child, found, seen, depth + 1);
                        return;
                    }

                    // 不是容器就当成一项
                    Add(
                        FirstString(element, "id", "model", "model_name", "modelName", "model_id", "name"),
                        FirstString(element, "display_name", "displayName", "title", "name"),
                        found, seen);
                    return;
            }
        }

        private static void Add(string? rawId, string? display, List<ModelInfo> found, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return;

            var id = Normalize(rawId);
            if (id.Length == 0) return;
            if (!seen.Add(id)) return;

            var name = string.IsNullOrWhiteSpace(display) ? "" : display.Trim();
            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) name = "";

            found.Add(new ModelInfo
            {
                Id = id,
                DisplayName = name,
                IsChatLike = IsChatLike(id),
            });
        }

        /// <summary>Google 原生接口给的是 <c>models/gemini-2.5-flash</c>，请求时要写成后半截。</summary>
        private static string Normalize(string id)
        {
            var s = id.Trim();
            if (s.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                s = s["models/".Length..];
            return s;
        }

        private static string? FirstString(JsonElement obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!obj.TryGetProperty(key, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        /// <summary>是不是像对话模型（不是就排到列表末尾，而不是丢掉）。</summary>
        public static bool IsChatLike(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var s = id.ToLowerInvariant();
            foreach (var marker in NonChatMarkers)
                if (s.Contains(marker, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>对话模型在前、其余在后，各组内按名字排序。</summary>
        public static IReadOnlyList<ModelInfo> SortForDisplay(IEnumerable<ModelInfo> models) =>
            models.OrderByDescending(m => m.IsChatLike)
                  .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                  .ToList();

        public static int NonChatCount(IEnumerable<ModelInfo> models) =>
            models.Count(m => !m.IsChatLike);
    }
}
