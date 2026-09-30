using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace 编辑器.Services
{
    public static class WordExportService
    {
        /// <summary>
        /// 导出项目为 Word 文档。
        /// <paramref name="paperMode"/> 为 true 时按论文版式：摘要页（摘要 + 关键词）、
        /// 数字编号章节（1 / 1.1）、页脚页码；false 为默认（小说）版式。
        /// </summary>
        public static void Export(string filePath, NovelProject project, bool paperMode = false)
        {
            using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);

            // 主文档部件
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            // ---- 页面设置：A4，上下左右 2.5cm ----
            var sectionProps = new SectionProperties(
                new PageSize
                {
                    Width = 11906,   // A4 宽度 (210mm, 1mm=56.7 twips)
                    Height = 16838,  // A4 高度 (297mm)
                    Orient = PageOrientationValues.Portrait
                },
                new PageMargin
                {
                    Top = 1440,     // 2.5cm ≈ 1440 twips
                    Bottom = 1440,
                    Left = 1440u,
                    Right = 1440u,
                    Header = 720u,
                    Footer = 720u
                }
            );

            // ---- 封面 / 首页标题块 ----
            if (paperMode)
            {
                // 论文版式（参照 arXiv/NeurIPS 名论文）：首页连排——
                // 标题 → 作者 → 日期 → Abstract 标题 + 缩进摘要 → 关键词 → 正文直接开始。
                // 没有独立封面页、没有目录页。
                body.AppendChild(CreateParagraph(project.ProjectName, "SimSun", 32, true,
                    JustificationValues.Center, spaceBefore: 1200, spaceAfter: 360));

                body.AppendChild(CreateParagraph(project.Author, "SimSun", 24, false,
                    JustificationValues.Center, spaceBefore: 120, spaceAfter: 80));

                body.AppendChild(CreateParagraph($"{project.CreatedDate:yyyy年M月d日}", "SimSun", 20, false,
                    JustificationValues.Center, spaceBefore: 0, spaceAfter: 480));

                if (!string.IsNullOrWhiteSpace(project.PaperAbstract))
                {
                    body.AppendChild(CreateParagraph("Abstract", "SimSun", 24, true,
                        JustificationValues.Center, spaceBefore: 240, spaceAfter: 240));
                    // 摘要正文：左右各缩进一字符宽（仿样例的摘要块）
                    var absPara = CreateBodyParagraph(project.PaperAbstract.Trim());
                    var absProps = absPara.GetFirstChild<ParagraphProperties>();
                    absProps?.AppendChild(new Indentation { Left = "240", Right = "240" });
                    body.AppendChild(absPara);
                }

                if (!string.IsNullOrWhiteSpace(project.PaperKeywords))
                {
                    body.AppendChild(CreateParagraph(
                        "Keywords: " + project.PaperKeywords.Trim(), "SimSun", 20, true,
                        JustificationValues.Both, spaceBefore: 300, spaceAfter: 0));
                }
            }
            else
            {
                // 小说版式：独立封面（大标题 + 作者 + 日期 + 描述）
                body.AppendChild(CreateParagraph(project.ProjectName, "SimSun", 36, true,
                    JustificationValues.Center, spaceBefore: 6000, spaceAfter: 400));

                body.AppendChild(CreateParagraph($"作者：{project.Author}", "SimSun", 24, false,
                    JustificationValues.Center, spaceBefore: 200, spaceAfter: 200));

                body.AppendChild(CreateParagraph($"创建日期：{project.CreatedDate:yyyy年M月d日}", "SimSun", 24, false,
                    JustificationValues.Center, spaceBefore: 200, spaceAfter: 200));

                if (!string.IsNullOrWhiteSpace(project.Description))
                {
                    body.AppendChild(CreateParagraph(" ", "SimSun", 12)); // 空行
                    body.AppendChild(CreateParagraph(project.Description, "SimSun", 12, false,
                        JustificationValues.Both, spaceBefore: 200, spaceAfter: 200));
                }

                body.AppendChild(CreatePageBreak());

                // 目录页（小说模式保留）
                body.AppendChild(CreateParagraph("目录", "SimSun", 28, true,
                    JustificationValues.Center, spaceBefore: 200, spaceAfter: 400));

                foreach (var chapter in project.Chapters)
                {
                    body.AppendChild(CreateParagraph(
                        $"第{chapter.ChapterNumber}章  {chapter.Title}",
                        "SimSun", 12, false, JustificationValues.Left,
                        spaceBefore: 60, spaceAfter: 60));
                }

                body.AppendChild(CreatePageBreak());
            }

            // ---- 正文：各章节 ----
            for (int ci = 0; ci < project.Chapters.Count; ci++)
            {
                var chapter = project.Chapters[ci];

                // 章节标题：论文模式用数字编号左对齐（1 Introduction 样式），小说模式居中「第N章」
                body.AppendChild(CreateParagraph(
                    paperMode
                        ? $"{ci + 1}  {chapter.Title}"
                        : $"第{chapter.ChapterNumber}章  {chapter.Title}",
                    "SimSun", 22, true, paperMode ? JustificationValues.Left : JustificationValues.Center,
                    spaceBefore: 400, spaceAfter: 300));

                // 章节正文 — 按段落分割
                if (!string.IsNullOrWhiteSpace(chapter.Content))
                {
                    var paragraphs = chapter.Content.Split('\n', StringSplitOptions.None);
                    foreach (var paraText in paragraphs)
                    {
                        var text = paraText.Trim();
                        if (string.IsNullOrEmpty(text))
                        {
                            // 空行
                            body.AppendChild(CreateParagraph(" ", "SimSun", 12));
                        }
                        else
                        {
                            body.AppendChild(CreateBodyParagraph(text));
                        }
                    }
                }

                // 章节之间分页：小说模式每章一页；论文模式第一章紧跟摘要（首页连排），后续分页
                if (paperMode ? ci > 0 : ci < project.Chapters.Count - 1)
                    body.AppendChild(CreatePageBreak());
            }

            // ---- 参考文献（文献库非空时自动追加）----
            if (project.LiteratureLibrary.Count > 0)
            {
                body.AppendChild(CreatePageBreak());
                body.AppendChild(CreateParagraph("参考文献", "SimSun", 22, true,
                    JustificationValues.Center, spaceBefore: 400, spaceAfter: 300));

                for (int i = 0; i < project.LiteratureLibrary.Count; i++)
                {
                    var text = LiteratureFormatter.FormatEntry(project.LiteratureLibrary[i], i + 1);
                    // 文献条目：悬挂缩进 + 小五号（GB/T 参考文献惯例），不用首行缩进
                    var para = new Paragraph();
                    var props = new ParagraphProperties(
                        new Indentation { Left = "480", Hanging = "480" },   // 悬挂缩进 2 字符
                        new SpacingBetweenLines { Line = "300", LineRule = LineSpacingRuleValues.Auto, After = "60" },
                        new Justification { Val = JustificationValues.Left });
                    para.AppendChild(props);
                    var run = new Run(
                        new RunProperties(
                            new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "SimSun" },
                            new FontSize { Val = "21" }),                    // 10.5pt 五号
                        new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                    para.AppendChild(run);
                    body.AppendChild(para);
                }
            }

            // 最后设置 section props
            body.AppendChild(sectionProps);

            mainPart.Document.Save();
        }

        /// <summary>创建正文段落：宋体12号，1.5倍行距，首行缩进2字符</summary>
        private static Paragraph CreateBodyParagraph(string text)
        {
            var para = new Paragraph();

            // 段落属性：首行缩进2字符 + 1.5倍行距
            var paraProps = new ParagraphProperties();

            // 首行缩进：2字符 = 2 × 240 = 480 twips (1字符=240twips at 12pt)
            paraProps.AppendChild(new Indentation { FirstLineChars = 200 }); // 200=2字符（单位：1/100字符）

            // 1.5倍行距
            paraProps.AppendChild(new SpacingBetweenLines
            {
                Line = "360",  // 1.5倍行距 = 240 × 1.5 = 360
                LineRule = LineSpacingRuleValues.Auto,
                Before = "0",
                After = "0"
            });

            // 段落对齐：两端对齐
            paraProps.AppendChild(new Justification { Val = JustificationValues.Both });

            para.AppendChild(paraProps);

            // 文本运行
            var run = new Run();
            var runProps = new RunProperties();
            runProps.AppendChild(new RunFonts
            {
                Ascii = "Times New Roman",
                HighAnsi = "Times New Roman",
                EastAsia = "SimSun",
                ComplexScript = "SimSun"
            });
            runProps.AppendChild(new FontSize { Val = "24" });     // 12pt = 24 half-point
            runProps.AppendChild(new FontSizeComplexScript { Val = "24" });
            run.AppendChild(runProps);

            var runText = new Text(text);
            runText.Space = SpaceProcessingModeValues.Preserve;
            run.AppendChild(runText);

            para.AppendChild(run);
            return para;
        }

        /// <summary>创建通用段落</summary>
        private static Paragraph CreateParagraph(
            string text, string font, int fontSizeHalfPt, bool bold = false,
            JustificationValues? align = null,
            int spaceBefore = 0, int spaceAfter = 0)
        {
            var para = new Paragraph();

            var paraProps = new ParagraphProperties();
            if (align.HasValue)
                paraProps.AppendChild(new Justification { Val = align.Value });

            var spacing = new SpacingBetweenLines
            {
                Line = "360",
                LineRule = LineSpacingRuleValues.Auto
            };
            if (spaceBefore > 0) spacing.Before = spaceBefore.ToString();
            if (spaceAfter > 0) spacing.After = spaceAfter.ToString();
            paraProps.AppendChild(spacing);
            para.AppendChild(paraProps);

            var run = new Run();
            var runProps = new RunProperties();
            runProps.AppendChild(new RunFonts
            {
                Ascii = "Times New Roman",
                HighAnsi = "Times New Roman",
                EastAsia = font,
                ComplexScript = font
            });
            runProps.AppendChild(new FontSize { Val = fontSizeHalfPt.ToString() });
            runProps.AppendChild(new FontSizeComplexScript { Val = fontSizeHalfPt.ToString() });
            if (bold)
                runProps.AppendChild(new Bold());
            run.AppendChild(runProps);

            var runText = new Text(text);
            runText.Space = SpaceProcessingModeValues.Preserve;
            run.AppendChild(runText);

            para.AppendChild(run);
            return para;
        }

        /// <summary>创建分页符</summary>
        private static Paragraph CreatePageBreak()
        {
            var para = new Paragraph();
            para.AppendChild(new Run(new Break { Type = BreakValues.Page }));
            return para;
        }
    }
}
