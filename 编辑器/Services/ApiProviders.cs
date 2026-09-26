using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;

namespace 编辑器.Services
{
    /// <summary>
    /// 接口协议。决定「请求体的形状」与「system 消息放在哪里」。
    /// </summary>
    public enum ApiWire
    {
        /// <summary>OpenAI Chat Completions：system 是 messages[0]。</summary>
        OpenAiChat,

        /// <summary>Anthropic Messages：system 是顶层字段（塞进 messages 会被 400 拒绝）。</summary>
        AnthropicMessages,
    }

    /// <summary>
    /// 认证方式。决定 API Key 放进哪个请求头 ——
    /// 这是接新服务商时最容易踩空的一步：填错头不会报「认证方式不对」，
    /// 只会返回 401/403，用户很难看出是头的问题。
    /// </summary>
    public enum ApiAuth
    {
        /// <summary><c>Authorization: Bearer sk-xxx</c>（绝大多数服务商）。</summary>
        Bearer,

        /// <summary><c>x-api-key: sk-xxx</c>，Anthropic 官方要求同时带 <c>anthropic-version</c>。</summary>
        XApiKey,

        /// <summary><c>api-key: xxx</c>，不附带 anthropic-version（小米 Mimo Token Plan 用这种）。</summary>
        ApiKeyHeader,
    }

    /// <summary>一个服务商的完整接入参数。填好这些，用户就只需要给 Key 和模型名。</summary>
    public sealed class ProviderPreset
    {
        /// <summary>稳定标识，写进 <see cref="ApiConfig.Provider"/>。</summary>
        public string Id { get; init; } = "";

        /// <summary>显示名，也是旧配置文件（v1 只存显示名）的兼容键。</summary>
        public string Name { get; init; } = "";

        /// <summary>分组，用于下拉排序与提示语（国内主流 / 国际主流 / 聚合中转 / 本地部署 / 自定义）。</summary>
        public string Group { get; init; } = "";

        public ApiWire Wire { get; init; }

        public ApiAuth Auth { get; init; }

        /// <summary>完整的请求地址（含路径），直接 POST。</summary>
        public string Endpoint { get; init; } = "";

        public string DefaultModel { get; init; } = "";

        /// <summary>
        /// 内置的常用模型，**只是写这段代码那一刻的参考**，服务商改了型号名就会过期。
        /// 权威清单以「拉取列表」（<see cref="ModelCatalog"/>）为准，这里只作离线兜底。
        /// </summary>
        public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 模型列表接口的显式声明。**优先用对话地址推导**（用户可能换过中转地址），
        /// 推导不出来时才用这里；推导成功且仍在官方域名上时，这里的查询参数会带过去
        /// （Anthropic 不加 <c>?limit=1000</c> 只返回前 20 个）。
        /// </summary>
        public string ModelsEndpoint { get; init; } = "";

        /// <summary>申请 API Key 的控制台地址。</summary>
        public string ConsoleUrl { get; init; } = "";

        /// <summary>一句话注意事项（大小写敏感、占位符、本地鉴权等）。</summary>
        public string Note { get; init; } = "";

        /// <summary>个别服务商要求的附加请求头（如 OpenRouter 的 HTTP-Referer）。</summary>
        public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; } =
            new Dictionary<string, string>();

        public bool IsCustom => Id == ApiProviders.IdCustom;

        public string WireLabel => ApiProviders.WireLabel(Wire);

        public string AuthLabel => ApiProviders.AuthLabel(Auth);

