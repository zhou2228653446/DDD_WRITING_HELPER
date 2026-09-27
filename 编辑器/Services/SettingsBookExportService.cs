using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace 编辑器.Services
{
    /// <summary>
    /// 「设定集」导出：把 SettingsBook 导成一本「书」—— 封面 → 目录 → 各章。
    ///
    /// 与整本小说的导出（WordExportService / PdfExportService）不同：设定集是资料书，
    /// 正文是条目化内容，所以渲染时做**轻量 Markdown 解析**（# 小标题 / - 列表 /
    /// **粗体**），并且正文段落**不做首行缩进**（资料书读起来像手册，不像小说正文）。
    ///
    /// 三个格式共用同一套行解析（<see cref="ParseContent"/>），只是渲染器不同。
    /// 只导出 <see cref="SettingsBookChapter.IncludeInExport"/> 为 true 的章。
    /// </summary>
    public static class SettingsBookExportService
    {
        // ==================================================================
        // 行解析（三种格式共用）
        // ==================================================================

        private enum MdKind { Heading1, Heading2, Heading3, ListItem, Paragraph, Blank }

        private readonly record struct MdLine(MdKind Kind, string Text);

        /// <summary>把章正文按行解析成排版单元。只认 # / ## / ### / - / 空行，其余一律正文。</summary>
        private static List<MdLine> ParseContent(string content)
        {
            var lines = new List<MdLine>();
            foreach (var raw in (content ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(new MdLine(MdKind.Blank, ""));
                    continue;
                }

                var text = line.TrimStart();
                if (text.StartsWith("### ", StringComparison.Ordinal))
                    lines.Add(new MdLine(MdKind.Heading3, text[4..].Trim()));
                else if (text.StartsWith("## ", StringComparison.Ordinal))
                    lines.Add(new MdLine(MdKind.Heading2, text[3..].Trim()));
                else if (text.StartsWith("# ", StringComparison.Ordinal))
                    lines.Add(new MdLine(MdKind.Heading1, text[2..].Trim()));
                else if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
                    lines.Add(new MdLine(MdKind.ListItem, text[2..].Trim()));
                else
                    lines.Add(new MdLine(MdKind.Paragraph, text));
            }
            return lines;
        }

        /// <summary>按 ** 切出粗体片段；奇数段是粗体。</summary>
        private static List<(string Text, bool Bold)> SplitBold(string text)
        {
            var result = new List<(string, bool)>();
            var parts = (text ?? "").Split("**");
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                    result.Add((parts[i], i % 2 == 1));
            }
            if (result.Count == 0) result.Add((" ", false));
            return result;
        }

        private static string StripBold(string text) => (text ?? "").Replace("**", "");

        /// <summary>书名：设定集自己的 Title 优先，空则「{项目名} · 设定集」。</summary>
        public static string BookTitle(SettingsBook book, NovelProject project)
        {
            if (!string.IsNullOrWhiteSpace(book.Title)) return book.Title.Trim();
            return $"{project.ProjectName} · 设定集";
        }

        private static IEnumerable<SettingsBookChapter> Exportable(SettingsBook book) =>
            book.Chapters.Where(c => c.IncludeInExport);

        // ==================================================================
        // Word
        // ==================================================================

        public static void ExportWord(string filePath, NovelProject project)
        {
            var book = project.SettingsBook ?? new SettingsBook();
            string title = BookTitle(book, project);

            using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = mainPart.Document.AppendChild(new Body());

            var sectionProps = new SectionProperties(
                new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait },
                new PageMargin { Top = 1440, Bottom = 1440, Left = 1440u, Right = 1440u, Header = 720u, Footer = 720u });

            // ---- 封面 ----
            body.AppendChild(Para(title, 36, true, JustificationValues.Center, 6000, 400));
            if (!string.IsNullOrWhiteSpace(book.Subtitle))
                body.AppendChild(Para(book.Subtitle.Trim(), 18, false, JustificationValues.Center, 300, 300));
            body.AppendChild(Para($"作者：{project.Author}", 24, false, JustificationValues.Center, 200, 200));
            body.AppendChild(Para($"创建日期：{project.CreatedDate:yyyy年M月d日}", 24, false, JustificationValues.Center, 200, 400));

            // 前言：标出哪些章是 AI 初稿
            var aiChapters = Exportable(book).Where(c => c.IsAiGenerated).Select(c => c.Title).ToList();
            if (aiChapters.Count > 0)
            {
                body.AppendChild(Para(" ", 12));
                body.AppendChild(Para("说明：以下章节由 AI 依据项目设定生成初稿，请以作者最终审校为准："
                    + string.Join("、", aiChapters) + "。", 10, false, JustificationValues.Both, 200, 200));
            }

            body.AppendChild(PageBreak());

            // ---- 目录 ----
            body.AppendChild(Para("目录", 28, true, JustificationValues.Center, 200, 400));
            foreach (var ch in Exportable(book))
                body.AppendChild(Para(ch.Title, 12, false, JustificationValues.Left, 60, 60));

            body.AppendChild(PageBreak());

            // ---- 正文：每章一页 ----
            var exportable = Exportable(book).ToList();
            foreach (var ch in exportable)
            {
                body.AppendChild(Para(ch.Title, 22, true, JustificationValues.Center, 400, 300));
                AppendWordContent(body, ch.Content);
                if (ch != exportable.Last())
                    body.AppendChild(PageBreak());
            }

            body.AppendChild(sectionProps);
            mainPart.Document.Save();
        }

        private static void AppendWordContent(Body body, string content)
        {
            foreach (var line in ParseContent(content))
            {
                switch (line.Kind)
                {
                    case MdKind.Heading1:
                        body.AppendChild(Para(line.Text, 18, true, JustificationValues.Center, 300, 150));
                        break;
                    case MdKind.Heading2:
                        body.AppendChild(Para(line.Text, 15, true, JustificationValues.Left, 240, 120));
                        break;
                    case MdKind.Heading3:
                        body.AppendChild(Para(line.Text, 13, true, JustificationValues.Left, 180, 90));
                        break;
                    case MdKind.ListItem:
                        body.AppendChild(ListPara(line.Text));
                        break;
                    case MdKind.Paragraph:
                        body.AppendChild(ContentPara(line.Text));
                        break;
                    case MdKind.Blank:
                        body.AppendChild(Para(" ", 12));
                        break;
                }
            }
        }

        /// <summary>通用段落（含粗体渲染）。</summary>
        private static Paragraph Para(string text, int halfPt = 12, bool bold = false,
            JustificationValues? align = null, int before = 0, int after = 0)
        {
            var para = new Paragraph();
            var props = new ParagraphProperties(
                new Justification { Val = align ?? JustificationValues.Left },
                new SpacingBetweenLines
                {
                    Line = "360", LineRule = LineSpacingRuleValues.Auto,
                    Before = before.ToString(), After = after.ToString()
                });
            para.AppendChild(props);

            foreach (var (seg, segBold) in SplitBold(text))
                para.AppendChild(RunOf(seg, halfPt, bold || segBold));

            return para;
        }

        /// <summary>正文段落：两端对齐、1.5 倍行距、无首行缩进（资料书风格）。</summary>
        private static Paragraph ContentPara(string text)
        {
            var para = new Paragraph();
            var props = new ParagraphProperties(
                new Justification { Val = JustificationValues.Both },
                new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto, Before = "0", After = "0" });
            para.AppendChild(props);
            foreach (var (seg, segBold) in SplitBold(text))
                para.AppendChild(RunOf(seg, 12, segBold));
            return para;
        }

        /// <summary>列表项：左缩进 + 项目符号。</summary>
        private static Paragraph ListPara(string text)
        {
            var para = new Paragraph();
            var props = new ParagraphProperties(
                new Justification { Val = JustificationValues.Left },
                new Indentation { Left = "420" },
                new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto, Before = "0", After = "0" });
            para.AppendChild(props);
            para.AppendChild(RunOf("· ", 12, false));
            foreach (var (seg, segBold) in SplitBold(text))
                para.AppendChild(RunOf(seg, 12, segBold));
            return para;
        }

        private static Run RunOf(string text, int halfPt, bool bold)
        {
            var run = new Run();
            var rp = new RunProperties();
            rp.AppendChild(new RunFonts
            {
                Ascii = "Times New Roman", HighAnsi = "Times New Roman",
                EastAsia = "SimSun", ComplexScript = "SimSun"
            });
            rp.AppendChild(new FontSize { Val = halfPt.ToString() });
            rp.AppendChild(new FontSizeComplexScript { Val = halfPt.ToString() });
            if (bold) rp.AppendChild(new Bold());
            run.AppendChild(rp);
            var t = new Text(text) { Space = SpaceProcessingModeValues.Preserve };
            run.AppendChild(t);
            return run;
        }

        private static Paragraph PageBreak()
        {
            var p = new Paragraph();
            p.AppendChild(new Run(new Break { Type = BreakValues.Page }));
            return p;
        }

        // ==================================================================
        // PDF（QuestPDF）
        // ==================================================================

        private const float PageMarginPt = 71f;   // 2.5cm

        private static bool _pdfInit;
        private static string _cjkFont = "SimSun";

        private static void EnsurePdfInit()
        {
            if (_pdfInit) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
            _cjkFont = ResolveCjkFont();
            _pdfInit = true;
        }

        private static string ResolveCjkFont()
        {
            try
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    if (!string.IsNullOrEmpty(family.Source)) installed.Add(family.Source);
                    foreach (var name in family.FamilyNames.Values)
                        if (!string.IsNullOrEmpty(name)) installed.Add(name);
                }
                foreach (var candidate in new[] { "SimSun", "宋体", "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "KaiTi", "楷体" })
                    if (installed.Contains(candidate)) return candidate;
            }
            catch { }
            return "SimSun";
        }

        private static string Safe(string? text) =>
            string.IsNullOrWhiteSpace(text) ? " " : text!;

        public static void ExportPdf(string filePath, NovelProject project)
        {
            var book = project.SettingsBook ?? new SettingsBook();
            string title = BookTitle(book, project);
            EnsurePdfInit();
            var font = _cjkFont;

            QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.A4);
                    page.Margin(PageMarginPt, Unit.Point);
                    page.DefaultTextStyle(x => x.FontFamily(font).FontSize(12).LineHeight(1.5f));

                    page.Content().Column(col =>
                    {
                        // 封面
                        col.Item().PaddingTop(160).AlignCenter()
                            .Text(Safe(title)).FontSize(28).Bold();
                        if (!string.IsNullOrWhiteSpace(book.Subtitle))
                            col.Item().PaddingTop(20).AlignCenter()
                                .Text(Safe(book.Subtitle.Trim())).FontSize(14);
                        col.Item().PaddingTop(28).AlignCenter()
                            .Text(Safe($"作者：{project.Author}")).FontSize(14);
                        col.Item().PaddingTop(10).AlignCenter()
                            .Text(Safe($"创建日期：{project.CreatedDate:yyyy年M月d日}")).FontSize(14);

                        var exportable = Exportable(book).ToList();
                        var aiChapters = exportable.Where(c => c.IsAiGenerated).Select(c => c.Title).ToList();
                        if (aiChapters.Count > 0)
                            col.Item().PaddingTop(48)
                                .Text(Safe($"说明：以下章节由 AI 依据项目设定生成初稿：{string.Join("、", aiChapters)}。"))
                                .FontSize(10);

                        // 目录
                        if (exportable.Count > 0)
                        {
                            col.Item().PageBreak();
                            col.Item().PaddingTop(40).AlignCenter().Text("目录").FontSize(20).Bold();
                            col.Item().PaddingTop(28);
                            foreach (var ch in exportable)
                                col.Item().PaddingVertical(5).Text(Safe(ch.Title)).FontSize(12);
                        }

                        // 正文：每章一页
                        foreach (var ch in exportable)
                        {
                            col.Item().PageBreak();
                            col.Item().PaddingTop(36).AlignCenter()
                                .Text(Safe(ch.Title)).FontSize(16).Bold();
                            col.Item().PaddingTop(26);
                            AppendPdfContent(col, ch.Content, font);
                        }
                    });
                });
            }).GeneratePdf(filePath);
        }

        private static void AppendPdfContent(ColumnDescriptor col, string content, string font)
        {
            foreach (var line in ParseContent(content))
            {
                switch (line.Kind)
                {
                    case MdKind.Heading1:
                        col.Item().PaddingTop(18).AlignCenter()
                            .Text(x => x.Span(Safe(line.Text)).FontSize(16).Bold());
                        break;
                    case MdKind.Heading2:
                        col.Item().PaddingTop(12)
                            .Text(x => x.Span(Safe(line.Text)).FontSize(13.5f).Bold());
                        break;
                    case MdKind.Heading3:
                        col.Item().PaddingTop(8)
                            .Text(x => x.Span(Safe(line.Text)).FontSize(12).Bold());
                        break;
                    case MdKind.ListItem:
                        col.Item().PaddingLeft(18).PaddingVertical(2)
                            .Text(x =>
                            {
                                x.Span("· ");
                                foreach (var (seg, segBold) in SplitBold(line.Text))
                                {
                                    if (segBold) x.Span(seg).Bold();
                                    else x.Span(seg);
                                }
                            });
                        break;
                    case MdKind.Paragraph:
                        col.Item().PaddingVertical(2)
                            .Text(x =>
                            {
                                foreach (var (seg, segBold) in SplitBold(line.Text))
                                {
                                    if (segBold) x.Span(seg).Bold();
                                    else x.Span(seg);
                                }
                            });
                        break;
                    case MdKind.Blank:
                        col.Item().PaddingVertical(4);
                        break;
                }
            }
        }

        // ==================================================================
        // TXT
        // ==================================================================

        /// <summary>导出为纯文本：封面 → 目录 → 各章。TXT 必须 UTF-8 带 BOM（见项目约定）。</summary>
        public static void ExportTxt(string filePath, NovelProject project)
        {
            var book = project.SettingsBook ?? new SettingsBook();
            string title = BookTitle(book, project);
            var sb = new StringBuilder();

            sb.AppendLine(title);
            if (!string.IsNullOrWhiteSpace(book.Subtitle))
                sb.AppendLine(book.Subtitle.Trim());
            sb.AppendLine($"作者：{project.Author}");
            sb.AppendLine($"创建日期：{project.CreatedDate:yyyy年M月d日}");

            var exportable = Exportable(book).ToList();
            var aiChapters = exportable.Where(c => c.IsAiGenerated).Select(c => c.Title).ToList();
            if (aiChapters.Count > 0)
                sb.AppendLine($"说明：以下章节由 AI 依据项目设定生成初稿：{string.Join("、", aiChapters)}。");

            sb.AppendLine();
            sb.AppendLine("========== 目录 ==========");
            for (int i = 0; i < exportable.Count; i++)
                sb.AppendLine($"{i + 1}. {exportable[i].Title}");

            for (int i = 0; i < exportable.Count; i++)
            {
                sb.AppendLine();
                sb.AppendLine($"========== {exportable[i].Title} ==========");
                foreach (var line in ParseContent(exportable[i].Content))
                {
                    switch (line.Kind)
                    {
                        case MdKind.Heading1: sb.AppendLine($"# {StripBold(line.Text)}"); break;
                        case MdKind.Heading2: sb.AppendLine($"## {StripBold(line.Text)}"); break;
                        case MdKind.Heading3: sb.AppendLine($"### {StripBold(line.Text)}"); break;
                        case MdKind.ListItem: sb.AppendLine($"- {StripBold(line.Text)}"); break;
                        case MdKind.Paragraph: sb.AppendLine(StripBold(line.Text)); break;
                        case MdKind.Blank: sb.AppendLine(); break;
                    }
                }
            }

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }
}
