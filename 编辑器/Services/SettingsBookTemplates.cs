using System;
using System.Collections.Generic;
using System.Linq;

namespace 编辑器.Services
{
    /// <summary>设定集的一章模板：章从哪来、AI 补全时怎么描述任务。</summary>
    public sealed class SettingsBookTemplate
    {
        /// <summary>唯一键，自动成书时按它把「模板」与「书里已有章」对上。</summary>
        public string SourceKey { get; init; } = "";

        /// <summary>默认章标题（用户可改）。</summary>
        public string Title { get; init; } = "";

        /// <summary>AI 生成该章时的任务描述（随章主题变）。</summary>
        public string AiTask { get; init; } = "";

        /// <summary>该章对应哪些项目字段（用于自动成书时抽取素材），描述性说明。</summary>
        public string Seed { get; init; } = "";
    }

    /// <summary>
    /// 「设定集」的默认骨架与自动成书逻辑。
    ///
    /// 骨架 = 11 个模板章，覆盖一本设定集通常要有的内容。前三类内容直接来自
    /// 项目已有设定（0 成本成书），其余留给 AI 补全。
    ///
    /// 与用户编辑的关系：**自动成书只补空、不覆盖**。按 SourceKey 找到已有章时，
    /// 内容非空的一律不动（那是用户手写或 AI 已生成的成果），只有空章才从项目
    /// 字段抽取填充。这样「打开设定集 → 自动成书」永远是幂等、安全的。
    /// </summary>
    public static class SettingsBookTemplates
    {
        /// <summary>全部模板章，顺序即默认书中顺序。</summary>
        public static readonly IReadOnlyList<SettingsBookTemplate> All = new List<SettingsBookTemplate>
        {
            new()
            {
                SourceKey = "overview", Title = "作品简介",
                Seed = "项目简介 Description、结构化世界观 WorldSetting",
                AiTask = "根据已有信息撰写作品简介：故事发生的时代与地点、主角是谁、围绕什么冲突展开、"
                       + "这部作品最想传达的看点。写成能直接放进书稿的条目式文字，不要提问。"
            },
            new()
            {
                SourceKey = "world", Title = "世界观与背景",
                Seed = "背景设定 BackgroundSettings、世界观 WorldSetting",
                AiTask = "把已有的背景设定整理成一本设定集应有的世界观章节：世界的运行法则、时代特征、"
                       + "社会结构。保留所有已有事实，按条目组织，不要发明与已有设定冲突的内容。"
            },
            new()
            {
                SourceKey = "geography", Title = "地理与势力",
                Seed = "（无直接来源，靠 AI 依据世界观与大纲推演）",
                AiTask = "依据已有世界观与大纲，编写地理章节：主要地点及其风貌、各势力/组织/家族及其立场、"
                       + "重要场所与它们对剧情的作用。凡是与已有设定冲突的推断，标注“【推断】”。"
            },
            new()
            {
                SourceKey = "history", Title = "历史与时间线",
                Seed = "（无直接来源，靠 AI 依据世界观与大纲推演）",
                AiTask = "编写历史章节：作品时间线（重要事件按先后排列）、关键历史节点如何塑造了当下局面。"
                       + "事件要能从已有大纲与背景中推出依据，推不出的标注“【推断】”。"
            },
            new()
            {
                SourceKey = "power", Title = "力量体系与科技",
                Seed = "结构化世界观 WorldSetting 的 MagicSystem / TechnologyLevel",
                AiTask = "整理力量体系/科技水平章节：规则是什么、代价是什么、边界在哪里、进阶途径。"
                       + "已有的内容优先呈现，缺失的部分依据世界观合理补全并标注“【推断】”。"
            },
            new()
            {
                SourceKey = "characters", Title = "主要人物档案",
                Seed = "人物设定 CharacterSettings、结构化角色 Characters",
                AiTask = "整理主要人物档案：每个角色一条，含身份、外貌、性格、动机、成长弧线、"
                       + "与其它角色的关键关系。保留全部已有事实，按人分组，不重复、不矛盾。"
            },
            new()
            {
                SourceKey = "relations", Title = "人物关系网",
                Seed = "（无直接来源，靠 AI 依据人物设定推演）",
                AiTask = "绘制人物关系网：谁与谁是同盟/敌对/亲情/师承，关系如何随剧情演变。"
                       + "以条目或分组的文字呈现，标注【推断】的必须是既有设定没有明说、但合理的内容。"
            },
            new()
            {
                SourceKey = "glossary", Title = "关键设定词汇",
                Seed = "（无直接来源，靠 AI 从全部设定中提炼）",
                AiTask = "从全部设定中提炼关键词汇表：专有名词、术语、地名、组织的标准定义。"
                       + "每条词目一行，格式「词条 —— 定义」。这是设定集里最容易被读者查的章节，务必准确。"
            },
            new()
            {
                SourceKey = "full_outline", Title = "全文大纲",
                Seed = "全文大纲 FullOutline",
                AiTask = "把全文大纲整理成设定集章节：保留原有的情节节点与结构，按故事发展阶段组织。"
            },
            new()
            {
                SourceKey = "chapter_outline", Title = "章节大纲",
                Seed = "章节大纲 ChapterOutline（当前章节）",
                AiTask = "把章节大纲整理成设定集章节：当前章节要写什么、节奏如何、与前后章如何衔接。"
            },
            new()
            {
                SourceKey = "style", Title = "文风设定",
                Seed = "文风设定 WritingStyle",
                AiTask = "把文风设定整理成设定集章节：叙述视角、语言风格、节奏偏好、写作禁忌。"
            },
            new()
            {
                SourceKey = "foreshadow", Title = "伏笔与线索登记",
                Seed = "",
                AiTask = "扫描已有章节正文，登记其中埋设的伏笔与线索：每条按「埋设章节｜伏笔内容｜计划回收点（未知则写『未定』）｜当前状态（未回收/已回收）」列出。只登记正文里真实出现的，不要推测。"
            },
        };

