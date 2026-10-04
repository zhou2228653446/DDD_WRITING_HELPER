using System.Text;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Mcp;

/// <summary>
/// 内置自检：`编辑器.Mcp.exe --selftest`。
///
/// 走的是**真实协议通道**：把一串 JSON-RPC 请求喂进 McpServer，再逐行解析响应。
/// 不另起 harness 工程是因为协议实现最大的风险就在「分帧/通知不回/错误码」这些地方，
/// 只有真跑一遍 stdio 才验得到；直接调工具方法会漏掉整条链路。
/// </summary>
internal static class SelfTest
{
    private static int _pass, _fail;

    public static async Task<int> RunAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcp_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var projPath = Path.Combine(dir, "测试项目.tdxproj");
        var exportPath = Path.Combine(dir, "out.txt");

        try
        {
            // ---- 造一个真实项目（设定集也补建，验证 12 章齐全）----
            var p = new NovelProject
            {
                ProjectName = "MCP自检之书",
                Description = "用于验证 MCP 链路的临时项目",
                FilePath = projPath,
                FullOutline = "林寒入山→得剑→下山",
                CharacterSettings = "林寒：主角，木讷但执拗。\n苏晚：医者。",
                WritingStyle = "冷峻白描",
                NarrativeViewpoint = "第三人称限知·跟随林寒",
            };
            SettingsBookTemplates.EnsureBook(p);
            p.Chapters.Add(new Chapter
            {
                ChapterNumber = 1, Title = "入山",
                Content = "林寒第一次看见那座山，是在霜降之后。苏晚说山里有他要找的东西。",
            });
            p.Chapters.Add(new Chapter
            {
                ChapterNumber = 2, Title = "得剑",
                Content = "剑在石缝里，锈得看不出本来面目。林寒伸手去拔，指腹立刻被割开一道口子。",
            });
            p.Save();

            // ---- 组装一次完整会话 ----
            var requests = new List<string>
            {
                Req(1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "selftest", version = "0" } }),
                Notify("notifications/initialized"),                       // 通知：不该有响应
                Req(2, "tools/list", new { }),
                Req(3, "tools/call", new { name = "chapters_list", arguments = new { } }),           // 未打开项目 → 应报错
                Req(4, "tools/call", new { name = "project_open", arguments = new { path = projPath } }),
                Req(5, "tools/call", new { name = "chapters_list", arguments = new { } }),
                Req(6, "tools/call", new { name = "chapter_read", arguments = new { number = 1 } }),
                Req(7, "tools/call", new { name = "settings_get", arguments = new { } }),
                Req(8, "tools/call", new { name = "settings_book_get", arguments = new { } }),
                Req(9, "tools/call", new { name = "chapter_search", arguments = new { keyword = "林寒" } }),
                Req(10, "tools/call", new { name = "characters_stats", arguments = new { } }),
                Req(11, "tools/call", new { name = "chapter_write", arguments = new { number = 2, content = "他没松手。", mode = "append" } }),
                Req(12, "tools/call", new { name = "chapter_read", arguments = new { number = 2 } }),
                Req(13, "tools/call", new { name = "settings_set", arguments = new { field = "writing_style", text = "冷峻白描，短句为主" } }),
                Req(14, "tools/call", new { name = "project_export", arguments = new { format = "txt", output = exportPath } }),
                Req(15, "tools/call", new { name = "不存在的工具", arguments = new { } }),
                Req(16, "不存在的办法", new { }),                            // 协议级错误
            };

            // ---- 跑一遍真实 stdio 循环 ----
            var output = new StringWriter();
            await McpServer.RunAsync(new StringReader(string.Join("\n", requests) + "\n"), output);
            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .Select(l => JsonDocument.Parse(l)).ToList();

            // ---- 断言 ----
            // 16 个有 id 的请求 + 1 条通知（通知不回）→ 恰好 16 条响应
            Assert("响应条数 = 有 id 的请求数（通知不产生响应）", lines.Count == 16, lines.Count.ToString());

            var init = lines[0].RootElement;
            Assert("initialize 返回 protocolVersion",
                init.GetProperty("result").TryGetProperty("protocolVersion", out var pv) && pv.GetString() == "2024-11-05", "");
            Assert("initialize 返回 serverInfo.name",
                init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "TdxClaw 写作助手", "");

            var tools = lines[1].RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                              .Select(t => t.GetProperty("name").GetString()!).ToList();
            Assert("tools/list 列出 16 个工具", tools.Count == 16, string.Join(",", tools));
            Assert("工具集覆盖 读/写/导出/AI",
                tools.Contains("chapter_read") && tools.Contains("chapter_write")
                && tools.Contains("project_export") && tools.Contains("ai_write"), "");
            Assert("工具都有 description（agent 靠它决定调谁）",
                lines[1].RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                    .All(t => t.GetProperty("description").GetString()!.Length > 5), "");

            Assert("未打开项目时给可操作提示", IsError(lines[2]) && Text(lines[2]).Contains("project_open"), Cut(Text(lines[2]), 40));
            Assert("project_open 成功并显示项目名", Text(lines[3]).Contains("MCP自检之书"), "");
            Assert("chapters_list 列出两章", Text(lines[4]).Contains("入山") && Text(lines[4]).Contains("得剑"), "");
            Assert("chapter_read 返回正文", Text(lines[5]).Contains("霜降"), "");
            Assert("settings_get 含叙事视角", Text(lines[6]).Contains("第三人称限知"), "");

            var book = Text(lines[7]);
            Assert("设定集 12 章且含伏笔章", book.Contains("foreshadow") && book.Contains("12 章"), "");

            Assert("chapter_search 命中", Text(lines[8]).Contains("命中") && Text(lines[8]).Contains("第1章"), Cut(Text(lines[8]), 40));
            Assert("角色出场统计识别出林寒", Text(lines[9]).Contains("林寒"), "");

            Assert("chapter_write 追加成功", Text(lines[10]).Contains("追加"), "");
            Assert("追加内容确实进了正文", Text(lines[11]).Contains("他没松手"), "");

            Assert("settings_set 写回成功", Text(lines[12]).Contains("writing_style"), "");
            Assert("导出 txt 文件已生成", File.Exists(exportPath), exportPath);

            Assert("未知工具 → isError", IsError(lines[14]), Cut(Text(lines[14]), 40));
            Assert("未知方法 → JSON-RPC error -32601",
                lines[15].RootElement.TryGetProperty("error", out var e) && e.GetProperty("code").GetInt32() == -32601, "");

            // ---- 落盘复核：改动真的写进了 .tdxproj ----
            var reloaded = NovelProject.Load(projPath);
            Assert("改动随 .tdxproj 落盘（文风）", reloaded.WritingStyle == "冷峻白描，短句为主", reloaded.WritingStyle);
            Assert("改动随 .tdxproj 落盘（章节正文）",
                reloaded.Chapters.First(c => c.ChapterNumber == 2).Content.Contains("他没松手"), "");

            // ---- 冲突防护：磁盘上的项目被别人改了，必须拒绝写 ----
            // 只 open 不写（避免污染正文，后面要断言原稿还在）。Session 是进程内单例，
            // 第二次 CallAsync 复用它——正是「打开之后被别人改了」的场景。
            await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "project_open", arguments = new { path = projPath } }),
            });
            File.WriteAllText(projPath, File.ReadAllText(projPath) + " ");   // 模拟编辑器界面存了一版
            var after = await CallAsync(new List<string>
            {
                Req(2, "tools/call", new { name = "chapter_write", arguments = new { number = 1, content = "再写", mode = "replace" } }),
            });
            Assert("磁盘被改后拒绝写入（防覆盖用户稿子）",
                after.Count > 0 && IsError(after[0]) && Text(after[0]).Contains("已被其他程序修改"),
                after.Count > 0 ? Cut(Text(after[0]), 60) : "(无响应)");
            Assert("拒绝写入时原正文未被破坏",
                NovelProject.Load(projPath).Chapters.First(c => c.ChapterNumber == 1).Content.Contains("霜降"), "");

            // ---- 回归：打开副本后写入必须落在副本 ----
            // 项目文件自身也会序列化 FilePath（保存时的路径）。文件被复制后里面存的是旧路径，
            // 而 Save() 照 FilePath 写 —— 不覆盖就会静默写到原文件上（实测踩到过，差点覆盖用户稿子）。
            var copyPath = Path.Combine(dir, "副本.tdxproj");
            File.Copy(projPath, copyPath, true);

            var probe = NovelProject.Load(copyPath);
            Assert("Load 后 FilePath = 实际打开的路径（不信文件里存的旧路径）",
                probe.FilePath == copyPath, probe.FilePath);

            await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "project_open", arguments = new { path = copyPath } }),
                Req(2, "tools/call", new { name = "chapter_write", arguments = new { number = 1, content = "只写副本", mode = "replace" } }),
            });

            var origAfter = NovelProject.Load(projPath);
            var copyAfter = NovelProject.Load(copyPath);
            Assert("打开副本后写入落在副本上",
                copyAfter.Chapters.First(c => c.ChapterNumber == 1).Content.Contains("只写副本"), "");
            Assert("打开副本不会污染原文件",
                origAfter.Chapters.First(c => c.ChapterNumber == 1).Content.Contains("霜降"), "");
        // ==================================================================
        // H. 资源（resources/*）+ 建项目 + AI 配置自检
        // ==================================================================
        {
            var createDir = Path.Combine(dir, "新书目录");
            Directory.CreateDirectory(createDir);

            var hLines = await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "project_open", arguments = new { path = projPath } }),
                Req(2, "resources/list", new { }),
                Req(3, "resources/read", new { uri = "tdx://chapter/1" }),
                Req(4, "resources/read", new { uri = "tdx://settings/full_outline" }),
                Req(5, "resources/read", new { uri = "http://别的地方/x" }),          // 不认识的 URI
                Req(6, "tools/call", new { name = "project_create", arguments = new { name = "Agent新书", directory = createDir, description = "由 MCP 自检创建" } }),
                Req(7, "tools/call", new { name = "ai_config_check", arguments = new { probe = false } }),
            });

            // ---- resources/list ----
            var list = hLines[1].RootElement.GetProperty("result").GetProperty("resources");
            var uris = list.EnumerateArray()
                           .Select(x => x.GetProperty("uri").GetString() ?? "").ToList();
            Assert("H1 resources/list 列出章节资源", uris.Contains("tdx://chapter/1"), string.Join(",", uris));
            Assert("H1 resources/list 列出设定资源",
                uris.Contains("tdx://settings/full_outline"), "");
            Assert("H1 resources/list 含设定集章", uris.Any(u => u.StartsWith("tdx://settings-book/")), "");

            // ---- resources/read ----
            var c1 = hLines[2].RootElement.GetProperty("result").GetProperty("contents")[0]
                             .GetProperty("text").GetString() ?? "";
            Assert("H2 resources/read 章节正文", c1.Contains("霜降"), Cut(c1, 40));

            var outline = hLines[3].RootElement.GetProperty("result").GetProperty("contents")[0]
                                  .GetProperty("text").GetString() ?? "";
            Assert("H2 resources/read 设定内容", outline.Contains("林寒入山"), Cut(outline, 40));

            Assert("H2 不认识的 URI → -32602",
                hLines[4].RootElement.TryGetProperty("error", out var re) &&
                re.GetProperty("code").GetInt32() == -32602, "");

            // ---- project_create ----
            var created = Text(hLines[5]);
            Assert("H3 project_create 成功", created.Contains("已创建并打开"), Cut(created, 60));
            var newFile = Path.Combine(createDir, "Agent新书.tdxproj");
            Assert("H3 项目文件真的落盘", File.Exists(newFile), newFile);
            if (File.Exists(newFile))
            {
                var np = NovelProject.Load(newFile);
                Assert("H3 新项目自带设定集 12 章",
                    np.SettingsBook?.Chapters.Count == 12, (np.SettingsBook?.Chapters.Count ?? -1).ToString());
                Assert("H3 新项目 FilePath 指向新文件", np.FilePath == newFile, np.FilePath);
            }

            // ---- ai_config_check：必须能诊断，且绝不能把 Key 吐出来 ----
            var chk = Text(hLines[6]);
            Assert("H4 ai_config_check 有输出", chk.Contains("配置文件"), Cut(chk, 60));
            var key = TryReadActiveKey();
            if (!string.IsNullOrWhiteSpace(key))
                Assert("H4 ai_config_check 不泄露 Key", !chk.Contains(key), "");
        }

        // ==================================================================
        // I. 工具清单应当包含新增的两个工具
        // ==================================================================
        {
            var iLines = await CallAsync(new List<string> { Req(1, "tools/list", new { }) });
            var names = iLines[0].RootElement.GetProperty("result").GetProperty("tools")
                               .EnumerateArray()
                               .Select(x => x.GetProperty("name").GetString() ?? "").ToList();
            Assert("I1 工具含 ai_config_check", names.Contains("ai_config_check"), "");
            Assert("I2 工具含 project_create", names.Contains("project_create"), "");
            Assert("I3 工具数为 16", names.Count == 16, names.Count.ToString());
        }
        }

        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"自检结果：{_pass} 通过 / {_fail} 失败");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 辅助 ----

    /// <summary>跑一次会话。Session 是进程内单例，跨循环保留——正好用来模拟「打开后又被改」。</summary>
    private static async Task<List<JsonDocument>> CallAsync(List<string> requests)
    {
        var output = new StringWriter();
        await McpServer.RunAsync(new StringReader(string.Join("\n", requests) + "\n"), output);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => JsonDocument.Parse(l)).ToList();
    }

    private static string Req(int id, string method, object parameters) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
        });

    private static string Notify(string method) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["method"] = method,
        });

    private static string Text(JsonDocument d) =>
        d.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    private static bool IsError(JsonDocument d) =>
        d.RootElement.GetProperty("result").TryGetProperty("isError", out var f) && f.GetBoolean();

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>
    /// 读当前启用配置的 Key —— **唯一用途**是断言「自检输出里不含它」，防止以后有人在
    /// ai_config_check 里多打一行把密钥漏给 agent。读完即判，绝不打印。
    /// </summary>
    private static string TryReadActiveKey()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TdxClaw", "api_profiles.json");
            var mgr = new ApiProfileManager(path);
            mgr.Load();
            return mgr.ActiveProfile?.ApiKey ?? "";
        }
        catch { return ""; }
    }

    private static void Assert(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.Error.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.Error.WriteLine($"  ✗ {name}{(string.IsNullOrEmpty(detail) ? "" : "  → " + detail)}"); }
    }
}
