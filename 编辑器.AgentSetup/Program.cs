using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using 编辑器.AgentSetup;

// ==================================================================
// TdxClaw 写作助手 · Agent 接入助手
//
// 给"在另一台设备上接 agent"的人用：双击 → 问三个问题 → 自动登录换令牌
// → 自动写进这台机器上装着的 agent 客户端。省掉"找配置文件、手抄 JSON、
// 令牌写错一个字符"这一整串。
//
// 设计上的几条硬规矩：
//   · 密码只用一次（换令牌），绝不落盘、不回显
//   · 动用户已有配置文件之前先备份（.bak-时间戳）
//   · 只改 tdxclaw 这一项，别的 MCP 服务器原样保留
//   · 服务器地址默认内置公网域名，回车即用；自建部署可自己输
// ==================================================================

var opt = Options.Parse(args);
if (opt.Help)
{
    Options.PrintHelp();
    return 0;
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine();
Console.WriteLine("TdxClaw 写作助手 · Agent 接入助手");
Console.WriteLine("=================================================");
Console.WriteLine("这个工具会：连到你的写作服务，换一个访问令牌，然后自动写进");
Console.WriteLine("这台电脑上装着的 AI 客户端（Claude / Cursor / Codex / WorkBuddy…）。");
Console.WriteLine();

// ── 1. 服务器地址 ──
var server = opt.Server ?? Ask("服务器地址", Options.DefaultServer);
server = server.TrimEnd('/');
if (!server.StartsWith("http", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("！地址要以 http:// 开头，例如 " + Options.DefaultServer);
    return 1;
}
var mcpUrl = server + "/mcp";

// ── 2. 账号 ──
string user = opt.User ?? Ask("账号类型（1=自用  2=试用）", "1").Trim() switch
{
    "2" or "trial" or "试用" => "trial",
    _ => "self",
};
string password = opt.Password ?? AskPassword(user == "trial" ? "试用密码" : "自用密码");
if (password.Length == 0) { Console.WriteLine("！密码不能为空"); return 1; }

string code = "";
if (user == "trial")
{
    code = opt.Code ?? Ask("四位个人码（区分试用者，自己定）", "").Trim();
    if (code.Length != 4 || !code.All(char.IsDigit)) { Console.WriteLine("！个人码要四位数字"); return 1; }
}

// ── 3. 登录换令牌 ──
Console.WriteLine();
Console.Write("→ 正在登录 " + server + " … ");
string token;
try
{
    token = Login(server, user, password, code);
    Console.WriteLine("✓ 拿到令牌");
}
catch (LoginException ex)
{
    Console.WriteLine("✗ 失败");
    Console.WriteLine();
    Console.WriteLine("  " + ex.Message);
    Console.WriteLine();
    Console.WriteLine("  常见原因：");
    Console.WriteLine("   · 密码不对 / 试用密码已轮换（向管理员要最新的）");
    Console.WriteLine("   · 试用名额已满（最多 3 人同时在线，等有人退出再试）");
    Console.WriteLine("   · 这台设备连不上服务器：先确认能打开 " + server);
    Console.WriteLine("     家宽走 IPv6，部分公司网/手机热点没有 IPv6 —— 换网络再试");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine("✗ 连不上");
    Console.WriteLine();
    Console.WriteLine("  错误：" + ex.Message);
    Console.WriteLine("  先确认浏览器能打开 " + server + " ；能打开但这里失败，多半是网络环境差异。");
    return 1;
}

// ── 4. 检测客户端 ──
Console.WriteLine();
Console.WriteLine("→ 正在检测这台电脑上的 AI 客户端…");
var targets = Clients.Detect(opt.ConfigRoot ?? "");
var installed = targets.Where(Clients.LooksInstalled).ToList();
if (installed.Count == 0)
{
    Console.WriteLine("  没检测到已安装的客户端（这不影响下面手动配置）。");
}
else
{
    foreach (var t in installed) Console.WriteLine("  ✓ " + t.Name);
}

// ── 5. 写配置 ──
const string serverName = "tdxclaw";
Console.WriteLine();
if (opt.PrintOnly)
{
    Console.WriteLine("（--print：只输出片段，不写任何文件）");
}
else
{
    var results = new List<Clients.WriteResult>();
    foreach (var t in installed)
        results.Add(Clients.Write(t, serverName, mcpUrl, token, opt.DryRun));

    foreach (var r in results)
        Console.WriteLine((r.Ok ? "  ✓ " : "  · ") + r.Client + "：" + r.Message);
    Console.WriteLine();
    if (!opt.DryRun && results.Any(r => r.Ok))
        Console.WriteLine("→ 配置已写入。重启对应的客户端后生效（Claude Desktop 必须完全退出再开）。");
}

Console.WriteLine();
Console.WriteLine("=================================================");
Console.WriteLine("配置片段（客户端没自动配上的，把下面整段贴进它的 mcpServers 里）：");
Console.WriteLine();
Console.WriteLine(ConfigJson(mcpUrl, token));
Console.WriteLine();
Console.WriteLine("客户端不吃自定义请求头的，把令牌写进地址（效果一样）：");
Console.WriteLine("  " + mcpUrl + "?access_token=" + token);
Console.WriteLine();
Console.WriteLine("⚠ 令牌存在服务器内存里：服务重启或重新发布后失效，");
Console.WriteLine("  回网页「帮助 → 接入 Agent」重新生成，或再跑一次本工具。");
Console.WriteLine("⚠ 这个窗口可以直接关掉。密码没有被保存。");
if (opt.DryRun) Console.WriteLine("（--dry-run：上面所有写入都只是演练，没有真的改文件）");
Console.WriteLine();
return 0;

// ──────────────────────────────────────────────────────────────────

static string Ask(string prompt, string def)
{
    Console.Write(prompt + (def.Length > 0 ? $"（回车用 {def}）" : "") + "：");
    var s = Console.ReadLine();
    return string.IsNullOrWhiteSpace(s) ? def : s.Trim();
}

/// <summary>密码输入：不回显（控制台上一个字符都不显示，避免肩窥/留痕）。</summary>
static string AskPassword(string prompt)
{
    Console.Write(prompt + "（不回显）：");
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) sb.Length--;
            continue;
        }
        if (k.KeyChar != '\0') sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static string Login(string server, string user, string password, string code)
{
    var payload = new Dictionary<string, object> { ["user"] = user, ["password"] = password };
    if (user == "trial") payload["code"] = code;

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    HttpResponseMessage resp;
    try
    {
        resp = http.PostAsync(server + "/api/auth/login", body).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        throw new Exception(ex.InnerException?.Message ?? ex.Message, ex);
    }

    var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    var json = JsonDocument.Parse(text).RootElement;

    if (!resp.IsSuccessStatusCode)
    {
        var err = json.TryGetProperty("error", out var e) ? e.GetString() ?? "" : $"HTTP {(int)resp.StatusCode}";
        if (json.TryGetProperty("needInit", out var ni) && ni.GetBoolean())
            err = "这台服务器还没初始化账号：先在它的本机浏览器上打开并设置自用密码";
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            err += "（等一会儿再试，或让管理员在帮助页释放名额）";
        throw new LoginException(err);
    }

    return json.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
}

static string ConfigJson(string url, string token) =>
    new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["tdxclaw"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = url,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

internal sealed class LoginException(string message) : Exception(message);

internal sealed class Options
{
    public const string DefaultServer = "http://ddd-lighthouse.ddns.net:5280";

    public string? Server;
    public string? User;
    public string? Password;
    public string? Code;
    public bool DryRun;
    public bool PrintOnly;
    public bool Help;
    /// <summary>测试用：把"用户目录"换成指定目录，避免碰到真实客户端配置。</summary>
    public string? ConfigRoot;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server": o.Server = Next(args, ref i); break;
                case "--user": o.User = Next(args, ref i); break;
                case "--password": o.Password = Next(args, ref i); break;
                case "--code": o.Code = Next(args, ref i); break;
                case "--config-root": o.ConfigRoot = Next(args, ref i); break;
                case "--dry-run": o.DryRun = true; break;
                case "--print": o.PrintOnly = true; break;
                case "--help" or "-h" or "/?": o.Help = true; break;
            }
        }
        return o;
    }

    private static string Next(string[] a, ref int i) => i + 1 < a.Length ? a[++i] : "";

    public static void PrintHelp()
    {
        Console.WriteLine("""
            TdxClaw Agent 接入助手

            用法：TdxClaw.AgentSetup.exe [选项]     不带选项就是交互式问答

              --server <url>      服务器地址（默认 http://ddd-lighthouse.ddns.net:5280）
              --user <self|trial> 账号类型
              --password <密码>   不给就交互输入（交互输入不回显）
              --code <四位码>     试用账号的个人码
              --print             只打印配置片段，不写任何文件
              --dry-run           演练：显示会写哪些文件，但不真的写
              --help              这份说明
            """);
    }
}