        /// <summary>按模板键查模板；未知键返回 null（自定义章没有模板）。</summary>
        public static SettingsBookTemplate? Find(string sourceKey) =>
            All.FirstOrDefault(t => t.SourceKey == sourceKey);

        /// <summary>章目录里显示的来源说明：这一章的内容从哪来。</summary>
        public static string SourceLabel(string sourceKey) => sourceKey switch
        {
            "overview" => "来自：作品简介",
            "world" => "来自：背景设定",
            "power" => "来自：世界观（力量 / 科技）",
            "characters" => "来自：人物设定",
            "full_outline" => "来自：全文大纲",
            "chapter_outline" => "来自：章节大纲",
            "style" => "来自：文风设定",
            "foreshadow" => "空章 · 让 AI 扫描正文登记伏笔",
            "geography" or "history" or "relations" or "glossary" => "空章 · 可让 AI 补全",
            _ => "自定义章节"
        };

        /// <summary>
        /// 从项目新建一本设定集（不合并）。每个模板章的内容从项目字段抽取；
        /// 没有直接来源的章留空，等用户手写或 AI 补全。
        /// </summary>
        public static SettingsBook Build(NovelProject project)
        {
            var book = new SettingsBook
            {
                Title = string.IsNullOrWhiteSpace(project.ProjectName)
                    ? "" : $"{project.ProjectName} · 设定集"
            };

            foreach (var tpl in All)
            {
                book.Chapters.Add(new SettingsBookChapter
                {
                    SourceKey = tpl.SourceKey,
                    Title = tpl.Title,
                    Content = ExtractSource(project, tpl.SourceKey)
                });
            }
            return book;
        }

        /// <summary>
        /// 确保项目里有一本设定集，且骨架完整。幂等：
        /// · 项目还没有书 → 新建（Build）；
        /// · 缺模板章 → 补上（内容从项目字段抽取）；
        /// · 已有章内容为空但有项目来源 → 填充；
        /// · 已有章内容非空 → **不动**（用户的成果优先）。
        /// </summary>
        public static void EnsureBook(NovelProject project)
        {
            if (project.SettingsBook == null)
            {
                project.SettingsBook = Build(project);
                return;
            }

            var book = project.SettingsBook;
            foreach (var tpl in All)
            {
                var existing = book.Chapters.FirstOrDefault(c => c.SourceKey == tpl.SourceKey);
                if (existing == null)
                {
                    book.Chapters.Add(new SettingsBookChapter
                    {
                        SourceKey = tpl.SourceKey,
                        Title = tpl.Title,
                        Content = ExtractSource(project, tpl.SourceKey)
                    });
                }
                else if (string.IsNullOrWhiteSpace(existing.Content))
                {
                    existing.Content = ExtractSource(project, tpl.SourceKey);
                    existing.ModifiedDate = DateTime.Now;
                }
            }
        }

