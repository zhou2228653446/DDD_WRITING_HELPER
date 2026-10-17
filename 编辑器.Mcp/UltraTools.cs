using System.Text;
using System.Text.Json;

namespace 编辑器.Mcp
{
    /// <summary>
    /// 超长篇结构相关的 MCP 工具：分卷、角色分级、主角状态面板。
    ///
    /// ★ 这些工具**只对启用了长篇结构的项目有效**。未启用时返回的是
    ///   "先调 ultra_config action=enable"，而不是"工具不存在"——
    ///   后者会让 agent 以为客户端版本旧，去重装一遍。
    /// </summary>
    internal static class UltraTools
    {
        /// <summary>
        /// 取长篇结构。未启用时返回一个 IsError 的结果（成功时是 default，IsError=false）。
        /// 不用 nullable —— ToolResult 是 struct，Nullable&lt;T&gt; 在调用处会更啰嗦。
        /// </summary>
        private static ToolResult RequireUltra(NovelProject p, out UltraLongConfig cfg)
        {
            if (p.UltraLong is { } c)
            {
                cfg = c;
                return default;
            }

            cfg = null!;
            return ToolResult.Fail(
                "这个项目还没有启用长篇结构（分卷 / 角色分级 / 状态面板）。" +
                "先调 ultra_config，action=enable、confirm=\"enable\"；论文/公文项目通常不需要开。");
        }

        // ══════════════════════════════════════════════════════════════════
        // ultra_config
        // ══════════════════════════════════════════════════════════════════

        public static ToolResult Config(Session s, NovelProject p, JsonElement args)
        {
            var action = NovelTools.Str(args, "action", "get").Trim().ToLowerInvariant();

            switch (action)
            {
                case "get":
                    return ToolResult.Ok(DescribeConfig(p));

                case "enable":
                {
                    var confirm = NovelTools.Str(args, "confirm").Trim();
                    if (!string.Equals(confirm, "enable", StringComparison.OrdinalIgnoreCase))
                        return ToolResult.Fail("启用会改变上下文组装方式（角色按章筛选、前情按卷摘要）。确认请传 confirm=\"enable\"。");

                    if (p.UltraLong != null)
                        return ToolResult.Ok("这个项目已经启用了长篇结构。\n\n" + DescribeConfig(p));

                    if (s.IsStale(out var stale)) return stale;

                    // 结构变更前留一版：启用后旧快照里没有卷信息，万一要退回去还能捞
                    s.Snapshot("启用长篇结构前", lightweight: true);
                    var cfg = p.EnableUltraLong();

                    if (NovelTools.Bool(args, "autoVolumes", false))
                    {
                        var per = Math.Clamp(NovelTools.Int(args, "chaptersPerVolume", 50), 1, 500);
                        SplitByChapters(p, cfg, per);
                    }

                    s.Save();
                    return ToolResult.Ok("已启用长篇结构。\n\n" + DescribeConfig(p));
                }

                case "update":
                {
                    var r = RequireUltra(p, out var cfg);
                    if (r.IsError) return r;
                    if (s.IsStale(out var stale)) return stale;

                    var volumes = NovelTools.Bool(args, "enableVolumes", cfg.EnableVolumes);
                    var lifecycle = NovelTools.Bool(args, "enableCharacterLifecycle", cfg.EnableCharacterLifecycle);
                    var state = NovelTools.Bool(args, "enableStateBoard", cfg.EnableStateBoard);
                    var ratio = args.TryGetProperty("dynamicBudgetRatio", out var rr) && rr.ValueKind == JsonValueKind.Number
                        ? rr.GetDouble()
                        : cfg.DynamicBudgetRatio;

                    cfg.EnableVolumes = volumes;
                    cfg.EnableCharacterLifecycle = lifecycle;
                    cfg.EnableStateBoard = state;
                    cfg.DynamicBudgetRatio = Math.Clamp(ratio, 0.08, 0.20);

                    s.Save();
                    return ToolResult.Ok("已更新。\n\n" + DescribeConfig(p));
                }

                default:
                    return ToolResult.Fail("action 只能是 get / enable / update。");
            }
        }

