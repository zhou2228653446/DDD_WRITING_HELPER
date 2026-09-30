using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace 编辑器.Services
{
    /// <summary>一个角色的出场统计结果。</summary>
    public class AppearanceStat
    {
        public string Name { get; init; } = "";
        /// <summary>出场的章节号列表（按章号升序）。</summary>
        public List<int> Chapters { get; init; } = new();
        /// <summary>出现的总次数（所有章合计）。</summary>
        public int TotalHits { get; set; }
        /// <summary>最后一次出场的章号；从未出场为 null。</summary>
        public int? LastChapter { get; set; }

        /// <summary>距最后一章缺席了多少章（连续缺席计算：最后出场章之后还有多少章）。</summary>
        public int AbsentStreak(int lastChapterNumber) =>
            LastChapter is int last ? Math.Max(0, lastChapterNumber - last) : Chapters.Count > 0 ? 0 : lastChapterNumber;
    }

    /// <summary>
    /// 角色出场统计 —— 纯本地文本扫描，不花 token、不调 AI。
    ///
    /// 名单来源：项目「人物设定」文本的行首姓名 + 结构化角色 Characters。
    /// 匹配策略：每章正文里数名字出现次数（含姓+名的短称呼，如「林寒」「寒哥」不做模糊，
    /// 只做「全名」与「名（去姓）≥2 字」两种精确串匹配，避免单字误命中）。
    /// </summary>
    public static class CharacterAppearanceService
    {
        /// <summary>从项目里收集候选角色名（去重、去空、≥2 字）。</summary>
        public static List<string> CollectNames(NovelProject project)
        {
            var names = new List<string>();

            // 结构化角色（有维护就用）
            if (project.Characters is { Count: > 0 })
                names.AddRange(project.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => c.Name.Trim()));

            // 人物设定文本：行首「姓名：/姓名，/姓名 」的写法也认
            var text = project.CharacterSettings ?? "";
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimStart('-', '*', '•', ' ', '　');
                if (line.Length < 2) continue;
                // 「林寒：主角……」「林寒，……」「1. 林寒……」
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"^(?:\d+[\.、)\)]\s*)?([\u4e00-\u9fa5A-Za-z·]{2,8})(?=[：:，,（(\s])");
                if (m.Success) names.Add(m.Groups[1].Value);
            }

            return names
                .Where(n => n.Length >= 2 && n.Length <= 8)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>生成一个名字的全部匹配串：全名 + 去姓的名（≥2 字才算，防单字误命中）。</summary>
        private static List<string> MatchKeys(string name)
        {
            var keys = new List<string> { name };
            if (name.Length >= 3)
            {
                // 去掉常见姓氏取「名」；复姓处理：前两字是常见复姓就去两个
                var given = name[1..];
                if (IsCompoundSurname(name[..2])) given = name[2..];
                if (given.Length >= 2) keys.Add(given);
            }
            return keys;
        }

        private static bool IsCompoundSurname(string two) =>
            two is "欧阳" or "司马" or "上官" or "诸葛" or "东方" or "夏侯" or "皇甫" or "尉迟"
                or "公孙" or "长孙" or "慕容" or "端木" or "独孤" or "南宫" or "西门" or "司徒";

        /// <summary>统计全部角色。返回按「最近出场章号降序 → 总次数降序」排列的列表。</summary>
        public static List<AppearanceStat> Analyze(NovelProject project)
        {
            var names = CollectNames(project);
            var chapters = project.Chapters
                .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                .OrderBy(c => c.ChapterNumber)
                .ToList();
            var lastNumber = chapters.Count > 0 ? chapters[^1].ChapterNumber : 0;

            var result = new List<AppearanceStat>();
            foreach (var name in names)
            {
                var keys = MatchKeys(name);
                var stat = new AppearanceStat { Name = name };
                foreach (var ch in chapters)
                {
                    var hits = 0;
                    foreach (var k in keys) hits += CountOccurrences(ch.Content, k);
                    if (hits > 0)
                    {
                        stat.Chapters.Add(ch.ChapterNumber);
                        stat.TotalHits += hits;
                        stat.LastChapter = ch.ChapterNumber;
                    }
                }
                result.Add(stat);
            }

            return result
                .OrderByDescending(s => s.LastChapter ?? -1)
                .ThenByDescending(s => s.TotalHits)
                .ToList();
        }

        /// <summary>统计一个名字在一章里的出现次数。名字很短时要求非重叠匹配即可（不做词边界）。</summary>
        private static int CountOccurrences(string text, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(key, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += key.Length;
            }
            return count;
        }

        /// <summary>渲染成可读报告（放进只读窗口）。</summary>
        public static string FormatReport(NovelProject project, List<AppearanceStat> stats)
        {
            var sb = new StringBuilder();
            var lastNumber = project.Chapters.Count > 0 ? project.Chapters.Max(c => c.ChapterNumber) : 0;
            sb.AppendLine($"全书共 {project.Chapters.Count} 章（统计到第 {lastNumber} 章），角色 {stats.Count} 人。");
            sb.AppendLine("排序：最近出场靠前；「连续缺席」= 最后一次出场之后又过了几章。");
            sb.AppendLine();

            foreach (var s in stats)
            {
                if (s.Chapters.Count == 0)
                {
                    sb.AppendLine($"· {s.Name}：从未出场");
                    continue;
                }
                var absent = lastNumber - (s.LastChapter ?? 0);
                var absentNote = absent >= 3 ? $"　⚠ 已缺席 {absent} 章" : "";
                var chapterList = s.Chapters.Count <= 20
                    ? string.Join("、", s.Chapters)
                    : string.Join("、", s.Chapters.Take(15)) + $" …（共 {s.Chapters.Count} 章）";
                sb.AppendLine($"· {s.Name}：{s.TotalHits} 次，最近第 {s.LastChapter} 章{absentNote}");
                sb.AppendLine($"  出场章：{chapterList}");
            }

            var missing = stats.Where(s => s.Chapters.Count > 0 && lastNumber - (s.LastChapter ?? 0) >= 5).ToList();
            if (missing.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("── 连续缺席 ≥5 章（想想他们去哪了）──");
                foreach (var m in missing)
                    sb.AppendLine($"· {m.Name}：最后出场第 {m.LastChapter} 章，已缺席 {lastNumber - m.LastChapter} 章");
            }
            return sb.ToString();
        }
    }
}
