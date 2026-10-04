using System.Text;
using 编辑器.Services;

namespace 编辑器.Mcp;

/// <summary>
/// MCP 资源（resources/*）：把打开的项目暴露成 `tdx://` 地址。
///
/// ★ 为什么要做资源而不只用工具：
/// 支持资源的客户端（Antigravity、Cline、Continue、Claude 等）可以在对话里直接
/// **引用/附加**一个资源，模型侧看到的是"一份带名字的文稿"，而不是再调一次工具
/// 才能拿到正文。对写作场景尤其合适：agent 说"参考第 3 章"时能把整章挂进上下文。
/// 不做的后果：agent 每次都得先 chapters_list 再 chapter_read，多一轮往返，
/// 而且拿不到稳定的引用标识。
///
/// URI 方案：
///   tdx://chapter/{章号}              章节正文
///   tdx://settings/{full_outline|chapter_outline|characters|background|style|viewpoint}
///   tdx://settings-book/{SourceKey}   设定集某一章
/// </summary>
internal static class Resources
{
    public const string Scheme = "tdx";

    /// <summary>当前项目可列出的资源。没开项目就返回空列表（不是错误）。</summary>
    public static List<object> List(Session s)
    {
        var list = new List<object>();
        if (!s.TryGet(out var p, out _)) return list;

        foreach (var c in p.Chapters.OrderBy(c => c.ChapterNumber))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["uri"] = $"{Scheme}://chapter/{c.ChapterNumber}",
                ["name"] = $"第{c.ChapterNumber}章 · {c.Title}",
                ["description"] = $"{c.WordCount} 字 · 修改于 {c.ModifiedDate:yyyy-MM-dd HH:mm}",
                ["mimeType"] = "text/plain",
            });
        }

        AddSetting(list, "full_outline", "全文大纲", p.FullOutline);
        AddSetting(list, "chapter_outline", "章节大纲", p.ChapterOutline);
        AddSetting(list, "characters", "人物设定", p.CharacterSettings);
        AddSetting(list, "background", "背景设定", p.BackgroundSettings);
        AddSetting(list, "style", "文风设定", p.WritingStyle);
        AddSetting(list, "viewpoint", "叙事视角", p.NarrativeViewpoint);

        if (p.SettingsBook?.Chapters.Count > 0)
        {
            foreach (var c in p.SettingsBook.Chapters)
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["uri"] = $"{Scheme}://settings-book/{c.SourceKey}",
                    ["name"] = $"设定集 · {c.Title}",
                    ["description"] = string.IsNullOrWhiteSpace(c.Content)
                        ? "（空章）"
                        : $"{c.Content.Length} 字{(c.IsAiGenerated ? " · AI 生成" : "")}",
                    ["mimeType"] = "text/plain",
                });
            }
        }

        return list;
    }

    private static void AddSetting(List<object> list, string key, string title, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;   // 空设定不占列表，避免 agent 读到一堆空资源
        list.Add(new Dictionary<string, object?>
        {
            ["uri"] = $"{Scheme}://settings/{key}",
            ["name"] = title,
            ["description"] = $"{value!.Length} 字",
            ["mimeType"] = "text/plain",
        });
    }

    /// <summary>读一个资源。返回 MCP 要求的 contents 数组。</summary>
    public static (List<object> Contents, bool IsError, string? Message) Read(Session s, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return (new(), true, "缺少 uri。");

        if (!uri.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
            return (new(), true, $"不认识的 URI：{uri}（本服务器只提供 {Scheme}:// 开头的资源）");

        if (!s.TryGet(out var p, out var err))
            return (new(), true, err.Text);

        var body = uri[(Scheme.Length + 3)..];      // 去掉 "tdx://"
        var slash = body.IndexOf('/');
        var kind = slash < 0 ? body : body[..slash];
        var tail = slash < 0 ? "" : body[(slash + 1)..];

        string text;
        switch (kind.ToLowerInvariant())
        {
            case "chapter":
            {
                if (!int.TryParse(tail, out var n))
                    return (new(), true, $"章号不是数字：{tail}");
                var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
                if (c == null)
                    return (new(), true, $"没有第 {n} 章。");
                text = $"第{c.ChapterNumber}章「{c.Title}」\n\n{c.Content}";
                break;
            }

            case "settings":
            {
                text = tail.ToLowerInvariant() switch
                {
                    "full_outline" => p.FullOutline ?? "",
                    "chapter_outline" => p.ChapterOutline ?? "",
                    "characters" => p.CharacterSettings ?? "",
                    "background" => p.BackgroundSettings ?? "",
                    "style" => p.WritingStyle ?? "",
                    "viewpoint" => p.NarrativeViewpoint ?? "",
                    _ => "",
                };
                if (text.Length == 0)
                    return (new(), true,
                        $"未知或为空的设定：{tail}。可选 full_outline / chapter_outline / characters / background / style / viewpoint");
                break;
            }

            case "settings-book":
            {
                var book = p.SettingsBook;
                if (book == null || book.Chapters.Count == 0)
                    return (new(), true, "这个项目还没有设定集。");
                var one = book.Chapters.FirstOrDefault(c =>
                    string.Equals(c.SourceKey, tail, StringComparison.OrdinalIgnoreCase));
                if (one == null)
                    return (new(), true, $"设定集里没有 key 为「{tail}」的章。用 resources/list 看全部。");
                text = $"设定集 · {one.Title}\n\n" +
                       (string.IsNullOrWhiteSpace(one.Content) ? "（本章还是空的）" : one.Content);
                break;
            }

            default:
                return (new(), true, $"未知资源类型：{kind}。可选 chapter / settings / settings-book");
        }

        return (new List<object>
        {
            new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["mimeType"] = "text/plain",
                ["text"] = text,
            },
        }, false, null);
    }
}