        private static string DescribeConfig(NovelProject p)
        {
            if (p.UltraLong is not { } cfg)
                return "长篇结构：未启用（论文 / 公文 / 普通小说项目的常态）。\n" +
                       "启用后才会出现分卷、角色分级、主角状态面板这些能力。";

            var sb = new StringBuilder();
            sb.AppendLine("长篇结构：已启用");
            sb.AppendLine($"· 分卷：{(cfg.EnableVolumes ? "开" : "关")}（{cfg.Volumes.Count} 卷，{p.ChaptersOfVolume(null).Count} 章未分卷）");
            sb.AppendLine($"· 角色分级：{(cfg.EnableCharacterLifecycle ? "开" : "关")}" +
                          $"（Core {p.Characters.Count(c => c.Scope == CharacterScope.Core)} / " +
                          $"Arc {p.Characters.Count(c => c.Scope == CharacterScope.Arc)} / " +
                          $"Guest {p.Characters.Count(c => c.Scope == CharacterScope.Guest)}）");
            sb.AppendLine($"· 状态面板：{(cfg.EnableStateBoard ? "开" : "关")}");
            sb.AppendLine($"· 动态预算：{cfg.EffectiveBudgetRatio:P0}（窗口的 8%~20%）");
            sb.AppendLine($"· 视角主角：{cfg.ProtagonistIds.Count} 人");

            var protagonists = p.Characters
                .Where(c => cfg.ProtagonistIds.Contains(c.CharacterId) || (c.Role ?? "").Contains("主角"))
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (protagonists.Count > 0)
                sb.AppendLine($"  当前识别到：{string.Join("、", protagonists)}");

            return sb.ToString().TrimEnd();
        }

