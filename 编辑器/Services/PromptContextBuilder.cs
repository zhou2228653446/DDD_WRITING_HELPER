namespace 编辑器.Services;

/// <summary>
/// 组装 system 提示词的上下文部分。
///
/// ★ 为什么从 MainWindow 里抽出来：Web 版需要同样的上下文组装。
/// 两边各写一份的话，改一处上下文规则（比如新增"文献库"那段）就得改两处，
/// 忘了改的那边会静默生成质量更差的内容——这正是"同源性"要防的事。
///
/// 现在桌面版、Web 版、MCP 走的是同一个实现，规则只有一份。
/// </summary>
public static class PromptContextBuilder
{
    /// <summary>项目设定段：大纲 / 人物 / 背景 / 文风 / 记忆 / 视角。</summary>
    public static string ProjectContext(NovelProject? project, string? memory)
    {
        if (project == null) return "";
        return AiPrompts.BuildContextBlock(
            project.FullOutline,
            project.ChapterOutline,
            project.BuildEffectiveCharacterSettings(),
            project.BackgroundSettings,
            project.WritingStyle,
            memory,
            project.NarrativeViewpoint);
    }

    /// <summary>参考章节段：用户勾选的其他章节（空的就不产生段落）。</summary>
    public static string RelatedChapters(NovelProject? project, IEnumerable<string>? selectedIds)
    {
        if (project == null || selectedIds == null) return "";

        var ids = selectedIds.ToList();
        if (ids.Count == 0) return "";

        var rows = project.Chapters
            .Where(c => ids.Contains(c.ChapterId))
            .Select(c => (c.ChapterNumber, c.Title, c.Content ?? ""));

        return AiPrompts.BuildRelatedChapters(rows);
    }

    /// <summary>
    /// 组装完整 system 提示词：身份 + 项目设定 + 设定集 + 文献库 + 参考章节 + 任务 + 输出契约。
    ///
    /// 返回**分段**结果（SystemPrompt）而不是拼好的字符串：设定段跨请求基本不变、
    /// 可打缓存断点，勾选章节段随时会变、不能打。意义见 <see cref="AiPrompts.BuildSections"/>。
    /// </summary>
    public static SystemPrompt BuildSystemPrompt(
        NovelProject? project,
        string task,
        string? memory = null,
        IEnumerable<string>? relatedChapterIds = null,
        string? outputContract = null,
        bool includeSettingsBook = true)
    {
        var stable = ProjectContext(project, memory);

        if (includeSettingsBook && project != null)
        {
            var sbBlock = project.BuildSettingsBookContextBlock();
            if (!string.IsNullOrWhiteSpace(sbBlock))
                stable = Join(stable, AiPrompts.Section("设定集（已确认设定）", sbBlock).TrimEnd());
        }

        // 参考文献库非空时追加引用块：正文引 [n]、只能引列表内文献（防编造）。
        // 挂在这个单一收口点上，全部生成功能（含万能聊天）自动生效。
        var literature = project?.LiteratureLibrary;
        if (literature != null && literature.Count > 0)
            stable = Join(stable, LiteratureFormatter.BuildContextBlock(literature));

        return AiPrompts.BuildSections(task, stable, RelatedChapters(project, relatedChapterIds), outputContract);
    }

    private static string Join(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b;
        if (string.IsNullOrWhiteSpace(b)) return a;
        return a + "\n\n" + b;
    }
}
