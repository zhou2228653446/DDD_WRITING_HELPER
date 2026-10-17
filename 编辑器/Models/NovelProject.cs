using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace 编辑器
{
    public class NovelProject
    {
        public string ProjectName { get; set; } = "新建小说";
        public string Author { get; set; } = "作者";
        public string Description { get; set; } = "";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
        public string FilePath { get; set; } = "";

        public WorldSetting? WorldSetting { get; set; }
        public List<Character> Characters { get; set; } = new List<Character>();
        public List<Chapter> Chapters { get; set; } = new List<Chapter>();

        // AI 创作上下文
        public string FullOutline { get; set; } = "";           // 全文大纲
        public string ChapterOutline { get; set; } = "";         // 章节大纲
        public string CharacterSettings { get; set; } = "";     // 主要人物设定
        public string BackgroundSettings { get; set; } = "";    // 主要背景设定
        public string WritingStyle { get; set; } = "";          // 文风设定

        // 叙事视角（如「第三人称限知·跟随主角林寒」）。非空时作为**硬约束**注入全部
        // 正文类生成（续写/润色等）：AI 只许写该视角能感知到的内容，防止续写「切头」。
        public string NarrativeViewpoint { get; set; } = "";

        // 「设定集」：把上面的设定统合成一本可制作、可导出的资料书（见 SettingsBook）。
        // 旧项目文件没有这个字段，反序列化时保持 null，首次打开设定集窗口时由
        // SettingsBookTemplates.EnsureBook 补建。
        public SettingsBook? SettingsBook { get; set; }

        // 参考文献库（论文写作用）：用户手动维护或从 BibTeX 导入；
        // AI 生成时整个库当上下文、引用标 [n]，AI 自己不决定引用什么（防编造）。
        public List<LiteratureEntry> LiteratureLibrary { get; set; } = new();

        // 论文导出用：摘要与关键词（论文模式导出时进摘要页；留空则该页省略）
        public string PaperAbstract { get; set; } = "";
        public string PaperKeywords { get; set; } = "";   // 分号或逗号分隔

        /// <summary>
        /// 把章号重排成 1..n 连续（按 Chapters 列表的当前顺序）。
        ///
        /// ⚠ 必须按**列表顺序**编号，不能先按 ChapterNumber 排序：
        /// 调整章节顺序后列表已是新顺序、章号还是旧值，按章号排序等于把顺序还原回去，
        /// 移动就完全失效了——这个 bug 单元测试抓出来过一次。
        ///
        /// 章号是唯一定位依据（chapter_read / chapter_write / 资源 URI），
        /// 中间留空洞会让 agent 找不到章，所以增删移动之后一律要调一次。
        /// </summary>
        public void RenumberChapters()
        {
            int i = 1;
            foreach (var c in Chapters)
                c.ChapterNumber = i++;
        }

        /// <summary>
        /// 把某章在列表里移动 <paramref name="delta"/> 位（负数上移、正数下移），
        /// 然后重排章号。到头了就返回 false，不做任何改动。
        ///
        /// 收敛到这里是为了让大纲窗口和 MCP 共用同一套规则——"移动 + 重排"的组合
        /// 最容易在章号上出错，两处各写一份迟早不一致。
        /// </summary>
        public bool MoveChapter(Chapter chapter, int delta)
        {
            var ordered = Chapters.OrderBy(c => c.ChapterNumber).ToList();
            int index = ordered.IndexOf(chapter);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= ordered.Count) return false;

            ordered.RemoveAt(index);
            ordered.Insert(target, chapter);
            Chapters = ordered;
            RenumberChapters();
            return true;
        }

        /// <summary>
        /// 项目文件与快照共用的 JSON 选项。
        ///
        /// ★ 必须设 <c>UnsafeRelaxedJsonEscaping</c>：
        ///   System.Text.Json 默认把**所有非 ASCII** 转成 <c>\uXXXX</c>，一个汉字占 6 个
        ///   字节。百万字项目光转义就多出 5 倍体积，序列化 CPU 也跟着涨——实测这个开关
        ///   能让 150 万字的 .tdxproj 从约 9MB 降到 1.6MB，读写都快一个数量级。
        ///
        /// ★ 名字里的 "Unsafe" 指的是**不转义 &lt; &gt; &amp;** 这类 HTML 敏感字符。
        ///   这里只写本地文件、Web 端以 application/json 返回，从不当 HTML 拼进页面，
        ///   没有 XSS 面。看到 Unsafe 想改回默认值之前，先读这段。
        /// </summary>
        internal static readonly JsonSerializerOptions FileJson = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath))
                throw new InvalidOperationException("项目路径未设置");

            ModifiedDate = DateTime.Now;
            string json = JsonSerializer.Serialize(this, FileJson);

            // ★ 原子写 + 保留上一版。
            // 整本书就这一个文件，直接 WriteAllText 会在「清空旧内容」和「写完新内容」
            // 之间留下一个时间窗：此刻断电或强杀，落盘的就是一个半截的 JSON，整本报废。
            // 先写 .tmp 再改名，这个窗口就没了。
            //
            // ★ 上一版留 .bak 用「改名」而不是「复制」（2026-10-11 改）：
            //   File.Copy 要把整本书读一遍再写一遍——机械盘上等于每次保存多两倍 I/O，
            //   而我们要保留的只是"上一版内容"，改名即位，零数据搬运。
            //   多人同时写（自用 + 3 个试用）时，这一项直接把写盘量降到 1/4，
            //   顺带缩短了保存锁的持有时间。
            //   任何时刻磁盘上都有一份完整可读的正式文件：
            //     ① 写 tmp 期间 → 旧文件还在
            //     ② 旧文件改名为 .bak 后 → .bak 就是完整一版（正式文件短暂缺席）
            //     ③ tmp 改名为正式文件后 → 新版就位
            //   ②③ 之间断电的话正式文件会缺席，由 Load 用 .bak 顶上（见 Load 里的兜底）。
            var tmp = FilePath + ".tmp";
            var bak = FilePath + ".bak";
            File.WriteAllText(tmp, json);

            try
            {
                if (File.Exists(FilePath))
                    File.Move(FilePath, bak, true);     // 同卷改目录项，不搬数据
                File.Move(tmp, FilePath);               // 同卷内的 Move 是原子操作
            }
            catch
            {
                // 两次改名之间失败：把 .bak 搬回来，绝不让正式文件缺席。
                // （搬不回来也没关系——内容在 .bak 里躺着，Load 能顶上）
                try
                {
                    if (!File.Exists(FilePath) && File.Exists(bak))
                        File.Move(bak, FilePath, true);
                }
                catch { }
                // .tmp 留在原地——内容还在，别让异常把它带走
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        public static NovelProject Load(string path)
        {
            // ★ 兜底：上次保存死在「旧版改名成 .bak」与「新文件就位」之间
            //   （断电 / 强杀；窗口极短但存在，见 Save 里的说明）。
            //   正式文件缺席，而 .bak 是完整的一版——透明地拿它顶上，
            //   否则用户的稿子会"打不开"，可内容其实就在旁边躺着。
            //   ⚠ FilePath 仍要设成**正式路径**（下面那句），这样下次 Save 自然归位。
            var readPath = path;
            if (!File.Exists(readPath))
            {
                var bak = path + ".bak";
                if (!File.Exists(bak))
                    throw new FileNotFoundException("项目文件不存在", path);
                readPath = bak;
            }

            string json = File.ReadAllText(readPath);
            var project = JsonSerializer.Deserialize<NovelProject>(json, FileJson)
                ?? throw new InvalidOperationException("项目文件格式错误");

            // ⚠ 必须把 FilePath 覆盖成**实际打开的路径**，不能信文件里存的那一个。
            // 项目文件自己也会序列化 FilePath（保存时的路径），一旦文件被复制/移动过，
            // 里面存的就是过期路径 —— 而 Save() 是照 FilePath 写的，于是改动会静默写到
            // 别处（最坏情况是覆盖另一个同名项目）。MCP 打开副本时实测踩到过。
            project.FilePath = path;
            return project;
        }

        /// <summary>
        /// 汇总「人物设定」自由文本与结构化人物卡（<see cref="Characters"/>）。
        /// 解决 agent 或作者只维护了人物卡、未手写自由文本时 AI 续写看不见角色设定的断层。
        /// </summary>
        public string BuildEffectiveCharacterSettings()
        {
            var freeText = CharacterSettings?.Trim() ?? "";
            if (Characters == null || Characters.Count == 0)
                return freeText;

            var sb = new System.Text.StringBuilder();
            if (freeText.Length > 0)
            {
                sb.AppendLine(freeText);
                sb.AppendLine();
                sb.AppendLine("【结构化人物卡】");
            }

            foreach (var c in Characters)
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                var tags = new List<string>();
                if (!string.IsNullOrWhiteSpace(c.Role)) tags.Add(c.Role.Trim());
                if (!string.IsNullOrWhiteSpace(c.Gender)) tags.Add(c.Gender.Trim());
                if (c.Age > 0) tags.Add($"{c.Age}岁");
                if (!string.IsNullOrWhiteSpace(c.Occupation)) tags.Add(c.Occupation.Trim());

                sb.AppendLine($"· {c.Name.Trim()}{(tags.Count > 0 ? $"（{string.Join(" / ", tags)}）" : "")}");
                if (!string.IsNullOrWhiteSpace(c.Appearance)) sb.AppendLine($"  外貌：{c.Appearance.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.Personality)) sb.AppendLine($"  性格：{c.Personality.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.Abilities)) sb.AppendLine($"  能力：{c.Abilities.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.Relationships)) sb.AppendLine($"  关系：{c.Relationships.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.Background)) sb.AppendLine($"  背景：{c.Background.Trim()}");
                if (!string.IsNullOrWhiteSpace(c.Notes)) sb.AppendLine($"  备注：{c.Notes.Trim()}");
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 提取设定集中已勾选导出且非空的章节，供常规写作（续写/扩写/润色/聊天）注入稳定前缀。
        /// </summary>
        public string BuildSettingsBookContextBlock()
        {
            if (SettingsBook?.Chapters == null || SettingsBook.Chapters.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            foreach (var ch in SettingsBook.Chapters)
            {
                if (!ch.IncludeInExport || string.IsNullOrWhiteSpace(ch.Content)) continue;
                sb.AppendLine($"### 设定集·{ch.Title}");
                sb.AppendLine(ch.Content.Trim());
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 组装 <paramref name="beforeChapterNumber"/> 之前章节的「前情梗概 + 上一章末尾」块：
        /// <list type="bullet">
        /// <item>优先使用作者/大纲卡片维护的 <see cref="Chapter.Summary"/>；若无梗概且正文非空，退化为截取正文开头。</item>
        /// <item>当 <paramref name="includePrevTail"/> 为 true（用于续写衔接）且紧邻上一章有正文时，附带上一章末尾 <paramref name="tailChars"/> 字，避免续写与上章结尾脱节（改变前只截章节开头 800 字，导致丢失上章结尾悬念）。</item>
        /// </list>
        /// </summary>
        public string BuildPriorChapterBrief(int beforeChapterNumber, bool includePrevTail = true, int maxSummaryChapters = 15, int tailChars = 800)
        {
            if (Chapters == null || Chapters.Count == 0)
                return "";

            var earlier = Chapters
                .Where(c => c.ChapterNumber < beforeChapterNumber
                            && (!string.IsNullOrWhiteSpace(c.Summary) || !string.IsNullOrWhiteSpace(c.Content)))
                .OrderByDescending(c => c.ChapterNumber)
                .Take(maxSummaryChapters)
                .OrderBy(c => c.ChapterNumber)
                .ToList();

            if (earlier.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            foreach (var c in earlier)
            {
                var summary = c.Summary?.Trim() ?? "";
                if (summary.Length > 0)
                {
                    sb.AppendLine($"· 第{c.ChapterNumber}章《{c.Title}》梗概：{summary}");
                }
                else if (!string.IsNullOrWhiteSpace(c.Content))
                {
                    var text = c.Content.Trim();
                    var snippet = text.Length > 300 ? text[..300] + "…" : text;
                    sb.AppendLine($"· 第{c.ChapterNumber}章《{c.Title}》（摘要片段）：{snippet}");
                }
            }

            if (includePrevTail)
            {
                var prev = earlier.LastOrDefault(c => !string.IsNullOrWhiteSpace(c.Content));
                if (prev != null)
                {
                    var content = prev.Content.Trim();
                    var tail = content.Length > tailChars ? "…" + content[^tailChars..] : content;
                    sb.AppendLine();
                    sb.AppendLine($"【紧邻上一章（第{prev.ChapterNumber}章《{prev.Title}》）末尾原文，供语气与悬念衔接】");
                    sb.AppendLine(tail);
                }
            }

            return sb.ToString().Trim();
        }
    }

    public class WorldSetting
    {
        public string WorldName { get; set; } = "";
        public string TimePeriod { get; set; } = "";
        public string Location { get; set; } = "";
        public string Background { get; set; } = "";
        public string MagicSystem { get; set; } = "";
        public string TechnologyLevel { get; set; } = "";
    }

    public class Character
    {
        public string CharacterId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public string Gender { get; set; } = "";
        public string Occupation { get; set; } = "";
        public string Appearance { get; set; } = "";
        public string Personality { get; set; } = "";
        public string Background { get; set; } = "";
        public string Role { get; set; } = ""; // 主角、配角、反派等
        public string Abilities { get; set; } = "";
        public string Relationships { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    public class Chapter : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string propertyName = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public string ChapterId { get; set; } = Guid.NewGuid().ToString();

        private string _title = "新建章节";
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; Notify(); } }
        }

        public int ChapterNumber { get; set; } = 1;

        private string _content = "";
        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                Notify();
                // WordCount 是计算属性，不通知的话界面上绑了字数的地方（章节树）
                // 会一直停留在打开时的旧值。
                Notify(nameof(WordCount));
            }
        }

        public string Summary { get; set; } = "";
        public int WordCount => Content.Length;
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
        public List<string> CharacterIds { get; set; } = new List<string>();
        public string SceneSetting { get; set; } = "";
    }
}