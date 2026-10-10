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
    private static int _pass, _fail, _skip;

    public static async Task<int> RunAsync()
    {
        McpLiveBridge.Suppressed = true;
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
            Assert("initialize 返回 instructions（供 Antigravity 生成 instructions.md）",
                init.GetProperty("result").TryGetProperty("instructions", out var inst) && (inst.GetString()?.Length ?? 0) > 50, "");

            var rawToolsLine = lines[1].RootElement.GetRawText();
            Assert("tools/list 不含非法的 \"required\":null（严格 JSON Schema 兼容）",
                !rawToolsLine.Contains("\"required\":null"), "");

            var tools = lines[1].RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                              .Select(t => t.GetProperty("name").GetString()!).ToList();
            Assert("tools/list 列出 28 个工具", tools.Count == 28, string.Join(",", tools));
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

            // ---- 回归：改稿前必须留快照 ----
            // MCP 是 agent **自动**改稿的场景，一次覆盖错章就是几千字没了，比界面里手点
            // 更容易出事。界面每次 AI 操作前都 TakeSnapshot，MCP 也必须存，否则没有后悔药。
            // ★ 快照按**书名**分子目录（产品有意如此：网页版所有书稿同处一个 books/ 目录，
            //   平铺会让 A 书的快照出现在 B 书的「恢复」列表里）。这里递归找，既不去猜
            //   具体层级，也能逮到「快照写到别处去了」这种真问题。
            var snapRoot = Path.Combine(Path.GetDirectoryName(projPath)!, ".snapshots");
            var snapFiles = Directory.Exists(snapRoot)
                ? Directory.GetFiles(snapRoot, "*.json", SearchOption.AllDirectories)
                           .Where(f => !string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase))
                           .ToList()
                : new List<string>();
            Assert("改稿前存了快照（agent 改坏能回滚）", snapFiles.Count > 0,
                Directory.Exists(snapRoot)
                    ? string.Join(",", snapFiles.Select(f => Path.GetRelativePath(snapRoot, f)))
                    : "(.snapshots 目录不存在)");
            // ⚠ 不能直接搜文件文本：System.Text.Json 默认把非 ASCII 转成 \uXXXX，
            // 快照里的「霜降」实际写作 \u971C\u964D，文本 Contains 必然落空。要反序列化再看。
            Assert("快照记的是改写之前的内容（不是改完的状态）",
                snapFiles.Count > 0 && snapFiles.Any(f =>
                {
                    try
                    {
                        var snap = JsonSerializer.Deserialize<NovelProject>(
                            File.ReadAllText(f),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return snap?.Chapters.Any(c => c.Content.Contains("霜降")) == true;
                    }
                    catch { return false; }
                }),
                snapFiles.Count == 0 ? "(没有快照文件)" : "快照里找不到改写前的原稿内容");
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
            else
                Skip("H4 ai_config_check 不泄露 Key", "当前未配置 API Key，无从比对；配好后再跑一次");
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
            Assert("I3 工具数为 28", names.Count == 28, names.Count.ToString());
            Assert("I4 章节结构管理齐备（改名/删除/重排）",
                names.Contains("chapter_rename") && names.Contains("chapter_delete")
                && names.Contains("chapter_reorder"), "");
            Assert("I5 快照可回滚（list/restore）",
                names.Contains("snapshot_list") && names.Contains("snapshot_restore"), "");
            Assert("I6 协作类工具齐备（进度画像 / 梗概 / 人物卡 / 记忆）",
                names.Contains("project_status") && names.Contains("chapter_summary_set")
                && names.Contains("character_upsert") && names.Contains("memory_set"), "");
        }

        // ==================================================================
        // J. agent 自主操作所需的能力：章节管理 / 梗概 / 人物卡 / 快照回滚
        //
        // 场景是"用户在 agent 里用自然语言指挥，具体操作全由 agent 完成"——
        // 所以凡是被改坏、被删掉的东西，agent 都得能自己收拾，不能让人回界面点。
        // ==================================================================
        {
            var jLines = await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "project_open", arguments = new { path = projPath } }),
                Req(2, "tools/call", new { name = "project_status", arguments = new { } }),
                Req(3, "tools/call", new { name = "chapter_rename", arguments = new { number = 2, title = "改名后的第二章" } }),
                Req(4, "tools/call", new { name = "chapter_summary_set", arguments = new { number = 1, summary = "林寒初入山门。" } }),
                Req(5, "tools/call", new { name = "characters_list", arguments = new { } }),
                Req(6, "tools/call", new { name = "character_upsert", arguments = new { name = "林寒", role = "主角", age = 19 } }),
                Req(7, "tools/call", new { name = "character_upsert", arguments = new { name = "林寒", personality = "话少，认死理" } }),
                Req(8, "tools/call", new { name = "characters_list", arguments = new { } }),
            });

            Assert("J1 project_status 给出进度与下一步建议",
                Text(jLines[1]).Contains("下一步建议"), Cut(Text(jLines[1]), 60));
            Assert("J2 chapter_rename 改名成功",
                Text(jLines[2]).Contains("改名后的第二章"), Cut(Text(jLines[2]), 50));
            Assert("J3 chapter_summary_set 写入梗概",
                Text(jLines[3]).Contains("梗概"), Cut(Text(jLines[3]), 50));
            Assert("J4 无人物卡时给出可操作提示",
                Text(jLines[4]).Contains("还没有结构化人物卡"), Cut(Text(jLines[4]), 50));
            Assert("J5 character_upsert 新建人物卡",
                Text(jLines[5]).Contains("新建"), Cut(Text(jLines[5]), 50));

            var afterUpsert = Text(jLines[7]);
            Assert("J6 更新只改传入字段，不覆盖未传的（角色/年龄还在）",
                afterUpsert.Contains("话少，认死理") && afterUpsert.Contains("主角") && afterUpsert.Contains("19"),
                Cut(afterUpsert, 60));

            // ---- 删章 → 用快照自己捞回来 ----
            var kLines = await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "chapter_reorder", arguments = new { number = 2, toNumber = 1 } }),
                Req(2, "tools/call", new { name = "chapter_delete", arguments = new { number = 2 } }),
            });
            Assert("J7 chapter_reorder 移动章节",
                Text(kLines[0]).Contains("移到"), Cut(Text(kLines[0]), 50));
            Assert("J8 chapter_delete 删除并重排",
                Text(kLines[1]).Contains("已删除"), Cut(Text(kLines[1]), 50));
            Assert("J9 删除后只剩一章",
                NovelProject.Load(projPath).Chapters.Count == 1,
                NovelProject.Load(projPath).Chapters.Count.ToString());

            var snapEntries = new ProjectSnapshotManager(projPath).LoadIndex();
            var lastId = snapEntries.Count == 0 ? "" : snapEntries[^1].Id;
            var rLines = await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "project_open", arguments = new { path = projPath } }),
                Req(2, "tools/call", new { name = "snapshot_restore", arguments = new { id = lastId } }),
            });
            Assert("J10 snapshot_restore 恢复成功",
                Text(rLines[1]).Contains("已恢复"), Cut(Text(rLines[1]), 50));
            Assert("J11 被删的章从快照里回来了",
                NovelProject.Load(projPath).Chapters.Count == 2,
                NovelProject.Load(projPath).Chapters.Count.ToString());
        }

        // ==================================================================
        // K. 进度回报：客户端声明了 progressToken，长任务就必须沿途推通知
        // ==================================================================
        {
            // 刻意用非法 task：它在"读配置 / 调模型"之前就返回，
            // 既能验证通知确实发出，又不会真去调一次模型——自检不该花钱、也不该联网。
            var raw = "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{"
                    + "\"name\":\"ai_write\",\"arguments\":{\"task\":\"__不是任务__\"},"
                    + "\"_meta\":{\"progressToken\":\"selftest-1\"}}}";

            var kLines = await CallAsync(new List<string> { raw });
            var progress = kLines.Where(d =>
                d.RootElement.TryGetProperty("method", out var m) &&
                m.GetString() == "notifications/progress").ToList();

            Assert("K1 给了 progressToken 就沿途发进度通知",
                progress.Count > 0, $"收到 {progress.Count} 条");
            Assert("K2 进度通知回传原 token",
                progress.Count > 0 &&
                progress[0].RootElement.GetProperty("params")
                           .GetProperty("progressToken").GetString() == "selftest-1",
                progress.Count == 0
                    ? "(没有通知)"
                    : progress[0].RootElement.GetProperty("params")
                                 .GetProperty("progressToken").ToString());
        }

        // ==================================================================
        // L. 实测改进回归：批量 settings_set / 人物卡字符串年龄与扩展字段 /
        //    project_create 完整路径容错 / 人物卡与前情梗概+上章末尾注入
        // ==================================================================
        {
            var fullPathBook = Path.Combine(dir, "路径直建之书.tdxproj");
            var lLines = await CallAsync(new List<string>
            {
                Req(1, "tools/call", new { name = "project_create", arguments = new { path = fullPathBook, full_outline = "初始大纲A", narrative_viewpoint = "第一人称" } }),
                Req(2, "tools/call", new { name = "settings_set", arguments = new { background = "雾港蒸汽朋克", writing_style = "冷峻克制" } }),
                Req(3, "tools/call", new { name = "character_upsert", arguments = new { name = "沈砚", role = "主角", age = "27岁", abilities = "精密擒纵机构修复", relationships = "苏怀音的搭档", notes = "随身带黄铜目镜" } }),
                Req(4, "tools/call", new { name = "chapter_create", arguments = new { title = "停摆", content = "第一章开头……中间过程……末尾悬念：钟声在第十二响戛然而止。", summary = "沈砚接下市政厅停摆大钟的委托。" } }),
                Req(5, "tools/call", new { name = "chapter_create", arguments = new { title = "齿轮", content = "第二章起点。" } }),
                Req(6, "tools/call", new { name = "settings_get", arguments = new { } }),
            });

            Assert("L1 project_create 支持直接传完整 .tdxproj 路径并顺带填初始设定",
                File.Exists(fullPathBook) && Text(lLines[0]).Contains("路径直建之书"), Cut(Text(lLines[0]), 60));
            Assert("L2 settings_set 支持批量写入多项设定",
                Text(lLines[1]).Contains("批量更新 2 项设定"), Cut(Text(lLines[1]), 60));

            var lp = NovelProject.Load(fullPathBook);
            var shen = lp.Characters.FirstOrDefault(c => c.Name == "沈砚");
            Assert("L3 character_upsert 容忍 \"27岁\" 字符串年龄并保存 abilities/relationships/notes",
                shen != null && shen.Age == 27 && shen.Abilities.Contains("擒纵") && shen.Relationships.Contains("苏怀音") && shen.Notes.Contains("目镜"),
                shen == null ? "(未找到人物卡)" : $"age={shen.Age}, abilities={shen.Abilities}");

            var effectiveChar = lp.BuildEffectiveCharacterSettings();
            Assert("L4 BuildEffectiveCharacterSettings 自动汇总结构化人物卡供 AI 续写使用",
                effectiveChar.Contains("沈砚") && effectiveChar.Contains("27岁") && effectiveChar.Contains("精密擒纵机构修复"),
                Cut(effectiveChar, 60));

            var priorBrief = lp.BuildPriorChapterBrief(2, includePrevTail: true);
            Assert("L5 BuildPriorChapterBrief 同时包含前章 Summary 与紧邻上章末尾原文",
                priorBrief.Contains("沈砚接下市政厅停摆大钟的委托") && priorBrief.Contains("钟声在第十二响戛然而止"),
                Cut(priorBrief, 80));
        }
        }

        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"自检结果：{_pass} 通过 / {_fail} 失败" +
            (_skip > 0 ? $" / {_skip} 跳过（前提不具备，非通过）" : ""));
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
    ///
    /// 必须走 <see cref="NovelTools.ResolveConfigDirectory"/>，不能自己拼 %APPDATA%\TdxClaw：
    /// 那个方法认 paths.json 里的目录重定向，产品读的是重定向后的位置。自己拼的话，
    /// 用户一旦改过配置目录，这里读到的就是另一处，Key 恒为空 → 断言被跳过，
    /// 于是又变成"看着全绿、其实少查一条"。
    /// </summary>
    private static string TryReadActiveKey()
    {
        try
        {
            var path = Path.Combine(NovelTools.ResolveConfigDirectory(), "api_profiles.json");
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

    /// <summary>
    /// 因为环境不具备前提而**没跑**的检查。必须显式打出来 —— 否则「全绿」会被读成
    /// 「全跑过了」，实际少了一条，而恰恰是那条只在配了 Key 的机器上才有意义。
    /// </summary>
    private static void Skip(string name, string reason)
    {
        _skip++;
        Console.Error.WriteLine($"  - {name}  （跳过：{reason}）");
    }
}
