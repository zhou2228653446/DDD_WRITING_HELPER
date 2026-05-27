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
        /// 导出项目为 Word 文档
        /// </summary>
        public static void Export(string filePath, NovelProject project)
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

            // ---- 封面 ----
            // 项目名
            body.AppendChild(CreateParagraph(project.ProjectName, "SimSun", 36, true,
                JustificationValues.Center, spaceBefore: 6000, spaceAfter: 400));

            // 作者
            body.AppendChild(CreateParagraph($"作者：{project.Author}", "SimSun", 24, false,
                JustificationValues.Center, spaceBefore: 200, spaceAfter: 200));

            // 日期
            body.AppendChild(CreateParagraph($"创建日期：{project.CreatedDate:yyyy年M月d日}", "SimSun", 24, false,
                JustificationValues.Center, spaceBefore: 200, spaceAfter: 200));

            // 描述
            if (!string.IsNullOrWhiteSpace(project.Description))
            {
                body.AppendChild(CreateParagraph(" ", "SimSun", 12)); // 空行
                body.AppendChild(CreateParagraph(project.Description, "SimSun", 12, false,
                    JustificationValues.Both, spaceBefore: 200, spaceAfter: 200));
            }

            // 分页符
            body.AppendChild(CreatePageBreak());

            // ---- 目录页（简要列出章节）----
            body.AppendChild(CreateParagraph("目录", "SimSun", 28, true,
                JustificationValues.Center, spaceBefore: 200, spaceAfter: 400));

            foreach (var chapter in project.Chapters)
            {
                body.AppendChild(CreateParagraph(
                    $"第{chapter.ChapterNumber}章  {chapter.Title}",
                    "SimSun", 12, false, JustificationValues.Left,
                    spaceBefore: 60, spaceAfter: 60));
            }

            // 分页符
            body.AppendChild(CreatePageBreak());

            // ---- 正文：各章节 ----
            foreach (var chapter in project.Chapters)
            {
                // 章节标题
                body.AppendChild(CreateParagraph(
                    $"第{chapter.ChapterNumber}章  {chapter.Title}",
                    "SimSun", 22, true, JustificationValues.Center,
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

                // 章节之间分页（最后一章除外）
                if (chapter != project.Chapters.Last())
                    body.AppendChild(CreatePageBreak());
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