        /// <summary>把项目里对应来源的设定拼成章正文（纯文本段落）。</summary>
        public static string ExtractSource(NovelProject project, string sourceKey)
        {
            var parts = new List<string>();

            switch (sourceKey)
            {
                case "overview":
                    if (!string.IsNullOrWhiteSpace(project.Description))
                        parts.Add(project.Description.Trim());
                    var w = project.WorldSetting;
                    if (w != null)
                    {
                        var bits = new List<string>();
                        if (!string.IsNullOrWhiteSpace(w.WorldName)) bits.Add($"世界名称：{w.WorldName.Trim()}");
                        if (!string.IsNullOrWhiteSpace(w.TimePeriod)) bits.Add($"时代：{w.TimePeriod.Trim()}");
                        if (!string.IsNullOrWhiteSpace(w.Location)) bits.Add($"主要地点：{w.Location.Trim()}");
                        if (bits.Count > 0) parts.Add(string.Join("\n", bits));
                    }
                    break;

                case "world":
                    if (!string.IsNullOrWhiteSpace(project.BackgroundSettings))
                        parts.Add(project.BackgroundSettings.Trim());
                    if (project.WorldSetting != null)
                    {
                        if (!string.IsNullOrWhiteSpace(project.WorldSetting.Background))
                            parts.Add(project.WorldSetting.Background.Trim());
                    }
                    break;

                case "power":
                    if (project.WorldSetting != null)
                    {
                        if (!string.IsNullOrWhiteSpace(project.WorldSetting.MagicSystem))
                            parts.Add(project.WorldSetting.MagicSystem.Trim());
                        if (!string.IsNullOrWhiteSpace(project.WorldSetting.TechnologyLevel))
                            parts.Add(project.WorldSetting.TechnologyLevel.Trim());
                    }
                    break;

                case "characters":
                    if (!string.IsNullOrWhiteSpace(project.CharacterSettings))
                        parts.Add(project.CharacterSettings.Trim());
                    if (project.Characters is { Count: > 0 })
                    {
                        var lines = project.Characters
                            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                            .Select(c =>
                            {
                                var bits = new List<string> { c.Name.Trim() };
                                if (!string.IsNullOrWhiteSpace(c.Role)) bits.Add($"身份：{c.Role.Trim()}");
                                if (!string.IsNullOrWhiteSpace(c.Occupation)) bits.Add($"职业：{c.Occupation.Trim()}");
                                if (c.Age > 0) bits.Add($"年龄：{c.Age}");
                                if (!string.IsNullOrWhiteSpace(c.Appearance)) bits.Add($"外貌：{c.Appearance.Trim()}");
                                if (!string.IsNullOrWhiteSpace(c.Personality)) bits.Add($"性格：{c.Personality.Trim()}");
                                if (!string.IsNullOrWhiteSpace(c.Background)) bits.Add($"背景：{c.Background.Trim()}");
                                return string.Join("；", bits);
                            });
                        parts.Add(string.Join("\n", lines));
                    }
                    break;

                case "full_outline":
                    if (!string.IsNullOrWhiteSpace(project.FullOutline))
                        parts.Add(project.FullOutline.Trim());
                    break;

                case "chapter_outline":
                    if (!string.IsNullOrWhiteSpace(project.ChapterOutline))
                        parts.Add(project.ChapterOutline.Trim());
                    break;

                case "style":
                    if (!string.IsNullOrWhiteSpace(project.WritingStyle))
                        parts.Add(project.WritingStyle.Trim());
                    break;

                // geography / history / relations / glossary：无直接来源，留给 AI 补全
            }

            return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }
}
