using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using 编辑器.Services;

namespace 编辑器
{
    public partial class MainWindow
    {
        private bool _mcpLiveSyncEnabled = true;
        private bool _suppressMcpMenuEvent;
        private bool _suppressDirtyTracking;
        private CancellationTokenSource? _mcpServerCts;
        private readonly DispatcherTimer _mcpBadgeTimer = new();

        // 流式生成（ai_write writeBack=true）期间记录目标章节的基准正文，便于在编辑框实时滚动预览
        private string? _mcpStreamChapterId;
        private string? _mcpStreamBaseContent;

        private string CurrentSettingsFilePath =>
            !string.IsNullOrWhiteSpace(_configDir)
                ? Path.Combine(_configDir, "settings.json")
                : _settingsFile;

        private void InitMcpLiveSync()
        {
            _mcpBadgeTimer.Interval = TimeSpan.FromSeconds(4);
            _mcpBadgeTimer.Tick += (_, _) =>
            {
                _mcpBadgeTimer.Stop();
                McpLiveBadge.Visibility = Visibility.Collapsed;
            };

            RefreshMcpLiveSyncSetting();
            McpLiveSyncMenuItem.Checked += McpLiveSyncMenuItem_Changed;
            McpLiveSyncMenuItem.Unchecked += McpLiveSyncMenuItem_Changed;

            _mcpServerCts = new CancellationTokenSource();
            _ = McpLiveBridge.StartServer(ev =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => HandleMcpLiveEvent(ev)));
                }
                catch
                {
                }
            }, _mcpServerCts.Token);
        }

        private void RefreshMcpLiveSyncSetting()
        {
            _mcpLiveSyncEnabled = McpLiveBridge.LoadLiveSyncEnabled(CurrentSettingsFilePath);
            _suppressMcpMenuEvent = true;
            try
            {
                McpLiveSyncMenuItem.IsChecked = _mcpLiveSyncEnabled;
            }
            finally
            {
                _suppressMcpMenuEvent = false;
            }

            if (!_mcpLiveSyncEnabled)
            {
                McpLiveBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void McpLiveSyncMenuItem_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressMcpMenuEvent) return;

            _mcpLiveSyncEnabled = McpLiveSyncMenuItem.IsChecked == true;
            McpLiveBridge.SaveLiveSyncEnabled(_mcpLiveSyncEnabled, CurrentSettingsFilePath);

            if (!_mcpLiveSyncEnabled)
            {
                McpLiveBadge.Visibility = Visibility.Collapsed;
                ShowNotification("已关闭「实时跟随 MCP 操作」");
            }
            else
            {
                ShowNotification("已开启「实时跟随 MCP 操作」：外部 AI 操作将实时同步到界面");
            }
        }

        private void HandleMcpLiveEvent(McpLiveEvent ev)
        {
            if (!_mcpLiveSyncEnabled) return;

            // 1. 状态栏徽章与文案实时更新
            McpLiveBadge.Visibility = Visibility.Visible;
            McpLiveBadgeText.Text = ev.EventType is "start" or "stream" ? "MCP 执行中" : "MCP 已同步";
            if (!string.IsNullOrWhiteSpace(ev.Summary))
            {
                UpdateStatus(ev.Summary);
            }

            // 2. 分阶段处理
            if (ev.EventType == "start")
            {
                _mcpBadgeTimer.Stop();

                // 若当前未打开项目（或打开的是别的项目）而 MCP 正在操作某个已落盘项目，先自动切过去
                if (!string.IsNullOrWhiteSpace(ev.ProjectPath) && File.Exists(ev.ProjectPath) &&
                    (_currentProject == null || !PathsEqual(_currentProject.FilePath, ev.ProjectPath)))
                {
                    SyncProjectFromDisk(ev.ProjectPath!, ev.ChapterNumber, ev.SettingField);
                }
                else if (ev.ChapterNumber > 0 && _currentProject != null)
                {
                    var ch = _currentProject.Chapters.FirstOrDefault(c => c.ChapterNumber == ev.ChapterNumber);
                    if (ch != null)
                    {
                        OpenChapterTab(ch);
                        if (ev.ToolName == "ai_write" && ev.WriteBack)
                        {
                            _mcpStreamChapterId = ch.ChapterId;
                            _mcpStreamBaseContent = ch.Content ?? "";
                        }
                    }
                }
                return;
            }

            if (ev.EventType == "stream")
            {
                _mcpBadgeTimer.Stop();

                if (ev.InputTokens > 0 || ev.OutputTokens > 0)
                {
                    TokenUsageTextBlock.Text =
                        $"MCP Token: 输入 {ev.InputTokens} + 输出 {ev.OutputTokens} = {ev.InputTokens + ev.OutputTokens}";
                }

                if (!string.IsNullOrEmpty(ev.PreviewText))
                {
                    _aiPanel.ResultTextBox.Text = ev.PreviewText;
                    _aiPanel.ResultTextBox.ScrollToEnd();

                    // 当 ai_write 开启了 writeBack 且目标是具体章节时，在主编辑框里实时流式展示正文增长
                    if (ev.WriteBack && ev.ChapterNumber > 0 && _currentProject != null &&
                        ev.TaskName is "continue" or "expand" or "polish")
                    {
                        var ch = _currentProject.Chapters.FirstOrDefault(c => c.ChapterNumber == ev.ChapterNumber);
                        if (ch != null)
                        {
                            OpenChapterTab(ch);
                            if (_mcpStreamChapterId != ch.ChapterId || _mcpStreamBaseContent == null)
                            {
                                _mcpStreamChapterId = ch.ChapterId;
                                _mcpStreamBaseContent = ch.Content ?? "";
                            }

                            _suppressDirtyTracking = true;
                            try
                            {
                                if (string.Equals(ev.WriteMode, "replace", StringComparison.OrdinalIgnoreCase))
                                {
                                    ch.Content = ev.PreviewText;
                                }
                                else
                                {
                                    var sep = string.IsNullOrEmpty(_mcpStreamBaseContent) || _mcpStreamBaseContent.EndsWith('\n')
                                        ? ""
                                        : "\n";
                                    ch.Content = _mcpStreamBaseContent + sep + ev.PreviewText;
                                }
                                UpdateWordCount(ch.Content);
                                ScrollEditorToEnd();
                            }
                            finally
                            {
                                _suppressDirtyTracking = false;
                            }
                        }
                    }
                }
                return;
            }

            // done / error
            _mcpStreamChapterId = null;
            _mcpStreamBaseContent = null;
            _mcpBadgeTimer.Stop();
            _mcpBadgeTimer.Start();

            if (!string.IsNullOrEmpty(ev.PreviewText))
            {
                _aiPanel.ResultTextBox.Text = ev.PreviewText;
                _aiPanel.ResultTextBox.ScrollToEnd();
            }

            if (ev.ProjectModified && !string.IsNullOrWhiteSpace(ev.ProjectPath) && File.Exists(ev.ProjectPath))
            {
                SyncProjectFromDisk(ev.ProjectPath!, ev.ChapterNumber, ev.SettingField);
                if (!string.IsNullOrWhiteSpace(ev.Summary))
                {
                    ShowNotification(ev.Summary, isError: ev.EventType == "error");
                }
            }
            else if (ev.EventType == "error" && !string.IsNullOrWhiteSpace(ev.Summary))
            {
                ShowNotification(ev.Summary, isError: true);
            }
            else if (ev.ChapterNumber > 0 && _currentProject != null)
            {
                var ch = _currentProject.Chapters.FirstOrDefault(c => c.ChapterNumber == ev.ChapterNumber);
                if (ch != null) OpenChapterTab(ch);
            }
        }

        /// <summary>
        /// 从磁盘平滑同步 MCP 刚保存的 <c>.tdxproj</c>：
        /// 若是同一项目则原地更新章节与设定，保留已打开的标签页且不闪烁；若是新项目则直接切换。
        /// </summary>
        internal void SyncProjectFromDisk(string projectPath, int targetChapterNumber = 0, string? settingField = null)
        {
            NovelProject? fresh = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    fresh = NovelProject.Load(projectPath);
                    break;
                }
                catch
                {
                    Thread.Sleep(25);
                }
            }
            if (fresh == null) return;

            _suppressDirtyTracking = true;
            try
            {
                // 情况 A：当前未打开项目，或打开的是另一本书 → 整体切换到该书
                if (_currentProject == null || !PathsEqual(_currentProject.FilePath, projectPath))
                {
                    _currentProject = fresh;
                    EditorTabControl.Items.Clear();
                    AttachProjectServices();
                    RefreshProjectView();
                    SyncAiContextFromProject();
                    _settingsBookWindow?.LoadFrom(_currentProject);
                    _outlineWindow?.Refresh();
                    RefreshSnapshotList();

                    var openCh = targetChapterNumber > 0
                        ? _currentProject.Chapters.FirstOrDefault(c => c.ChapterNumber == targetChapterNumber)
                        : _currentProject.Chapters.OrderBy(c => c.ChapterNumber).FirstOrDefault();
                    if (openCh != null)
                    {
                        OpenChapterTab(openCh);
                        UpdateWordCount(openCh.Content);
                        ScrollEditorToEnd();
                    }
                    _isDirty = false;
                    return;
                }

                // 情况 B：同一本书 → 原地更新属性与章节，避免关闭重开标签页造成闪烁
                _currentProject.ProjectName = fresh.ProjectName;
                _currentProject.Description = fresh.Description;
                _currentProject.ModifiedDate = fresh.ModifiedDate;
                _currentProject.FullOutline = fresh.FullOutline;
                _currentProject.ChapterOutline = fresh.ChapterOutline;
                _currentProject.CharacterSettings = fresh.CharacterSettings;
                _currentProject.BackgroundSettings = fresh.BackgroundSettings;
                _currentProject.WritingStyle = fresh.WritingStyle;
                _currentProject.NarrativeViewpoint = fresh.NarrativeViewpoint;
                _currentProject.Characters = fresh.Characters ?? new List<Character>();
                _currentProject.SettingsBook = fresh.SettingsBook;
                _currentProject.LiteratureLibrary = fresh.LiteratureLibrary ?? new List<LiteratureEntry>();

                // ★ 用户正在界面上敲字、还没落盘时，绝不能让磁盘内容盖掉内存里的那几章。
                //   MCP 存的版本里没有用户这一版，直接覆盖就是静默丢稿——
                //   而且丢的是用户亲手写的字，是这个工具最不能出的事。
                //   保护范围取"已打开的标签页 + 当前选中章"：用户最可能在编辑的就是这几章。
                var protectedChapters = new HashSet<Chapter>();
                if (_isDirty)
                {
                    foreach (var tab in EditorTabControl.Items.OfType<TabItem>())
                        if (tab.Tag is Chapter tc) protectedChapters.Add(tc);

                    var sel = GetSelectedChapter();
                    if (sel != null) protectedChapters.Add(sel);
                }

                bool structureChanged =
                    SyncChaptersInPlace(_currentProject.Chapters, fresh.Chapters, protectedChapters);
                if (structureChanged)
                {
                    RefreshProjectView();
                }
                else
                {
                    _aiPanel.RefreshChapterList(_currentProject.Chapters);
                }

                // 刷新附属服务与已打开的子窗口
                _memoryManager?.Load();
                if (_memoryManager != null)
                    _aiPanel.MemoryTextBox.Text = _memoryManager.GetRawMemory();

                RefreshSnapshotList();
                _settingsWindow?.LoadFrom(_currentProject);
                if (_settingsWindow != null && TryMapSettingSection(settingField, out var sec))
                {
                    _settingsWindow.NavigateTo(sec);
                }
                _settingsBookWindow?.LoadFrom(_currentProject);
                _outlineWindow?.Refresh();

                if (targetChapterNumber > 0)
                {
                    var targetCh = _currentProject.Chapters.FirstOrDefault(c => c.ChapterNumber == targetChapterNumber);
                    if (targetCh != null)
                    {
                        OpenChapterTab(targetCh);
                        UpdateChapterInfo(targetCh);
                        UpdateWordCount(targetCh.Content);
                        ScrollEditorToEnd();
                    }
                }
                else
                {
                    var currentCh = GetSelectedChapter();
                    UpdateWordCount(currentCh?.Content);
                }

                if (protectedChapters.Count > 0)
                {
                    // 有意保持 dirty：内存里现在是"MCP 改过的章 + 用户正在改的章"的合并结果，
                    // 交给自动保存写回磁盘，两边的内容都不会丢。
                    ShowNotification(
                        $"MCP 已更新项目，但你正在编辑的 {protectedChapters.Count} 章尚未保存，已为你保留（稍后一并存盘）。",
                        isError: false);
                }
                else
                {
                    _isDirty = false;
                }
            }
            finally
            {
                _suppressDirtyTracking = false;
            }
        }

        /// <param name="protectedChapters">
        /// 用户有未保存编辑的章节：这些章的正文与梗概**不覆盖**，
        /// 保留内存里的版本（其余字段仍同步）。保护后调用方应保持 dirty，
        /// 让自动保存把"MCP 改的章 + 用户改的章"合并写回磁盘。
        /// </param>
        private bool SyncChaptersInPlace(
            List<Chapter> currentList, List<Chapter> freshList,
            HashSet<Chapter>? protectedChapters = null)
        {
            bool structureChanged = currentList.Count != freshList.Count;
            var existingById = currentList.ToDictionary(c => c.ChapterId, StringComparer.Ordinal);
            var keptIds = new HashSet<string>(StringComparer.Ordinal);
            var updatedOrder = new List<Chapter>(freshList.Count);

            for (int i = 0; i < freshList.Count; i++)
            {
                var f = freshList[i];
                if (!existingById.TryGetValue(f.ChapterId, out var existing))
                {
                    // 若 ChapterId 对不上，退回按章节号匹配（兼容整章替换）
                    existing = currentList.FirstOrDefault(c =>
                        c.ChapterNumber == f.ChapterNumber && !keptIds.Contains(c.ChapterId));
                }

                if (existing != null)
                {
                    if (existing.ChapterNumber != f.ChapterNumber) structureChanged = true;
                    if (currentList.IndexOf(existing) != i) structureChanged = true;

                    existing.ChapterNumber = f.ChapterNumber;
                    existing.Title = f.Title;

                    // 正文与梗概只在"用户没在改这一章"时才跟磁盘走
                    if (protectedChapters == null || !protectedChapters.Contains(existing))
                    {
                        existing.Content = f.Content;
                        existing.Summary = f.Summary;
                        existing.ModifiedDate = f.ModifiedDate;
                        existing.LastModified = f.LastModified;
                    }
                    keptIds.Add(existing.ChapterId);
                    updatedOrder.Add(existing);
                }
                else
                {
                    structureChanged = true;
                    keptIds.Add(f.ChapterId);
                    updatedOrder.Add(f);
                }
            }

            // 移除已被删除章节的打开标签页
            var removedTabs = EditorTabControl.Items.OfType<TabItem>()
                .Where(t => t.Tag is Chapter c && !keptIds.Contains(c.ChapterId))
                .ToList();
            foreach (var tab in removedTabs)
            {
                EditorTabControl.Items.Remove(tab);
            }

            if (structureChanged)
            {
                currentList.Clear();
                currentList.AddRange(updatedOrder);
            }

            return structureChanged;
        }

        private void ScrollEditorToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var box = FindContentTextBox(EditorTabControl);
                if (box != null)
                {
                    box.CaretIndex = box.Text.Length;
                    box.ScrollToEnd();
                }
            }), DispatcherPriority.Loaded);
        }

        private static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool TryMapSettingSection(string? field, out SettingSection section)
        {
            section = SettingSection.FullOutline;
            if (string.IsNullOrWhiteSpace(field)) return false;

            switch (field.Trim().ToLowerInvariant())
            {
                case "full_outline" or "outline":
                    section = SettingSection.FullOutline;
                    return true;
                case "chapter_outline":
                    section = SettingSection.ChapterOutline;
                    return true;
                case "characters" or "character":
                    section = SettingSection.CharacterSettings;
                    return true;
                case "background":
                    section = SettingSection.BackgroundSettings;
                    return true;
                case "writing_style" or "write_style" or "style":
                    section = SettingSection.WritingStyle;
                    return true;
                case "viewpoint" or "narrative_viewpoint":
                    section = SettingSection.NarrativeViewpoint;
                    return true;
                default:
                    return false;
            }
        }
    }
}
