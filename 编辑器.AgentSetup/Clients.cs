using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace 编辑器.AgentSetup;

/// <summary>一个 agent 客户端的接入方式。</summary>
internal sealed class ClientTarget
{
    public string Id = "";
    public string Name = "";
    /// <summary>配置文件绝对路径；null = 这台机器上没法定位（比如未安装）。</summary>
    public string? Path;
    /// <summary>json / toml / yaml</summary>
    public string Kind = "json";
    /// <summary>JSON 里服务器表所在键（VS Code 用 servers，其余 mcpServers）。</summary>
    public string Key = "mcpServers";
    /// <summary>写完之后要提醒用户的话。</summary>
    public string Note = "";
    /// <summary>是否支持 HTTP（streamable http）接入。不支持的老客户端只能走 stdio。</summary>
    public bool SupportsHttp = true;
}

/// <summary>
/// 各家 agent 客户端的配置位置与写法。
///
/// ⚠ 客户端清单与 <c>install-mcp.py</c> 的 CLIENTS 表**同源**（那边是 stdio 版、
///   这边是 HTTP 版）。加一家客户端要两处一起改——两份表放同一个仓库里，
///   比"让 exe 去读 Python 脚本"实际（用户设备上没有 Python）。
/// </summary>
internal static class Clients
{
    public static List<ClientTarget> Detect(string configRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appdata = Environment.GetEnvironmentVariable("APPDATA") ?? "";
        // configRoot 只给自检/测试用：把"用户目录"整体搬到临时目录，
        // 这样测试永远不会碰到跑测试那台机器上的真实客户端配置。
        if (!string.IsNullOrEmpty(configRoot))
        {
            home = configRoot;
            appdata = System.IO.Path.Combine(configRoot, "Roaming");
        }

        return new List<ClientTarget>
        {
            new()
            {
                Id = "claude", Name = "Claude Desktop",
                Path = System.IO.Path.Combine(appdata, "Claude", "claude_desktop_config.json"),
                Note = "改完要完全退出 Claude 再启动（它只在启动时读配置）",
            },
            new()
            {
                Id = "cursor", Name = "Cursor",
                Path = System.IO.Path.Combine(home, ".cursor", "mcp.json"),
                Note = "全局配置；改完重启 Cursor",
            },
            new()
            {
                Id = "workbuddy", Name = "WorkBuddy",
                Path = System.IO.Path.Combine(home, ".workbuddy", "mcp.json"),
                Note = "装完在连接器管理页点「信任」新服务器",
            },
            new()
            {
                Id = "cline", Name = "Cline（VS Code 扩展）",
                Path = System.IO.Path.Combine(appdata, "Code", "User", "globalStorage",
                                              "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
            },
            new()
            {
                Id = "antigravity", Name = "Google Antigravity",
                Path = System.IO.Path.Combine(home, ".gemini", "config", "mcp_config.json"),
                Note = "IDE：Settings ▸ Customizations ▸ Open MCP Config",
            },
            new()
            {
                Id = "codex", Name = "Codex CLI",
                Path = System.IO.Path.Combine(home, ".codex", "config.toml"),
                Kind = "toml",
                Note = "写的是结构化合并，不动你已有的其它 [mcp_servers.*] 段；装完 `codex mcp list` 验证",
            },
            new()
            {
                Id = "continue", Name = "Continue",
                Path = System.IO.Path.Combine(home, ".continue", "config.yaml"),
                Kind = "yaml", SupportsHttp = true,
                Note = "Continue 用 YAML，格式各家版本差异大，这里只打印片段不自动写",
            },
        };
    }

    /// <summary>一个目标是否"这台机器上装了"——以配置文件或它所在的目录是否存在判断。</summary>
    public static bool LooksInstalled(ClientTarget t)
    {
        if (t.Path == null) return false;
        if (File.Exists(t.Path)) return true;
        var dir = System.IO.Path.GetDirectoryName(t.Path);
        return dir != null && Directory.Exists(dir);
    }

    /// <summary>写入结果：成功 / 跳过 / 失败，都带上给人看的一句话。</summary>
    public sealed record WriteResult(string Client, bool Ok, string Message);

    public static WriteResult Write(ClientTarget t, string serverName, string url, string token, bool dryRun)
    {
        if (t.Path == null) return new WriteResult(t.Name, false, "这台机器上找不到它的配置位置");

        if (t.Kind == "yaml")
            return new WriteResult(t.Name, false, t.Note.Length > 0 ? t.Note : "YAML 客户端请手动粘贴（见下方片段）");

        if (!t.SupportsHttp)
            return new WriteResult(t.Name, false, "该客户端只支持 stdio，请改用 install-mcp.bat 那条路");

        try
        {
            var text = t.Kind == "toml" ? MergeToml(t, serverName, url, token)
                                        : MergeJson(t, serverName, url, token);
            if (dryRun) return new WriteResult(t.Name, true, $"（演练）将写入 {t.Path}");
            Backup(t.Path);
            File.WriteAllText(t.Path, text, new UTF8Encoding(false));
            return new WriteResult(t.Name, true, "已写入" + (t.Note.Length > 0 ? "；" + t.Note : ""));
        }
        catch (Exception ex)
        {
            return new WriteResult(t.Name, false, "写入失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 改用户已有配置是危险动作（那是他攒了很久的东西），所以：
    /// 先备份一份带时间戳的副本，再整文件重写。
    /// </summary>
    private static void Backup(string path)
    {
        if (!File.Exists(path)) return;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(path, path + ".bak-" + stamp, overwrite: true);
    }

    private static string MergeJson(ClientTarget t, string serverName, string url, string token)
    {
        JsonObject root;
        if (File.Exists(t.Path!))
        {
            var raw = File.ReadAllText(t.Path!);
            root = string.IsNullOrWhiteSpace(raw)
                ? new JsonObject()
                : (JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException("配置文件不是 JSON 对象"));
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(t.Path!);
            if (dir != null) Directory.CreateDirectory(dir);
            root = new JsonObject();
        }

        JsonObject table;
        if (root[t.Key] is JsonObject existing) table = existing;
        else { table = new JsonObject(); root[t.Key] = table; }

        // ⚠ 只动 tdxclaw 这一项，其余键原样保留（用户可能有别的 MCP 服务器）
        table[serverName] = new JsonObject
        {
            ["type"] = "http",
            ["url"] = url,
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Codex 的 TOML：只替换/追加 [mcp_servers.&lt;name&gt;] 段，别的内容原样保留。
    /// 不做完整 TOML 解析（会重排格式、丢注释），纯文本段替换最不伤用户文件。
    /// </summary>
    private static string MergeToml(ClientTarget t, string serverName, string url, string token)
    {
        var block = new StringBuilder()
            .Append("[mcp_servers.").Append(serverName).Append("]\n")
            .Append("url = \"").Append(EscapeToml(url)).Append("\"\n")
            .Append("transport = \"streamable-http\"\n")
            .Append("http_headers = { \"Authorization\" = \"Bearer ").Append(EscapeToml(token)).Append("\" }\n")
            .ToString();

        if (!File.Exists(t.Path!))
        {
            var dir = System.IO.Path.GetDirectoryName(t.Path!);
            if (dir != null) Directory.CreateDirectory(dir);
            return block;
        }

        var lines = File.ReadAllLines(t.Path!).ToList();
        var head = "[mcp_servers." + serverName + "]";
        int start = lines.FindIndex(l => l.Trim() == head);
        if (start < 0)
        {
            var text = string.Join("\n", lines);
            if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
            return text + "\n" + block;
        }
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
        lines.RemoveRange(start, end - start);
        lines.Insert(start, block.TrimEnd());
        return string.Join("\n", lines) + "\n";
    }

    private static string EscapeToml(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