        private static void SplitByChapters(NovelProject p, UltraLongConfig cfg, int perVolume)
        {
            cfg.Volumes.Clear();
            var ordered = p.Chapters.OrderBy(c => c.ChapterNumber).ToList();
            for (int i = 0; i < ordered.Count; i += perVolume)
            {
                var number = cfg.Volumes.Count + 1;
                var v = new Volume
                {
                    Number = number,
                    Title = $"第{number}卷",
                };
                cfg.Volumes.Add(v);

                foreach (var c in ordered.Skip(i).Take(perVolume))
                    c.VolumeId = v.VolumeId;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // volume_list
        // ══════════════════════════════════════════════════════════════════

        public static ToolResult ListVolumes(NovelProject p)
        {
            var r = RequireUltra(p, out var cfg);
            if (r.IsError) return r;

            if (cfg.Volumes.Count == 0)
            {
                var loose0 = p.ChaptersOfVolume(null);
                return ToolResult.Ok(
                    $"还没有分卷。全书 {p.Chapters.Count} 章" +
                    (loose0.Count > 0 ? $"（{loose0.Count} 章未分卷）" : "") +
                    "。用 volume_upsert 建卷，再用 chapter_volume_set 把章节挂进去。");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"共 {cfg.Volumes.Count} 卷：");

            foreach (var v in cfg.Volumes.OrderBy(v => v.Number))
            {
                var chapters = p.ChaptersOfVolume(v.VolumeId);
                var words = chapters.Sum(c => c.WordCount);
                var range = chapters.Count == 0
                    ? "（空卷）"
                    : $"第{chapters.First().ChapterNumber}–{chapters.Last().ChapterNumber}章";

                sb.AppendLine($"· 第{v.Number}卷《{v.Title}》 {range}，{chapters.Count} 章 {words} 字");
                if (!string.IsNullOrWhiteSpace(v.Goal)) sb.AppendLine($"  目标：{v.Goal.Trim()}");
                if (!string.IsNullOrWhiteSpace(v.Summary)) sb.AppendLine($"  回顾：{Shorten(v.Summary.Trim(), 60)}");
            }

            var loose = p.ChaptersOfVolume(null);
            if (loose.Count > 0)
                sb.AppendLine($"未分卷：{loose.Count} 章（第{loose.First().ChapterNumber}–{loose.Last().ChapterNumber}章）");

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        // ══════════════════════════════════════════════════════════════════
        // volume_upsert / volume_delete
        // ══════════════════════════════════════════════════════════════════

        public static ToolResult UpsertVolume(Session s, NovelProject p, JsonElement args)
        {
            var r = RequireUltra(p, out var cfg);
            if (r.IsError) return r;

            var id = NovelTools.Str(args, "volumeId").Trim();

            if (id.Length > 0)
            {
                var v = cfg.Volumes.FirstOrDefault(x => x.VolumeId == id);
                if (v == null) return ToolResult.Fail($"没有 volumeId 为「{id}」的卷。用 volume_list 看现有卷。");
                if (s.IsStale(out var stale)) return stale;

                var number = NovelTools.Int(args, "number", 0);
                var title = NovelTools.Str(args, "title");
                var goal = NovelTools.Str(args, "goal");
                var summary = NovelTools.StrAny(args, "", "summary", "content");

                if (number > 0) v.Number = number;
                if (!string.IsNullOrWhiteSpace(title)) v.Title = title.Trim();
                if (args.TryGetProperty("goal", out _)) v.Goal = goal.Trim();
                if (args.TryGetProperty("summary", out _) || args.TryGetProperty("content", out _))
                    v.Summary = summary.Trim();

                s.Save();
                return ToolResult.Ok($"已更新第{v.Number}卷《{v.Title}》。");
            }

            // 新建
            var newNumber = NovelTools.Int(args, "number", 0);
            if (newNumber <= 0) newNumber = p.NextVolumeNumber();

            var newTitle = NovelTools.Str(args, "title").Trim();
            if (string.IsNullOrWhiteSpace(newTitle)) newTitle = $"第{newNumber}卷";

            if (s.IsStale(out var stale2)) return stale2;

            var nv = new Volume
            {
                Number = newNumber,
                Title = newTitle,
                Goal = NovelTools.Str(args, "goal").Trim(),
                Summary = NovelTools.StrAny(args, "", "summary", "content").Trim(),
            };
            cfg.Volumes.Add(nv);
            s.Save();

            return ToolResult.Ok($"已新建第{nv.Number}卷《{nv.Title}》。用 chapter_volume_set 把章节挂进来。");
        }

        public static ToolResult DeleteVolume(Session s, NovelProject p, JsonElement args)
        {
            var r = RequireUltra(p, out var cfg);
            if (r.IsError) return r;

            var id = NovelTools.Str(args, "volumeId").Trim();
            var number = NovelTools.Int(args, "number", 0);
            var confirm = NovelTools.Str(args, "confirm").Trim();

            var v = id.Length > 0
                ? cfg.Volumes.FirstOrDefault(x => x.VolumeId == id)
                : cfg.Volumes.FirstOrDefault(x => x.Number == number);

            if (v == null) return ToolResult.Fail("找不到这个卷。用 volume_list 看现有卷（可以传 volumeId 或 number）。");

            if (!string.Equals(confirm, v.Title, StringComparison.Ordinal))
                return ToolResult.Fail($"确要删第{v.Number}卷《{v.Title}》？把 confirm 传成卷名「{v.Title}」才会删。");

            if (s.IsStale(out var stale)) return stale;

            var affected = p.ChaptersOfVolume(v.VolumeId);
            s.Snapshot($"MCP 删除第{v.Number}卷前", lightweight: true);

            foreach (var c in affected) c.VolumeId = null;
            cfg.Volumes.Remove(v);
            s.Save();

            return ToolResult.Ok($"已删除第{v.Number}卷《{v.Title}》。" +
                (affected.Count > 0
                    ? $"其中 {affected.Count} 章已转为「未分卷」，正文一字未动。"
                    : "该卷原本没有章节。"));
        }

        // ══════════════════════════════════════════════════════════════════
        // chapter_volume_set
        // ══════════════════════════════════════════════════════════════════

        public static ToolResult SetChapterVolume(Session s, NovelProject p, JsonElement args)
        {
            var r = RequireUltra(p, out var cfg);
            if (r.IsError) return r;

            var numbers = ParseNumbers(args, "numbers", "number");
            if (numbers.Count == 0)
                return ToolResult.Fail("要指定章节号：numbers=\"1-5,7\" 或 number=3。");

            var volumeId = NovelTools.Str(args, "volumeId").Trim();
            Volume? target = null;
            if (volumeId.Length > 0)
            {
                target = cfg.Volumes.FirstOrDefault(v => v.VolumeId == volumeId);
                if (target == null)
                {
                    var byNumber = int.TryParse(volumeId, out var vn) ? vn : -1;
                    target = cfg.Volumes.FirstOrDefault(v => v.Number == byNumber);
                }
                if (target == null) return ToolResult.Fail($"没有这个卷（volumeId={volumeId}）。用 volume_list 看现有卷。");
            }

            var changed = 0;
            var missing = new List<int>();

            if (s.IsStale(out var stale)) return stale;
            s.Snapshot("MCP 调整章节归属卷前", lightweight: true);

            foreach (var n in numbers)
            {
                var c = p.Chapters.FirstOrDefault(x => x.ChapterNumber == n);
                if (c == null) { missing.Add(n); continue; }
                c.VolumeId = target?.VolumeId;
                changed++;
            }

            s.Save();

            var sb = new StringBuilder();
            sb.Append(target == null
                ? $"已把 {changed} 章改为「未分卷」。"
                : $"已把 {changed} 章挂到第{target.Number}卷《{target.Title}》。");
            if (missing.Count > 0)
                sb.Append($" 找不到：第 {string.Join("、", missing)} 章。");

            return ToolResult.Ok(sb.ToString());
        }

        // ══════════════════════════════════════════════════════════════════
        // state_board_get / state_board_update
        // ══════════════════════════════════════════════════════════════════

        public static ToolResult GetStateBoard(NovelProject p)
        {
            var r = RequireUltra(p, out var cfg);
            if (r.IsError) return r;

            var list = ResolveProtagonists(p, cfg);
            if (list.Count == 0)
                return ToolResult.Ok("还没有识别到主角。用 character_upsert 建卡（Role 里带「主角」），" +
                                     "或把 CharacterId 写进 ProtagonistIds。");

            var sb = new StringBuilder();
            foreach (var c in list)
            {
                sb.AppendLine($"· {c.Name}（{c.CharacterId}）");
                sb.AppendLine($"  境界/阶段：{Show(c.CurrentRealm)}");
                sb.AppendLine($"  当前处境：{Show(c.CurrentSituation)}");
                sb.AppendLine($"  当前目标：{Show(c.CurrentGoal)}");
                sb.AppendLine($"  关键道具：{(c.KeyItems is { Count: > 0 } ? string.Join("、", c.KeyItems) : "（未记录）")}");
                sb.AppendLine($"  状态：{StatusText(c.Status)}");
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        public static ToolResult UpdateStateBoard(Session s, NovelProject p, JsonElement args)
        {
            var r = RequireUltra(p, out var cfg);
            if (r.IsError) return r;

            var id = NovelTools.Str(args, "characterId").Trim();
            var name = NovelTools.Str(args, "name").Trim();

            var c = id.Length > 0
                ? p.Characters.FirstOrDefault(x => x.CharacterId == id)
                : p.Characters.FirstOrDefault(x => x.Name == name);

            if (c == null) return ToolResult.Fail("找不到这个角色。用 characters_list 看现有角色（传 characterId 或 name）。");

            var realm = NovelTools.StrAny(args, "", "realm", "currentRealm");
            var situation = NovelTools.StrAny(args, "", "situation", "currentSituation");
            var goal = NovelTools.StrAny(args, "", "goal", "currentGoal");

            if (s.IsStale(out var stale)) return stale;
            s.Snapshot($"MCP 更新{c.Name}状态前", lightweight: true);

            if (args.TryGetProperty("realm", out _) || args.TryGetProperty("currentRealm", out _))
                c.CurrentRealm = realm.Trim();
            if (args.TryGetProperty("situation", out _) || args.TryGetProperty("currentSituation", out _))
                c.CurrentSituation = situation.Trim();
            if (args.TryGetProperty("goal", out _) || args.TryGetProperty("currentGoal", out _))
                c.CurrentGoal = goal.Trim();

            if (args.TryGetProperty("items", out var itemsEl) || args.TryGetProperty("keyItems", out itemsEl))
                c.KeyItems = ReadStringList(itemsEl);

            if (args.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            {
                var parsed = ParseStatus(statusEl.GetString() ?? "");
                if (parsed != null) c.Status = parsed.Value;
            }

            s.Save();

            return ToolResult.Ok(
                $"已更新{c.Name}的状态：\n" +
                $"· 境界/阶段：{Show(c.CurrentRealm)}\n" +
                $"· 当前处境：{Show(c.CurrentSituation)}\n" +
                $"· 当前目标：{Show(c.CurrentGoal)}\n" +
                $"· 关键道具：{(c.KeyItems is { Count: > 0 } ? string.Join("、", c.KeyItems) : "（无）")}");
        }

        // ══════════════════════════════════════════════════════════════════
        // character_scope_set
        // ══════════════════════════════════════════════════════════════════

        public static ToolResult SetCharacterScope(Session s, NovelProject p, JsonElement args)
        {
            var r = RequireUltra(p, out _);
            if (r.IsError) return r;

            var names = new List<string>();
            if (args.TryGetProperty("names", out var namesEl))
                names.AddRange(ReadStringList(namesEl) ?? new List<string>());
            var single = NovelTools.Str(args, "name").Trim();
            if (single.Length > 0) names.Add(single);

            var ids = ReadStringList(args.TryGetProperty("ids", out var idsEl) ? idsEl : default) ?? new List<string>();
            var singleId = NovelTools.Str(args, "characterId").Trim();
            if (singleId.Length > 0) ids.Add(singleId);

            if (names.Count == 0 && ids.Count == 0)
                return ToolResult.Fail("要指定角色：names=[\"林寒\",\"苏晚\"] 或 ids=[\"...\"]，也可以单个 name / characterId。");

            var scopeText = NovelTools.Str(args, "scope").Trim();
            var statusText = NovelTools.Str(args, "status").Trim();

            var scope = ParseScope(scopeText);
            var status = ParseStatus(statusText);

            if (scope == null && status == null)
                return ToolResult.Fail("scope 可选 core / arc / guest；status 可选 active / inactive / retired / deceased。至少给一个。");

            var targets = p.Characters.Where(c =>
                ids.Contains(c.CharacterId) || names.Contains(c.Name)).ToList();

            if (targets.Count == 0) return ToolResult.Fail("没找到这些角色。用 characters_list 看现有角色名。");

            if (s.IsStale(out var stale)) return stale;
            s.Snapshot("MCP 调整角色分级前", lightweight: true);

            foreach (var c in targets)
            {
                if (scope != null) c.Scope = scope.Value;
                if (status != null) c.Status = status.Value;
            }
            s.Save();

            return ToolResult.Ok($"已更新 {targets.Count} 个角色：" +
                string.Join("、", targets.Select(c => $"{c.Name}（{ScopeText(c.Scope)}/{StatusText(c.Status)}）")));
        }

        // ══════════════════════════════════════════════════════════════════
        // 辅助
        // ══════════════════════════════════════════════════════════════════

        private static List<Character> ResolveProtagonists(NovelProject p, UltraLongConfig cfg)
        {
            var list = p.Characters.Where(c => cfg.ProtagonistIds.Contains(c.CharacterId)).ToList();
            if (list.Count == 0)
                list = p.Characters.Where(c => (c.Role ?? "").Contains("主角")).ToList();
            return list;
        }

        private static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "（未记录）" : s.Trim();

        private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private static string ScopeText(CharacterScope s) => s switch
        {
            CharacterScope.Core => "核心常驻",
            CharacterScope.Arc => "本卷关键",
            _ => "单章客串",
        };

        private static string StatusText(CharacterStatus s) => s switch
        {
            CharacterStatus.Active => "活跃",
            CharacterStatus.Inactive => "隐退",
            CharacterStatus.Retired => "退场",
            _ => "阵亡",
        };

        private static CharacterScope? ParseScope(string text) => text.Trim().ToLowerInvariant() switch
        {
            "core" or "核心" or "常驻" => CharacterScope.Core,
            "arc" or "本卷" or "分卷" => CharacterScope.Arc,
            "guest" or "客串" or "龙套" => CharacterScope.Guest,
            _ => null,
        };

        private static CharacterStatus? ParseStatus(string text) => text.Trim().ToLowerInvariant() switch
        {
            "active" or "活跃" or "存活" => CharacterStatus.Active,
            "inactive" or "隐退" or "幕后" => CharacterStatus.Inactive,
            "retired" or "退场" or "离队" => CharacterStatus.Retired,
            "deceased" or "dead" or "阵亡" or "死亡" => CharacterStatus.Deceased,
            _ => null,
        };

        /// <summary>解析 "1-5,7,9-10" 或 [1,2,3] 或单个 number。</summary>
        private static List<int> ParseNumbers(JsonElement args, string arrayKey, string singleKey)
        {
            var result = new List<int>();

            if (args.TryGetProperty(arrayKey, out var el))
            {
                if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                            result.Add(n);
                }
                else if (el.ValueKind == JsonValueKind.String)
                {
                    foreach (var part in (el.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = part.Trim();
                        var dash = p.IndexOf('-');
                        if (dash > 0
                            && int.TryParse(p[..dash], out var from)
                            && int.TryParse(p[(dash + 1)..], out var to)
                            && to >= from && to - from <= 500)
                        {
                            for (int i = from; i <= to; i++) result.Add(i);
                        }
                        else if (int.TryParse(p, out var one))
                        {
                            result.Add(one);
                        }
                    }
                }
            }

            var single = NovelTools.Int(args, singleKey, -1);
            if (single > 0) result.Add(single);

            return result.Distinct().OrderBy(x => x).ToList();
        }

        private static List<string>? ReadStringList(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
                return el.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "")
                    .Where(s => s.Length > 0)
                    .ToList();

            if (el.ValueKind == JsonValueKind.String)
                return (el.GetString() ?? "")
                    .Split(new[] { ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

            return null;
        }
    }
}
