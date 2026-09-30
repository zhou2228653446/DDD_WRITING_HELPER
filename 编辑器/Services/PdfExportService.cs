using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace 编辑器.Services
{
    /// <summary>
    /// 导出项目为 PDF。版式与 Word 导出一致：
    /// 封面页 → 目录页 → 每章一页；正文宋体 12pt、1.5 倍行距、首行缩进 2 字符。
    /// 使用 QuestPDF（自包含 SkiaSharp，无额外原生依赖）。
    /// </summary>
    public static class PdfExportService
    {
        /// <summary>四边页边距：2.5cm ≈ 71pt</summary>
        private const float PageMargin = 71f;

        /// <summary>正文首行缩进：2 字符（12pt 字号下 ≈ 24pt）</summary>
        private const float FirstLineIndent = 24f;

        /// <summary>中文字体候选，按优先级探测系统中实际安装的</summary>
        private static readonly string[] CjkFontCandidates =
        {
            "SimSun", "宋体", "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "NSimSun", "KaiTi", "楷体"
        };

        private static bool _initialized;
        private static string _cjkFont = "SimSun";

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            // QuestPDF 社区许可（本项目为个人/非商业用途，符合 Community 条款）
            QuestPDF.Settings.License = LicenseType.Community;

            // 关键：默认开启字形检查时，正文里出现任何一个字体未覆盖的生僻字都会让整个导出抛异常。
            // 小说正文出现生僻字概率不低，这里关掉检查，缺字交由 SkiaSharp 的回退字体处理。
            QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;

            _cjkFont = ResolveCjkFont();
            _initialized = true;
        }

        /// <summary>
        /// 探测本机可用的中文字体名（英文名/中文名都收），避免字体名写错导致中文渲染成方框。
        /// </summary>
        private static string ResolveCjkFont()
        {
            try
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    if (!string.IsNullOrEmpty(family.Source))
                        installed.Add(family.Source);
                    foreach (var name in family.FamilyNames.Values)
                    {
                        if (!string.IsNullOrEmpty(name))
                            installed.Add(name);
                    }
                }

                foreach (var candidate in CjkFontCandidates)
                {
                    if (installed.Contains(candidate))
                        return candidate;
                }
            }
            catch
            {
                // 字体枚举失败时退回 SimSun（Windows 必装）
            }

            return "SimSun";
        }

        /// <summary>空文本在 QuestPDF 中会出问题，统一用一个空格兜底</summary>
        private static string Safe(string? text) =>
            string.IsNullOrWhiteSpace(text) ? " " : text!;

        /// <summary>
        /// 导出项目为 PDF 文件。
        /// <paramref name="paperMode"/> 为 true 时按论文版式：摘要页 + 数字编号章节。
        /// </summary>
        public static void Export(string filePath, NovelProject project, bool paperMode = false)
        {
            EnsureInitialized();

            var font = _cjkFont;

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(PageMargin, Unit.Point);
                    page.DefaultTextStyle(x => x.FontFamily(font).FontSize(12).LineHeight(1.5f));

                    page.Content().Column(col =>
                    {
                        // ---------- 封面 ----------
                        col.Item().PaddingTop(paperMode ? 120 : 160).AlignCenter()
                            .Text(Safe(project.ProjectName)).FontSize(28).Bold();

                        col.Item().PaddingTop(28).AlignCenter()
                            .Text(Safe($"作者：{project.Author}")).FontSize(14);

                        col.Item().PaddingTop(10).AlignCenter()
                            .Text(Safe($"创建日期：{project.CreatedDate:yyyy年M月d日}")).FontSize(14);

                        if (!string.IsNullOrWhiteSpace(project.Description))
                        {
                            col.Item().PaddingTop(48).Text(project.Description!).FontSize(12);
                        }

                        // ---------- 摘要页（论文模式）----------
                        if (paperMode &&
                            (!string.IsNullOrWhiteSpace(project.PaperAbstract) || !string.IsNullOrWhiteSpace(project.PaperKeywords)))
                        {
                            col.Item().PageBreak();
                            col.Item().PaddingTop(40).AlignCenter()
                                .Text("摘  要").FontSize(20).Bold();

                            if (!string.IsNullOrWhiteSpace(project.PaperAbstract))
                            {
                                col.Item().PaddingTop(24)
                                    .Text(project.PaperAbstract.Trim())
                                    .ParagraphFirstLineIndentation(FirstLineIndent, Unit.Point);
                            }

                            if (!string.IsNullOrWhiteSpace(project.PaperKeywords))
                            {
                                col.Item().PaddingTop(20).Text(t =>
                                {
                                    t.Span("关键词：").Bold();
                                    t.Span(project.PaperKeywords.Trim());
                                });
                            }
                        }

                        // ---------- 目录 ----------
                        if (project.Chapters.Count > 0)
                        {
                            col.Item().PageBreak();

                            col.Item().PaddingTop(40).AlignCenter()
                                .Text("目录").FontSize(20).Bold();

                            col.Item().PaddingTop(28);

                            foreach (var chapter in project.Chapters)
                            {
                                col.Item().PaddingVertical(5).Text(Safe(paperMode
                                    ? $"{chapter.ChapterNumber}　{chapter.Title}"
                                    : $"第{chapter.ChapterNumber}章　{chapter.Title}")).FontSize(12);
                            }
                        }

                        // ---------- 正文：每章一页 ----------
                        for (int i = 0; i < project.Chapters.Count; i++)
                        {
                            var chapter = project.Chapters[i];
                            col.Item().PageBreak();

                            col.Item().PaddingTop(36)
                                .Text(Safe(paperMode
                                    ? $"{i + 1}　{chapter.Title}"
                                    : $"第{chapter.ChapterNumber}章　{chapter.Title}"))
                                .FontSize(16).Bold();

                            col.Item().PaddingTop(26);

                            if (!string.IsNullOrWhiteSpace(chapter.Content))
                            {
                                var paragraphs = chapter.Content
                                    .Replace("\r\n", "\n").Replace('\r', '\n')
                                    .Split('\n');

                                foreach (var raw in paragraphs)
                                {
                                    var text = raw.Trim();

                                    if (text.Length == 0)
                                    {
                                        // 保留空行，用空文本块占位
                                        col.Item().PaddingTop(6);
                                    }
                                    else
                                    {
                                        col.Item()
                                            .Text(text)
                                            .ParagraphFirstLineIndentation(FirstLineIndent, Unit.Point);
                                    }
                                }
                            }
                        }

                        // ---------- 参考文献（文献库非空时自动追加）----------
                        if (project.LiteratureLibrary.Count > 0)
                        {
                            col.Item().PageBreak();
                            col.Item().PaddingTop(36).AlignCenter()
                                .Text("参考文献").FontSize(16).Bold();
                            col.Item().PaddingTop(20);

                            for (int i = 0; i < project.LiteratureLibrary.Count; i++)
                            {
                                var text = LiteratureFormatter.FormatEntry(project.LiteratureLibrary[i], i + 1);
                                col.Item().PaddingVertical(3)
                                    .Text(text).FontSize(10.5f);
                            }
                        }
                    });

                    // ---------- 页脚页码 ----------
                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.DefaultTextStyle(x => x.FontSize(9).FontFamily(font));
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            }).GeneratePdf(filePath);
        }
    }
}
