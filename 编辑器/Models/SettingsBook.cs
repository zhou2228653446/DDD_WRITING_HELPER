using System;
using System.Collections.Generic;

namespace 编辑器
{
    /// <summary>
    /// 「设定集」：把散落在项目各处的设定（全文大纲 / 章节大纲 / 人物 / 背景 / 文风 /
    /// 作品简介 / 结构化世界观）统合成一本结构化的资料书。
    ///
    /// 它是一本可以「制作」的书：用户决定哪些章进书、按什么顺序、每章写什么；
    /// AI 负责按模板补全缺内容；最后导出为 Word / PDF / TXT 拿给读者或自己查阅。
    ///
    /// 随项目保存（<see cref="NovelProject.SettingsBook"/>），不额外开文件。
    /// 章节结构由 <see cref="Services.SettingsBookTemplates"/> 提供默认骨架，
    /// 用户增删章、改标题、调顺序都只动本书数据，不碰模板。
    /// </summary>
    public class SettingsBook
    {
        /// <summary>书名。空 = 导出时用「{项目名} · 设定集」。</summary>
        public string Title { get; set; } = "";

        /// <summary>副标题，一句话定位这本书（可选）。</summary>
        public string Subtitle { get; set; } = "";

        /// <summary>章节，顺序即书中顺序。</summary>
        public List<SettingsBookChapter> Chapters { get; set; } = new();
    }

    /// <summary>设定集中的一章。</summary>
    public class SettingsBookChapter
    {
        public string ChapterId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 模板键（<see cref="Services.SettingsBookTemplates"/> 里的 SourceKey）。
        /// 自动成书时按它匹配「模板章 ↔ 书里已有章」；用户新增的自定义章为空串。
        /// </summary>
        public string SourceKey { get; set; } = "";

        public string Title { get; set; } = "";

        /// <summary>章正文，允许轻量 Markdown（# 小标题 / - 列表 / **粗体**），导出时渲染。</summary>
        public string Content { get; set; } = "";

        /// <summary>是否进书（导出的书里包含这一章）。</summary>
        public bool IncludeInExport { get; set; } = true;

        /// <summary>内容是否由 AI 生成（UI 徽标用，也写进导出前言，提醒"这一章是 AI 初稿"）。</summary>
        public bool IsAiGenerated { get; set; }

        public DateTime ModifiedDate { get; set; } = DateTime.Now;
    }
}