        public override string ToString() => Name;
    }

    /// <summary>
    /// 主流大模型服务商的内置接入参数库。
    ///
    /// 设计意图：把「协议 / 地址 / 认证头 / 常用模型 / 申请入口」这五件事一次填好，
    /// 用户只需选一个服务商、粘一个 API Key，模型名可以从下拉里挑也可以自己写。
    /// 与服务商无关的坑（哪个头、system 放哪、模型名大小写）在这里一次性消化掉。
    /// </summary>
    public static class ApiProviders
    {
        public const string IdCustom = "custom";
        public const string IdOpenAi = "openai";

        public const string GroupDomestic = "国内主流";
        public const string GroupGlobal = "国际主流";
        public const string GroupRelay = "聚合中转";
        public const string GroupLocal = "本地部署";
        public const string GroupCustom = "自定义";

        /// <summary>下拉里的分组顺序。</summary>
        public static readonly string[] GroupOrder =
        {
            GroupDomestic, GroupGlobal, GroupRelay, GroupLocal, GroupCustom,
        };

        private static IReadOnlyList<ProviderPreset>? _all;

        /// <summary>
        /// ⚠ 必须惰性求值。写成静态字段初始化器（或在静态构造里引用还没赋值的字段）
        /// 会因为初始化顺序拿到空列表 —— 这个坑在提示词方案库上踩过一次。
        /// </summary>
        public static IReadOnlyList<ProviderPreset> All => _all ??= Build();

        public static ProviderPreset Custom => Find(IdCustom)!;

        /// <summary>按 Id 或显示名查找（大小写不敏感），都认不出就返回「自定义」。</summary>
        public static ProviderPreset? Find(string? idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return null;

            var key = idOrName.Trim();
            var list = All;

            foreach (var p in list)
                if (string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase)) return p;

            foreach (var p in list)
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) return p;

            // 旧配置里的历史写法（v1 存的是显示名或更早的叫法）
            foreach (var p in list)
                if (LegacyAliases(p.Id).Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase)))
                    return p;

            return null;
        }

        private static IEnumerable<string> LegacyAliases(string id) => id switch
        {
            "anthropic" => new[] { "Claude", "claude" },
            "openai" => new[] { "Open AI", "GPT" },
            "siliconflow" => new[] { "Silicon Flow", "硅基流动" },
            _ => Array.Empty<string>(),
        };

        public static string WireLabel(ApiWire wire) => wire switch
        {
            ApiWire.AnthropicMessages => "Anthropic Messages（/v1/messages，system 走顶层字段）",
            _ => "OpenAI 兼容（/v1/chat/completions，system 走 messages[0]）",
        };

        public static string AuthLabel(ApiAuth auth) => auth switch
        {
            ApiAuth.XApiKey => "x-api-key + anthropic-version",
            ApiAuth.ApiKeyHeader => "api-key（不带 anthropic-version）",
            _ => "Authorization: Bearer",
        };

        // ---------------------------------------------------------------
        // 协议与认证的解析
        //   - 协议：已知服务商以预设声明为准；只有「自定义 / 未识别」才按地址形状猜
        //     （/messages → Anthropic，/completions → OpenAI 兼容）。
        //   - 认证优先听用户在设置页显式选的（AuthOverride），否则用预设声明；
        //     自定义服务商没有声明可依据，按协议推断（Anthropic 格式默认 x-api-key）。
        // ---------------------------------------------------------------

        public static bool LooksAnthropic(string? url) =>
            !string.IsNullOrWhiteSpace(url) &&
            url.Contains("/messages", StringComparison.OrdinalIgnoreCase);

        public static bool LooksOpenAi(string? url) =>
            !string.IsNullOrWhiteSpace(url) &&
            url.Contains("/completions", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 判定协议。
        ///
        /// 规则：**选了已知服务商就听它的**，只有「自定义 / 未识别」才按地址形状猜。
        /// 反过来（让地址盖过服务商）会制造一个更隐蔽的坑：用户选了 DeepSeek、
        /// 顺手把地址换成某个 Anthropic 中转，协议被悄悄改掉，用户毫无察觉。
        /// 宁可规则简单可预测，矛盾时用 <see cref="WireWarning"/> 显式提醒。
        /// </summary>
        public static ApiWire ResolveWire(ApiConfig? config)
        {
            var preset = Find(config?.Provider);
            if (preset != null && !preset.IsCustom) return preset.Wire;

            if (LooksAnthropic(config?.ApiUrl)) return ApiWire.AnthropicMessages;
            if (LooksOpenAi(config?.ApiUrl)) return ApiWire.OpenAiChat;
            return ApiWire.OpenAiChat;
        }

        /// <summary>
        /// 地址形状与所选服务商声明的协议矛盾时给一条警告（否则会静默打不通）。
        /// 没矛盾返回 null。
        /// </summary>
        public static string? WireWarning(ApiConfig? config)
        {
            var preset = Find(config?.Provider);
            if (preset == null || preset.IsCustom) return null;

            var url = config?.ApiUrl;
            if (string.IsNullOrWhiteSpace(url)) return null;

            if (preset.Wire == ApiWire.AnthropicMessages && LooksOpenAi(url))
                return "⚠ 地址看起来是 OpenAI 兼容端点，与所选服务商不符。"
                       + "若这是你的中转地址，请把服务商改为「自定义 / 其它」。";

            if (preset.Wire == ApiWire.OpenAiChat && LooksAnthropic(url))
                return "⚠ 地址看起来是 Anthropic Messages 端点，与所选服务商不符。"
                       + "若这是你的中转地址，请把服务商改为「自定义 / 其它」。";

            return null;
        }

        public static ApiAuth ResolveAuth(ApiConfig? config)
        {
            var parsed = ParseAuthOverride(config?.AuthOverride);
            if (parsed != null) return parsed.Value;

            // 自定义服务商没有可依据的声明，按协议推断
            var preset = Find(config?.Provider);
            if (preset != null && !preset.IsCustom) return preset.Auth;

            return ResolveWire(config) == ApiWire.AnthropicMessages
                ? ApiAuth.XApiKey
                : ApiAuth.Bearer;
        }

        /// <summary>把用户手写的认证方式字符串（JSON 里可能写成 "bearer" / "api-key"）解析成枚举。</summary>
        public static ApiAuth? ParseAuthOverride(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var k = text.Trim();
            if (string.Equals(k, "bearer", StringComparison.OrdinalIgnoreCase)) return ApiAuth.Bearer;
            if (string.Equals(k, "x-api-key", StringComparison.OrdinalIgnoreCase)) return ApiAuth.XApiKey;
            if (string.Equals(k, "xapikey", StringComparison.OrdinalIgnoreCase)) return ApiAuth.XApiKey;
            if (string.Equals(k, "api-key", StringComparison.OrdinalIgnoreCase)) return ApiAuth.ApiKeyHeader;
            if (string.Equals(k, "apikeyheader", StringComparison.OrdinalIgnoreCase)) return ApiAuth.ApiKeyHeader;

            return null;
        }

        /// <summary>协议与认证的中文速览，给设置页与状态栏用。</summary>
        public static string Describe(ApiConfig? config)
        {
            if (config == null) return "";
            var preset = Find(config.Provider);
            var name = preset?.Name ?? config.Provider ?? "自定义";
            return $"{name} · {WireLabel(ResolveWire(config))} · {AuthLabel(ResolveAuth(config))}";
        }

        /// <summary>按配置挑一个实现。调用方不需要知道协议有哪些。</summary>
        public static IApiService CreateService(ApiConfig config) =>
            ResolveWire(config) == ApiWire.AnthropicMessages
                ? new AnthropicService(config)
                : new OpenAIService(config);

        // ---------------------------------------------------------------
        // 请求头装配（两个实现 + 测试连接共用同一份逻辑，避免三处各写一遍）
        // ---------------------------------------------------------------

        /// <summary>
        /// 套上认证头与协议必需的头，返回实际使用的认证方式描述（供「测试连接」回显）。
        /// </summary>
        public static string ApplyHeaders(HttpRequestHeaders headers, ApiConfig config)
        {
            var key = config.ApiKey ?? "";
            var auth = ResolveAuth(config);
            var wire = ResolveWire(config);

            switch (auth)
            {
                case ApiAuth.XApiKey:
                    headers.TryAddWithoutValidation("x-api-key", key);
                    if (wire == ApiWire.AnthropicMessages)
                        headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    return AuthLabel(ApiAuth.XApiKey);

                case ApiAuth.ApiKeyHeader:
                    headers.TryAddWithoutValidation("api-key", key);
                    return AuthLabel(ApiAuth.ApiKeyHeader);

                default:
                    headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    if (wire == ApiWire.AnthropicMessages)
                        headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    return AuthLabel(ApiAuth.Bearer);
            }
        }

        /// <summary>套上服务商额外要求的头（OpenRouter 的 HTTP-Referer / X-Title 等）。</summary>
        public static void ApplyExtraHeaders(HttpRequestHeaders headers, ApiConfig config)
        {
            var preset = Find(config.Provider);
            if (preset == null) return;

            foreach (var kv in preset.ExtraHeaders)
                headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        // ---------------------------------------------------------------
        // 内置参数表
        // ---------------------------------------------------------------

        private static ProviderPreset Preset(
            string id, string name, string group, ApiWire wire, ApiAuth auth,
            string endpoint, string defaultModel, string[] models,
            string console = "", string note = "", (string Key, string Value)[]? headers = null,
            string modelsEndpoint = "")
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
                foreach (var h in headers) dict[h.Key] = h.Value;

            return new ProviderPreset
            {
                Id = id,
                Name = name,
                Group = group,
                Wire = wire,
                Auth = auth,
                Endpoint = endpoint,
                DefaultModel = defaultModel,
                Models = models,
                ModelsEndpoint = modelsEndpoint,
                ConsoleUrl = console,
                Note = note,
                ExtraHeaders = dict,
            };
        }

        private const ApiWire Oa = ApiWire.OpenAiChat;
        private const ApiWire An = ApiWire.AnthropicMessages;
        private const ApiAuth Bearer = ApiAuth.Bearer;
        private const ApiAuth XKey = ApiAuth.XApiKey;
        private const ApiAuth AKey = ApiAuth.ApiKeyHeader;

        private static IReadOnlyList<ProviderPreset> Build() => new List<ProviderPreset>
        {
            // ================= 国内主流 =================

            Preset("deepseek", "DeepSeek", GroupDomestic, Oa, Bearer,
                "https://api.deepseek.com/v1/chat/completions",
                "deepseek-chat",
                new[] { "deepseek-chat", "deepseek-reasoner" },
                "https://platform.deepseek.com/api_keys"),

            Preset("moonshot", "月之暗面 Kimi", GroupDomestic, Oa, Bearer,
                "https://api.moonshot.cn/v1/chat/completions",
                "moonshot-v1-8k",
                new[] { "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k", "kimi-latest" },
                "https://platform.moonshot.cn/console/api-keys"),

            Preset("zhipu", "智谱 GLM", GroupDomestic, Oa, Bearer,
                "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                "glm-4-flash",
                new[] { "glm-4-flash", "glm-4-air", "glm-4-plus", "glm-4-long" },
                "https://bigmodel.cn/usercenter/apikeys",
                "注意地址是 /api/paas/v4，不是常见的 /v1。"),

            Preset("dashscope", "阿里通义千问", GroupDomestic, Oa, Bearer,
                "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                "qwen-plus",
                new[] { "qwen-plus", "qwen-turbo", "qwen-max", "qwen-long" },
                "https://bailian.console.aliyun.com/",
                "走的是 DashScope 的 OpenAI 兼容模式端点。"),

            Preset("volces", "火山方舟 · 豆包", GroupDomestic, Oa, Bearer,
                "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
                "doubao-seed-1-6-250615",
                new[] { "doubao-seed-1-6-250615", "doubao-1-5-pro-32k-250115" },
                "https://console.volcengine.com/ark",
                "模型名可填模型 ID，也可填你在方舟创建的推理接入点 ID（ep- 开头）。"),

            Preset("hunyuan", "腾讯混元", GroupDomestic, Oa, Bearer,
                "https://api.hunyuan.cloud.tencent.com/v1/chat/completions",
                "hunyuan-turbos-latest",
                new[] { "hunyuan-turbos-latest", "hunyuan-t1-latest", "hunyuan-lite" },
                "https://console.cloud.tencent.com/hunyuan/api-key"),

            Preset("qianfan", "百度文心", GroupDomestic, Oa, Bearer,
                "https://qianfan.baidubce.com/v2/chat/completions",
                "ernie-4.5-turbo-128k",
                new[] { "ernie-4.5-turbo-128k", "ernie-4.5-8k-preview", "ernie-speed-128k" },
                "https://console.bce.baidu.com/iam/#/iam/apikey/list",
                "用千帆 v2 接口，Key 形如 bce-v3/ALTAK-..."),

            Preset("minimax", "MiniMax", GroupDomestic, Oa, Bearer,
                "https://api.minimax.chat/v1/chat/completions",
                "MiniMax-Text-01",
                new[] { "MiniMax-Text-01", "MiniMax-M1", "abab6.5s-chat" },
                "https://platform.minimaxi.com/user-center/basic-information/interface-key",
                "★ 模型名区分大小写，请勿改成全小写。"),

            Preset("stepfun", "阶跃星辰 StepFun", GroupDomestic, Oa, Bearer,
                "https://api.stepfun.com/v1/chat/completions",
                "step-2-16k",
                new[] { "step-2-16k", "step-1-8k", "step-1-flash" },
                "https://platform.stepfun.com/interface-key"),

            Preset("lingyiwanwu", "零一万物 Yi", GroupDomestic, Oa, Bearer,
                "https://api.lingyiwanwu.com/v1/chat/completions",
                "yi-lightning",
                new[] { "yi-lightning", "yi-large", "yi-medium" },
                "https://platform.lingyiwanwu.com/apikeys"),

            Preset("siliconflow", "硅基流动 SiliconFlow", GroupDomestic, Oa, Bearer,
                "https://api.siliconflow.cn/v1/chat/completions",
                "deepseek-ai/DeepSeek-V3",
                new[]
                {
                    "deepseek-ai/DeepSeek-V3",
                    "deepseek-ai/DeepSeek-R1",
                    "Qwen/Qwen2.5-72B-Instruct",
                },
                "https://cloud.siliconflow.cn/account/ak",
                "★ 模型名形如 组织/模型 且区分大小写，例如 deepseek-ai/DeepSeek-V3。"),

            Preset("mimo", "小米 Mimo（Token Plan）", GroupDomestic, An, AKey,
                "https://token-plan-cn.xiaomimimo.com/anthropic/v1/messages",
                "mimo-v2.5-pro",
                new[] { "mimo-v2.5-pro" },
                "https://xiaomimimo.com",
                "Anthropic 格式，但认证头是 api-key 且不加 anthropic-version。"),

            // ================= 国际主流 =================

            Preset("openai", "OpenAI", GroupGlobal, Oa, Bearer,
                "https://api.openai.com/v1/chat/completions",
                "gpt-4o-mini",
                new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-4.1-mini", "o4-mini" },
                "https://platform.openai.com/api-keys"),

            Preset("anthropic", "Claude（Anthropic）", GroupGlobal, An, XKey,
                "https://api.anthropic.com/v1/messages",
                "claude-sonnet-4-5",
                new[] { "claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5" },
                "https://console.anthropic.com/settings/keys",
                "Anthropic 格式：认证走 x-api-key，system 是顶层字段。模型名建议用「拉取列表」拿完整的带日期版本。",
                modelsEndpoint: "https://api.anthropic.com/v1/models?limit=1000"),

            Preset("gemini", "Google Gemini", GroupGlobal, Oa, Bearer,
                "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                "gemini-2.0-flash",
                new[] { "gemini-2.0-flash", "gemini-2.5-flash", "gemini-2.5-pro" },
                "https://aistudio.google.com/apikey",
                "走 Gemini 的 OpenAI 兼容端点，Google 原生协议不支持。"),

            Preset("xai", "xAI Grok", GroupGlobal, Oa, Bearer,
                "https://api.x.ai/v1/chat/completions",
                "grok-3-mini",
                new[] { "grok-3", "grok-3-mini", "grok-2-1212" },
                "https://console.x.ai"),

            Preset("mistral", "Mistral", GroupGlobal, Oa, Bearer,
                "https://api.mistral.ai/v1/chat/completions",
                "mistral-large-latest",
                new[] { "mistral-large-latest", "mistral-small-latest", "open-mistral-nemo" },
                "https://console.mistral.ai/api-keys"),

            Preset("groq", "Groq", GroupGlobal, Oa, Bearer,
                "https://api.groq.com/openai/v1/chat/completions",
                "llama-3.3-70b-versatile",
                new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "qwen/qwen3-32b" },
                "https://console.groq.com/keys"),

            // ================= 聚合中转 =================

            Preset("openrouter", "OpenRouter", GroupRelay, Oa, Bearer,
                "https://openrouter.ai/api/v1/chat/completions",
                "anthropic/claude-sonnet-4.5",
                new[]
                {
                    "anthropic/claude-sonnet-4.5",
                    "openai/gpt-4o-mini",
                    "google/gemini-2.5-flash",
                    "deepseek/deepseek-chat",
                },
                "https://openrouter.ai/keys",
                "一个 Key 通吃多家模型，模型名形如 厂商/型号。",
                new[] { ("HTTP-Referer", "https://github.com/zhou2228653446/DDD_WRITING_HELPER"), ("X-Title", "编辑器 · AI 写作助手") }),

            Preset("relay", "自建中转（One API / New API 等）", GroupRelay, Oa, Bearer,
                "http://localhost:3000/v1/chat/completions",
                "deepseek-chat",
                new[] { "deepseek-chat", "gpt-4o-mini", "claude-sonnet-4-5" },
                "https://github.com/songquanpeng/one-api",
                "把地址改成你中转服务的地址；模型名填中转后台里配置的那个。"),

            // ================= 本地部署 =================

            Preset("ollama", "Ollama（本机）", GroupLocal, Oa, Bearer,
                "http://localhost:11434/v1/chat/completions",
                "qwen2.5:7b",
                new[] { "qwen2.5:7b", "qwen2.5:14b", "llama3.1:8b", "deepseek-r1:7b" },
                "https://ollama.com",
                "本地无需鉴权，API Key 随便填一个非空值（例如 ollama）即可。"),

            Preset("lmstudio", "LM Studio（本机）", GroupLocal, Oa, Bearer,
                "http://localhost:1234/v1/chat/completions",
                "local-model",
                new[] { "local-model" },
                "https://lmstudio.ai",
                "在 LM Studio 里开启 Local Server；API Key 随便填一个非空值。"),

            Preset("vllm", "vLLM / 自建推理服务", GroupLocal, Oa, Bearer,
                "http://localhost:8000/v1/chat/completions",
                "Qwen/Qwen2.5-7B-Instruct",
                new[] { "Qwen/Qwen2.5-7B-Instruct" },
                "https://docs.vllm.ai",
                "模型名填启动时 --served-model-name 指定的名字；Key 随便填。"),

            // ================= 自定义 =================

            Preset(IdCustom, "自定义 / 其它", GroupCustom, Oa, Bearer,
                "", "", Array.Empty<string>(), "",
                "地址、协议、认证方式全部自己填。地址里出现 /messages 会自动按 Anthropic 格式处理。"),
        };
    }
}
