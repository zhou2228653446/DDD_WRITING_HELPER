using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace 编辑器.Services
{
    /// <summary>
    /// 超长篇的上下文组装：把"哪些角色/哪些前情"从"全量塞"改成"按章裁剪"。
    ///
    /// ★★ 这个类里的每一个公开方法，在 <see cref="NovelProject.UltraLong"/> 为 null 时
    ///    都必须**逐字退回原行为**——论文、公文、普通小说项目走的还是老路。
    ///    `PaperBaselineTests` 盯格式，这里由 `ContextAssemblerTests` 盯行为。
    ///
    /// ★ 分工：
    ///   · <see cref="BuildStableCharacters"/> 产出进 **system**（跨章稳定，能缓存）；
    ///   · <see cref="BuildDynamic"/> 产出进 **user**（每章都变，绝不能进缓存前缀）。
    ///   状态面板属于后者——它每章都在变，塞进 system 会把缓存前缀整段打穿。
    /// </summary>
    public static class ContextAssembler
    {
        /// <summary>动态素材的硬上限（token）。窗口再大也不该给前情超过这个量。</summary>
        private const int MaxDynamicBudget = 12_000;

        /// <summary>动态素材的下限。太小会把"近期梗概"挤成一两行，反而不如不加。</summary>
        private const int MinDynamicBudget = 2_000;

        public static bool Enabled(NovelProject? p) => p?.UltraLong != null;

        // ══════════════════════════════════════════════════════════════════
        // 稳定段（system）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 进 system 的角色资料。
        ///
        /// 启用角色分级时只留 **Core 且未死亡** 的（外貌/性格/能力这些静态资料）；
        /// 本章要用的 Arc/Guest 角色在 <see cref="BuildDynamic"/> 里另给一份精简卡。
        ///
        /// 未启用时直接返回 <see cref="NovelProject.BuildEffectiveCharacterSettings"/>，
        /// 一个字节都不改——论文的"研究对象"、公文的"事项要素"都在这个字段里。
        /// </summary>
        public static string BuildStableCharacters(NovelProject? p)
        {
            if (p == null) return "";
            if (p.UltraLong is not { EnableCharacterLifecycle: true })
                return p.BuildEffectiveCharacterSettings();

            var core = p.Characters.Where(c =>
                c.Scope == CharacterScope.Core &&
                c.Status != CharacterStatus.Deceased);

            return p.BuildCharacterSettings(core);
        }

        // ══════════════════════════════════════════════════════════════════
        // 动态段（user）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 本章动态素材：主角状态 / 卷摘要 / 本章登场角色 / 近期梗概 / 上章末尾。
        ///
        /// 未启用、或章号无效时返回空串——调用方拿到空串就当没这段，不要拼空段落。
        /// </summary>
        /// <param name="windowTokens">模型上下文窗口，用来算预算（默认按 200K）。</param>
        public static string BuildDynamic(NovelProject? p, int chapterNumber, int windowTokens = 200_000)
        {
            if (p?.UltraLong is not { } cfg) return "";
            if (chapterNumber <= 0) return "";

            var budget = Math.Clamp(
                (int)(Math.Max(8_000, windowTokens) * cfg.EffectiveBudgetRatio),
                MinDynamicBudget, MaxDynamicBudget);

            var sb = new StringBuilder();

            if (cfg.EnableStateBoard)
                AppendStatePanel(sb, p, cfg);

            if (cfg.EnableVolumes)
                AppendVolumeContext(sb, p, chapterNumber, (int)(budget * 0.25));

            AppendCurrentCharacters(sb, p, chapterNumber, (int)(budget * 0.30));
            AppendRecentBrief(sb, p, chapterNumber, (int)(budget * 0.30));
            AppendPrevTail(sb, p, chapterNumber, (int)(budget * 0.15));

            return sb.ToString().Trim();
        }

        private static void AppendStatePanel(StringBuilder sb, NovelProject p, UltraLongConfig cfg)
        {
            var protagonists = ResolveProtagonists(p, cfg);
            if (protagonists.Count == 0) return;

            var lines = new List<string>();
            foreach (var c in protagonists)
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(c.CurrentRealm)) parts.Add($"境界/阶段：{c.CurrentRealm.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.CurrentSituation)) parts.Add($"当前处境：{c.CurrentSituation.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.CurrentGoal)) parts.Add($"当前目标：{c.CurrentGoal.Trim()}");
                if (c.KeyItems is { Count: > 0 }) parts.Add($"关键道具：{string.Join("、", c.KeyItems)}");
                if (parts.Count == 0) continue;

                lines.Add($"· {c.Name}：{string.Join("；", parts)}");
            }

            if (lines.Count == 0) return;

            sb.AppendLine("【主角当前状态（以这里为准，不得写成更早的境界或已丢掉的道具）】");
            foreach (var line in lines) sb.AppendLine(line);
            sb.AppendLine();
        }

        private static List<Character> ResolveProtagonists(NovelProject p, UltraLongConfig cfg)
        {
            var picked = new List<Character>();

            if (cfg.ProtagonistIds is { Count: > 0 })
            {
                foreach (var id in cfg.ProtagonistIds)
                {
                    var c = p.Characters.FirstOrDefault(x => x.CharacterId == id);
                    if (c != null) picked.Add(c);
                }
            }

            // 没标注就按 Role 里的"主角"兜底——旧数据一般只有这个字段
            if (picked.Count == 0)
                picked.AddRange(p.Characters.Where(c => c.Role != null && c.Role.Contains("主角")));

            return picked.Where(c => c.Status != CharacterStatus.Deceased).ToList();
        }

        private static void AppendVolumeContext(StringBuilder sb, NovelProject p, int chapterNumber, int budget)
        {
            var volumes = p.UltraLong?.Volumes;
            if (volumes == null || volumes.Count == 0) return;

            var current = p.VolumeOfChapter(chapterNumber);

            sb.AppendLine("【卷目标与前情】");

            if (current != null)
            {
                sb.AppendLine($"本卷：{current.Title}");
                if (!string.IsNullOrWhiteSpace(current.Goal))
                    sb.AppendLine($"本卷目标：{Truncate(current.Goal.Trim(), Math.Max(200, budget / 3))}");
                if (!string.IsNullOrWhiteSpace(current.Summary))
                    sb.AppendLine($"本卷回顾：{Truncate(current.Summary.Trim(), Math.Max(200, budget / 3))}");
            }
            else
            {
                sb.AppendLine("（本章尚未归入任何卷）");
            }

            // 之前的卷：最近 2 卷给回顾，更早的只留标题
            var earlier = volumes
                .Where(v => current == null || v.Number < current.Number)
                .OrderByDescending(v => v.Number)
                .ToList();

            foreach (var v in earlier.Take(2))
            {
                if (string.IsNullOrWhiteSpace(v.Summary)) continue;
                sb.AppendLine($"第{v.Number}卷《{v.Title}》回顾：{Truncate(v.Summary.Trim(), Math.Max(120, budget / 4))}");
            }

            var older = earlier.Skip(2).ToList();
            if (older.Count > 0)
            {
                var titles = string.Join("、", older.OrderBy(v => v.Number).Select(v => $"第{v.Number}卷《{v.Title}》"));
                sb.AppendLine($"更早的卷（只作大局提示，细节不复述）：{Truncate(titles, Math.Max(120, budget / 5))}");
            }

            sb.AppendLine();
        }

        private static void AppendCurrentCharacters(StringBuilder sb, NovelProject p, int chapterNumber, int budget)
        {
            var chapter = p.Chapters.FirstOrDefault(c => c.ChapterNumber == chapterNumber);
            var volume = p.VolumeOfChapter(chapterNumber);

            var ids = new HashSet<string>(chapter?.CharacterIds ?? new List<string>(), StringComparer.Ordinal);

            // 本章没标注就退到"本卷出现过的人"
            if (ids.Count == 0 && volume != null)
            {
                foreach (var c in p.ChaptersOfVolume(volume.VolumeId))
                    foreach (var id in c.CharacterIds ?? new List<string>())
                        ids.Add(id);
            }

            List<Character> picked;
            if (ids.Count > 0)
            {
                picked = p.Characters
                    .Where(c => ids.Contains(c.CharacterId) && c.Status != CharacterStatus.Deceased)
                    .ToList();
            }
            else
            {
                // 连卷都没分：用最近 3 章正文里真出现过的名字兜底。
                // 不调 CharacterAppearanceService 扫全书——那个是给统计用的，太重。
                var recentText = string.Join("\n", p.Chapters
                    .Where(c => c.ChapterNumber < chapterNumber)
                    .OrderByDescending(c => c.ChapterNumber)
                    .Take(3)
                    .Select(c => c.Content ?? ""));

                picked = p.Characters
                    .Where(c => c.Status != CharacterStatus.Deceased
                                && !string.IsNullOrWhiteSpace(c.Name)
                                && recentText.Contains(c.Name, StringComparison.Ordinal))
                    .ToList();
            }

            // 主角状态面板里已经写过的人，这里不重复长篇
            picked = picked.Take(6).ToList();

            if (picked.Count > 0)
            {
                sb.AppendLine("【本章相关的其他角色（只用这些人的设定，不要自己加人）】");
                foreach (var c in picked)
                {
                    var tag = c.Scope == CharacterScope.Core ? "常驻"
                            : c.Scope == CharacterScope.Guest ? "客串" : "本卷";
                    var line = $"· {c.Name}（{tag}{(string.IsNullOrWhiteSpace(c.Role) ? "" : " / " + c.Role.Trim())}）";
                    if (!string.IsNullOrWhiteSpace(c.Personality))
                        line += $"：{Truncate(c.Personality.Trim(), 60)}";
                    if (!string.IsNullOrWhiteSpace(c.CurrentSituation))
                        line += $"；当前：{Truncate(c.CurrentSituation.Trim(), 60)}";
                    sb.AppendLine(Truncate(line, Math.Max(80, budget / Math.Max(1, picked.Count))));
                }
                sb.AppendLine();
            }

            AppendDeadList(sb, p, chapterNumber);
        }

        private static void AppendDeadList(StringBuilder sb, NovelProject p, int chapterNumber)
        {
            // 只列最近 20 章里死的人：写到 300 章时全书死亡名单本身就是几千 token，
            // 而且越早死的越不需要提醒。更早的由设定集「主要人物档案」承担。
            var recentIds = new HashSet<string>(
                p.Chapters
                    .Where(c => c.ChapterNumber >= chapterNumber - 20 && c.ChapterNumber < chapterNumber)
                    .SelectMany(c => c.CharacterIds ?? new List<string>()),
                StringComparer.Ordinal);

            var dead = p.Characters
                .Where(c => c.Status == CharacterStatus.Deceased
                            && (recentIds.Count == 0 || recentIds.Contains(c.CharacterId))
                            && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => c.Name)
                .Distinct()
                .Take(20)
                .ToList();

            if (dead.Count == 0) return;

            sb.AppendLine($"【已死亡/退场（严禁出场）】：{string.Join("、", dead)}");
            sb.AppendLine();
        }

        private static void AppendRecentBrief(StringBuilder sb, NovelProject p, int chapterNumber, int budget)
        {
            var earlier = p.Chapters
                .Where(c => c.ChapterNumber < chapterNumber)
                .OrderByDescending(c => c.ChapterNumber)
                .Take(8)
                .OrderBy(c => c.ChapterNumber)
                .ToList();

            if (earlier.Count == 0) return;

            sb.AppendLine("【最近几章梗概】");
            var per = Math.Max(60, budget / Math.Max(1, earlier.Count));
            foreach (var c in earlier)
            {
                var text = !string.IsNullOrWhiteSpace(c.Summary)
                    ? c.Summary.Trim()
                    : (c.Content ?? "").Trim();

                if (text.Length == 0) continue;
                sb.AppendLine($"· 第{c.ChapterNumber}章《{c.Title}》：{Truncate(text, per)}");
            }
            sb.AppendLine();
        }

        private static void AppendPrevTail(StringBuilder sb, NovelProject p, int chapterNumber, int budget)
        {
            var prev = p.Chapters
                .Where(c => c.ChapterNumber < chapterNumber && !string.IsNullOrWhiteSpace(c.Content))
                .OrderByDescending(c => c.ChapterNumber)
                .FirstOrDefault();

            if (prev == null) return;

            var chars = Math.Min(Math.Max(200, budget), 800);
            var text = prev.Content.Trim();
            var tail = text.Length > chars ? "…" + text[^chars..] : text;

            sb.AppendLine($"【紧邻上一章（第{prev.ChapterNumber}章《{prev.Title}》）末尾原文，供语气与悬念衔接】");
            sb.AppendLine(tail);
            sb.AppendLine();
        }

        private static string Truncate(string text, int max)
        {
            if (max <= 0 || text.Length <= max) return text;
            return text[..max] + "…";
        }
    }
}
