using System;
using System.IO;
using System.Linq;
using System.Text;

namespace 编辑器.Services
{
    /// <summary>
    /// 导出项目为纯文本（.txt）。
    /// 版式与 Word 导出一致：封面信息 → 目录 → 正文（每章之间空行分隔）。
    /// </summary>
    public static class TxtExportService
    {
        /// <summary>
        /// 导出项目为 TXT 文本文件。
        /// <paramref name="paperMode"/> 为 true 时附摘要页与数字编号章节。
        /// </summary>
        public static void Export(string filePath, NovelProject project, bool paperMode = false)
        {
            var sb = new StringBuilder();

            // ---- 封面信息 ----
            sb.AppendLine(project.ProjectName);
            sb.AppendLine(project.Author);
            sb.AppendLine($"{project.CreatedDate:yyyy年M月d日}");
            if (!string.IsNullOrWhiteSpace(project.Description))
            {
                sb.AppendLine();
                sb.AppendLine(project.Description);
            }

            sb.AppendLine();
            sb.AppendLine(new string('=', 40));
            sb.AppendLine();

            // ---- 摘要 + 关键词（论文模式：首页连排，不单独分页）----
            if (paperMode)
            {
                if (!string.IsNullOrWhiteSpace(project.PaperAbstract))
                {
                    sb.AppendLine("Abstract");
                    sb.AppendLine();
                    sb.AppendLine("　　" + project.PaperAbstract.Trim());
                    sb.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(project.PaperKeywords))
                    sb.AppendLine("Keywords: " + project.PaperKeywords.Trim());
                sb.AppendLine();
                sb.AppendLine(new string('=', 40));
                sb.AppendLine();
            }

            // ---- 目录（仅小说模式；论文版式无目录）----
            if (!paperMode && project.Chapters.Count > 0)
            {
                sb.AppendLine("目录");
                sb.AppendLine();
                foreach (var chapter in project.Chapters)
                {
                    sb.AppendLine($"  第{chapter.ChapterNumber}章　{chapter.Title}");
                }
                sb.AppendLine();
                sb.AppendLine(new string('=', 40));
                sb.AppendLine();
            }

            // ---- 正文 ----
            for (int i = 0; i < project.Chapters.Count; i++)
            {
                var chapter = project.Chapters[i];

                sb.AppendLine(paperMode
                    ? $"{i + 1}　{chapter.Title}"
                    : $"第{chapter.ChapterNumber}章　{chapter.Title}");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(chapter.Content))
                {
                    var lines = chapter.Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    foreach (var raw in lines)
                    {
                        var text = raw.Trim();
                        // 段落首行缩进两个全角空格——TXT 无样式，这是排版惯例
                        sb.AppendLine(text.Length == 0 ? string.Empty : "　　" + text);
                    }
                }

                // 章节之间留空行分隔
                if (i < project.Chapters.Count - 1)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }
            }

            // ---- 参考文献（文献库非空时自动追加）----
            if (project.LiteratureLibrary.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("参考文献");
                sb.AppendLine();
                for (int i = 0; i < project.LiteratureLibrary.Count; i++)
                    sb.AppendLine(LiteratureFormatter.FormatEntry(project.LiteratureLibrary[i], i + 1));
            }

            // UTF-8 with BOM：不带 BOM 时记事本会把中文识别成 ANSI 而乱码
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }
}
