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
        /// </summary>
        public static void Export(string filePath, NovelProject project)
        {
            var sb = new StringBuilder();

            // ---- 封面信息 ----
            sb.AppendLine(project.ProjectName);
            sb.AppendLine($"作者：{project.Author}");
            sb.AppendLine($"创建日期：{project.CreatedDate:yyyy年M月d日}");
            if (!string.IsNullOrWhiteSpace(project.Description))
            {
                sb.AppendLine();
                sb.AppendLine(project.Description);
            }

            sb.AppendLine();
            sb.AppendLine(new string('=', 40));
            sb.AppendLine();

            // ---- 目录 ----
            if (project.Chapters.Count > 0)
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

                sb.AppendLine($"第{chapter.ChapterNumber}章　{chapter.Title}");
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

            // UTF-8 with BOM：不带 BOM 时记事本会把中文识别成 ANSI 而乱码
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }
}
