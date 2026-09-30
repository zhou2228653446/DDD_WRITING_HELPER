using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using 编辑器.Services;

namespace 编辑器
{
    public partial class MainWindow : HandyControl.Controls.Window
    {
        private NovelProject? _currentProject;
        private string _projectsPath = null!;
        private string _configDir = null!;
        private static readonly string _pathsConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw", "paths.json");
        private IApiService? _apiService;
        private ApiProfileManager _profileManager = null!;
        private AppearanceManager _appearanceManager = null!;
        private SystemPromptStore _promptStore = null!;
        private List<NovelSkill> _skills = new();
        private NovelSkill? _activeSkill;
        private ProjectSnapshotManager? _snapshotManager;
        private ChatLogger? _chatLogger;

        /// <summary>「万能聊天」的多轮对话记忆（按项目）。换项目时整体替换。</summary>
        private ChatSessionStore? _chatSession;

        private AiMemoryManager? _memoryManager;
        private readonly DispatcherTimer _notificationTimer = new();
        private CancellationTokenSource? _aiCts;
        private Action<int, int>? _tokenProgress;

        /// <summary>
        /// 阶段性提示（重试中、思考中）。接到状态栏上 —— 没有它的话，
        /// 断线重试的十几秒里界面完全静止，用户只会以为卡死了。
        /// </summary>
        private Action<string>? _aiNotice;

        // AI 面板分离
        private AiPanelControl _aiPanel = null!;
        private FloatingAiWindow? _floatingWindow;
        private bool _isDetached;

        /// <summary>「设定」窗口（全文大纲 / 人物设定等贯穿全书的设定）。关掉后置 null。</summary>
        private SettingsWindow? _settingsWindow;
        private SettingsBookWindow? _settingsBookWindow;
        private LiteratureWindow? _literatureWindow;
        private static readonly string _settingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw", "settings.json");

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public MainWindow()
        {
            InitializeComponent();
            _notificationTimer.Interval = TimeSpan.FromSeconds(3);
            _notificationTimer.Tick += (_, _) =>
            {
                _notificationTimer.Stop();
                // 淡出后再真正收起，避免通知条"啪"地消失
                Motion.PlayFadeOut(NotificationBorder, () =>
                {
                    NotificationBorder.Visibility = Visibility.Collapsed;
                    NotificationBorder.Height = 0;
                });
            };
            InitializeData();
            Loaded += WelcomeDialog_DeferIfNeeded;
            Closing += MainWindow_Closing;
        }

        private void InitializeData()
        {
            // 加载自定义路径配置
            var paths = LoadPathsConfig();
            _projectsPath = paths.ProjectsDirectory;
            _configDir = paths.ConfigDirectory;

            Directory.CreateDirectory(_projectsPath);
            Directory.CreateDirectory(_configDir);

            // 初始化面板宽度（此时控件树已完全初始化）
            ProjectPanelColumn.Width = new GridLength(250);
            AiPanelColumn.Width = new GridLength(300);

            // 手动连接视图面板的Checked/Unchecked事件，避免XAML解析时事件过早触发导致空引用
            ProjectManagerMenuItem.Checked += ProjectManager_Checked;
            ProjectManagerMenuItem.Unchecked += ProjectManager_Unchecked;
            AiPanelMenuItem.Checked += AiPanel_Checked;
            AiPanelMenuItem.Unchecked += AiPanel_Unchecked;

            // 初始化 AI 面板
            _aiPanel = new AiPanelControl();
            _aiPanel.Initialize(_configDir);
            _aiPanel.DetachRequested += ToggleDetachAiPanel;
            WireUpAiPanelCallbacks();
            AiPanelHost.Content = _aiPanel;

            // 初始化外观管理并应用保存的主题
            _appearanceManager = new AppearanceManager(_configDir);
            ApplyAppearance(_appearanceManager.Load());

            // 初始化配置方案管理器
            _profileManager = new ApiProfileManager(Path.Combine(_configDir, "api_profiles.json"));
            _profileManager.Load();
            _profileManager.ProfilesChanged += OnProfilesChanged;
            _profileManager.ActiveProfileChanged += OnActiveProfileChanged;

            // 初始化系统提示词（用户覆写层）。必须在任何 AI 调用之前注入，
            // 否则首次生成会用内置默认而不是用户改过的文本。
            InitPromptStore();

            RefreshProfileSwitcher();
            ApplyActiveProfile();

            UpdateStatus("就绪");
        }

        private void WireUpAiPanelCallbacks()
        {
            _aiPanel.OnContinueWriting = () => ContinueWritingAsync();
            _aiPanel.OnPolishText = () => PolishTextAsync();
            _aiPanel.OnGenerateName = () => GenerateNameAsync();
            _aiPanel.OnChat = () => ChatAsync();
            _aiPanel.OnGenOutline = () => GenOutlineAsync();
            _aiPanel.OnGenCharacter = () => GenCharacterAsync();
            _aiPanel.OnGenBackground = () => GenBackgroundAsync();
            _aiPanel.OnGenChapterOutline = () => GenChapterOutlineAsync();
            _aiPanel.OnGenWritingStyle = () => GenWritingStyleAsync();
            _aiPanel.OnCopyResult = CopyAiResult;
            _aiPanel.OnApplyToContext = ApplyToContext;
            _aiPanel.OnSaveMemory = SaveMemory;
            _aiPanel.OnClearMemory = ClearMemory;
            _aiPanel.OnClearChat = ClearChatSession;
            _aiPanel.OnStopAi = StopAi;
            _aiPanel.OnOpenSettings = ShowSettingsWindow;
            InitSkills();
        }

        /// <summary>
        /// 接线 AI 面板的方案与技能下拉，并做首次填充。
        /// 方案切换 → 提示词整套换 + 技能下拉按方案过滤并恢复该方案记住的技能。
        /// </summary>
        private void InitSkills()
        {
            _aiPanel.SkillChanged += OnSkillChanged;
            _aiPanel.PresetChanged += OnPresetChanged;

            RefreshPresetIndicator();
            RefreshSkills();
        }

        private sealed class SkillOption
        {
            public NovelSkill? Skill { get; }
            public string Display => Skill?.Name ?? "无技能";
            public SkillOption(NovelSkill? skill) => Skill = skill;
        }

        /// <summary>程序性刷新技能下拉时压住写回——否则刷新会被当成用户改选，把该方案的记忆覆盖掉。</summary>
        private bool _suppressSkillWriteback;

        private void OnSkillChanged()
        {
            _activeSkill = (_aiPanel.SkillCombo.SelectedItem as SkillOption)?.Skill;
            _aiPanel.SetSkillHint(_activeSkill == null
                ? "选择技能后，对应功能的生成会用该手法指令（与当前方案叠加）"
                : (_activeSkill.Description + (_activeSkill.InputHint.Length > 0 ? " " + _activeSkill.InputHint : "")));

            // 把选择记到「当前方案」名下：切走再切回来，技能还是这个
            if (!_suppressSkillWriteback && _promptStore != null)
            {
                var id = _activeSkill?.Id;
                if (_promptStore.GetSkillFor(_promptStore.ActivePresetId) != id)
                {
                    _promptStore.SetSkillFor(_promptStore.ActivePresetId, id);
                    try { _promptStore.Save(); } catch { /* 落盘失败不打断选技能 */ }
                }
            }
        }

        /// <summary>用户在 AI 面板头部切换提示词方案：方案与技能一起切。</summary>
        private void OnPresetChanged(string presetId)
        {
            if (_promptStore == null || presetId == _promptStore.ActivePresetId) return;

            _promptStore.ActivePresetId = presetId;
            try { _promptStore.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"保存提示词方案失败：{ex.Message}", "AI 助手",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RefreshPresetIndicator();
            RefreshSkills();
        }

        /// <summary>技能命中的任务提示词，否则回退默认任务说明。</summary>
        private string ResolveTaskText(string taskKey, string fallback) =>
            NovelSkillStore.ResolveTaskPrompt(_activeSkill, taskKey, fallback);

        /// <summary>技能命中的输出契约，否则回退默认契约。</summary>
        private string? ResolveContractText(string taskKey, string? fallback) =>
            NovelSkillStore.ResolveContract(_activeSkill, taskKey, fallback);

        private void WelcomeDialog_DeferIfNeeded(object? sender, EventArgs e)
        {
            ShowWelcomeIfNeeded();
            Loaded -= WelcomeDialog_DeferIfNeeded;
        }

        private void ShowWelcomeIfNeeded()
        {
            try
            {
                if (File.Exists(_settingsFile))
                {
                    var json = File.ReadAllText(_settingsFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("SkipWelcome", out var v) && v.GetBoolean())
                        return;
                }
            }
            catch { }

            var dialog = new WelcomeDialog(this);
            if (dialog.ShowDialog() == true && dialog.SkipWelcome)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_settingsFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_settingsFile,
                        JsonSerializer.Serialize(new { SkipWelcome = true }, _jsonOptions));
                }
                catch { }
            }
        }

        private void RefreshProfileSwitcher()
        {
            var current = ProfileSwitcher.SelectedItem as string;
            ProfileSwitcher.ItemsSource = _profileManager.GetProfileNames();
            if (current != null && _profileManager.GetProfileNames().Contains(current))
                ProfileSwitcher.SelectedItem = current;
            else
                ProfileSwitcher.SelectedItem = _profileManager.ActiveProfileName;
        }

        private void ApplyActiveProfile()
        {
            var active = _profileManager.ActiveProfile;
            if (active != null && !string.IsNullOrWhiteSpace(active.ApiKey))
            {
                // 协议与认证方式都交给 ApiProviders 判定，这里不再关心是 OpenAI 还是
                // Anthropic 格式、Key 放在哪个头里。
                _apiService = ApiProviders.CreateService(active);

                var preset = ApiProviders.Find(active.Provider);
                UpdateStatus($"API: {_profileManager.ActiveProfileName}"
                             + $"（{preset?.Name ?? "自定义"} / {active.Model}）");
            }
            else
            {
                _apiService = null;
            }
        }

        private void OnProfilesChanged()
        {
            RefreshProfileSwitcher();
        }

        private void OnActiveProfileChanged()
        {
            ApplyActiveProfile();
            Dispatcher.Invoke(() => RefreshProfileSwitcher());
        }

        private void ProfileSwitcher_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ProfileSwitcher.SelectedItem is string name && name != _profileManager.ActiveProfileName)
            {
                _profileManager.SetActive(name);
            }
        }

        private Chapter? GetSelectedChapter()
        {
            if (EditorTabControl.SelectedItem is TabItem tabItem)
                return tabItem.Content as Chapter;
            return EditorTabControl.SelectedItem as Chapter;
        }

        // 菜单事件处理
        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "TdxClaw项目文件 (*.tdxproj)|*.tdxproj",
                    InitialDirectory = _projectsPath
                };

                if (dialog.ShowDialog() == true)
                {
                    _currentProject = new NovelProject
                    {
                        ProjectName = Path.GetFileNameWithoutExtension(dialog.FileName),
                        FilePath = dialog.FileName,
                        CreatedDate = DateTime.Now,
                        Chapters = new List<Chapter>()
                    };

                    SaveProject();
                    AttachProjectServices();
                    RefreshProjectView();
                    SyncAiContextFromProject();
                    RefreshSnapshotList();
                    UpdateStatus("新建项目成功");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"创建项目失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "TdxClaw项目文件 (*.tdxproj)|*.tdxproj",
                    InitialDirectory = _projectsPath
                };

                if (dialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(dialog.FileName);
                    _currentProject = JsonSerializer.Deserialize<NovelProject>(json, _jsonOptions);
                    if (_currentProject == null)
                    {
                        MessageBox.Show("项目文件格式错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    _currentProject.FilePath = dialog.FileName;

                    // 清除所有已打开的标签页
                    EditorTabControl.Items.Clear();
                    AttachProjectServices();
                    RefreshProjectView();
                    SyncAiContextFromProject();
                    RefreshSnapshotList();
                    UpdateStatus($"打开项目: {_currentProject.ProjectName}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开项目失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先创建或打开项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                SaveProject();
                UpdateStatus("项目已保存");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存项目失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveProject()
        {
            SyncAiContextToProject();
            _currentProject!.ModifiedDate = DateTime.Now;
            var json = JsonSerializer.Serialize(_currentProject, _jsonOptions);
            File.WriteAllText(_currentProject.FilePath, json);
        }

        private void NewChapter_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先创建或打开项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var chapter = new Chapter
            {
                Title = $"新章节 {_currentProject.Chapters.Count + 1}",
                Content = "",
                ChapterNumber = _currentProject.Chapters.Count + 1,
                CreatedDate = DateTime.Now,
                LastModified = DateTime.Now,
                ModifiedDate = DateTime.Now
            };

            _currentProject.Chapters.Add(chapter);
            RefreshProjectView();
            // 直接为新章节打开编辑标签页
            OpenChapterTab(chapter);
            UpdateStatus("新建章节成功");
        }

        private void SaveChapter_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapter();
            if (chapter != null)
            {
                var now = DateTime.Now;
                chapter.LastModified = now;
                chapter.ModifiedDate = now;
                if (_currentProject != null)
                {
                    _currentProject.ModifiedDate = now;
                    SaveProject();
                }
                UpdateStatus($"已保存章节: {chapter.Title}");
            }
        }

        // ---- 章节管理 ----

        private Chapter? GetSelectedChapterFromTree()
        {
            return ProjectTreeView.SelectedItem as Chapter;
        }

        private void DeleteChapter_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapterFromTree();
            if (chapter == null || _currentProject == null)
            {
                ShowNotification("请先选中要删除的章节", isError: true);
                return;
            }

            var result = MessageBox.Show($"确定删除章节「{chapter.Title}」吗？\n此操作不可撤销。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _currentProject.Chapters.Remove(chapter);
            SaveProject();

            // 关闭对应标签页
            var tab = EditorTabControl.Items.OfType<TabItem>()
                .FirstOrDefault(t => t.Tag is Chapter c && c.ChapterId == chapter.ChapterId);
            if (tab != null) EditorTabControl.Items.Remove(tab);

            RefreshProjectView();
            UpdateStatus($"已删除章节: {chapter.Title}");
        }

        private void RenameChapter_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapterFromTree();
            if (chapter == null)
            {
                ShowNotification("请先选中要重命名的章节", isError: true);
                return;
            }
            RenameChapter(chapter);
        }

        private void RenameChapter(Chapter chapter)
        {
            var newName = ShowInputDialog("重命名章节", "请输入新的章节名称：", chapter.Title);
            if (!string.IsNullOrWhiteSpace(newName) && newName != chapter.Title)
            {
                chapter.Title = newName;
                chapter.ModifiedDate = DateTime.Now;
                if (_currentProject != null)
                {
                    SaveProject();
                    _aiPanel.RefreshChapterList(_currentProject.Chapters);
                }
                UpdateChapterInfo(chapter);
                UpdateStatus($"已重命名章节: {newName}");
            }
        }

        private void MoveChapterUp_Click(object sender, RoutedEventArgs e) => MoveChapter(-1);
        private void MoveChapterDown_Click(object sender, RoutedEventArgs e) => MoveChapter(1);

        private void MoveChapterMenu_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapterFromTree();
            if (chapter == null || _currentProject == null)
            {
                ShowNotification("请先选中要排序的章节", isError: true);
                return;
            }

            var btn = sender as System.Windows.Controls.Button;
            var menu = new System.Windows.Controls.ContextMenu();

            var upItem = new System.Windows.Controls.MenuItem { Header = "上移" };
            upItem.Click += (_, _) => MoveChapter(-1);
            menu.Items.Add(upItem);

            var downItem = new System.Windows.Controls.MenuItem { Header = "下移" };
            downItem.Click += (_, _) => MoveChapter(1);
            menu.Items.Add(downItem);

            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void MoveChapter(int direction)
        {
            var chapter = GetSelectedChapterFromTree();
            if (chapter == null || _currentProject == null) return;

            var index = _currentProject.Chapters.IndexOf(chapter);
            var newIndex = index + direction;
            if (newIndex < 0 || newIndex >= _currentProject.Chapters.Count) return;

            _currentProject.Chapters.RemoveAt(index);
            _currentProject.Chapters.Insert(newIndex, chapter);

            // 更新章节编号
            for (int i = 0; i < _currentProject.Chapters.Count; i++)
                _currentProject.Chapters[i].ChapterNumber = i + 1;

            SaveProject();
            RefreshProjectView();
            UpdateStatus($"「{chapter.Title}」已{(direction < 0 ? "上移" : "下移")}");
        }

        private void ExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null)
            {
                ShowNotification("请先创建或打开项目", isError: true);
                return;
            }

            // 先同步上下文到项目
            SyncAiContextToProject();

            var dialog = new SaveFileDialog
            {
                Filter = "Word 文档 (*.docx)|*.docx",
                FileName = $"{_currentProject.ProjectName}.docx",
                InitialDirectory = _projectsPath
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    WordExportService.Export(dialog.FileName, _currentProject);
                    ShowNotification($"已导出为 Word：{Path.GetFileName(dialog.FileName)}");

                    // 询问是否打开
                    var open = MessageBox.Show("导出成功！是否立即打开文件？", "导出完成",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (open == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = dialog.FileName,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void ExportPdf_Click(object sender, RoutedEventArgs e) =>
            ExportProjectFile("PDF 文档 (*.pdf)|*.pdf", ".pdf", "PDF",
                (path, project) => PdfExportService.Export(path, project));

        private void ExportTxt_Click(object sender, RoutedEventArgs e) =>
            ExportProjectFile("文本文件 (*.txt)|*.txt", ".txt", "TXT",
                (path, project) => TxtExportService.Export(path, project));

        /// <summary>导出通用流程：校验项目 → 同步 AI 上下文 → 选路径 → 写文件 → 询问是否打开</summary>
        private void ExportProjectFile(string filter, string extension, string displayName,
            Action<string, NovelProject> exporter)
        {
            if (_currentProject == null)
            {
                ShowNotification("请先创建或打开项目", isError: true);
                return;
            }

            // 先同步上下文到项目
            SyncAiContextToProject();

            var project = _currentProject;

            var dialog = new SaveFileDialog
            {
                Filter = filter,
                FileName = $"{SanitizeFileName(project.ProjectName)}{extension}",
                InitialDirectory = _projectsPath
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                exporter(dialog.FileName, project);
                ShowNotification($"已导出为 {displayName}：{Path.GetFileName(dialog.FileName)}");

                // 询问是否打开
                var open = MessageBox.Show("导出成功！是否立即打开文件？", "导出完成",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dialog.FileName,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>剔除文件名中的非法字符，避免项目名含 : / 等字符导致保存失败</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "未命名";

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return cleaned.Length == 0 ? "未命名" : cleaned;
        }
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_currentProject == null) return;

            var result = MessageBox.Show(
                $"是否保存项目「{_currentProject.ProjectName}」的更改？",
                "退出确认",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    SaveProject();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Cancel = true;
                }
            }
        }

        // 菜单快捷入口（委托给 AiPanelControl 回调）
        private async void ContinueWriting_Click(object sender, RoutedEventArgs e) => await ContinueWritingAsync();
        private async void PolishText_Click(object sender, RoutedEventArgs e) => await PolishTextAsync();
        private async void GenerateName_Click(object sender, RoutedEventArgs e) => await GenerateNameAsync();
        private async void Chat_Click(object sender, RoutedEventArgs e) => await ChatAsync();

        // AI功能（由 AiPanelControl 回调触发）
        private async Task<AiResult?> ContinueWritingAsync()
        {
            var chapter = GetSelectedChapter();
            if (chapter == null)
            {
                MessageBox.Show("请先选择要编辑的章节", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (string.IsNullOrEmpty(chapter.Content))
            {
                MessageBox.Show("当前章节内容为空，请先写一些内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            TakeSnapshot("续写前备份");
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(AiPrompts.Keys.Continue, AiPrompts.Task.Continue));
            var requirement = _aiPanel.InputTextBox.Text.Trim();

            var userPrompt = AiPrompts.Section($"待续写的正文（第{chapter.ChapterNumber}章 {chapter.Title}）", chapter.Content);
            if (!string.IsNullOrEmpty(requirement))
                userPrompt += $"\n{requirement}";

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(userPrompt, systemPrompt,
                    new CompletionOptions
                    {
                        MaxTokens = 2000,
                        CancellationToken = _aiCts!.Token,
                        OnNotice = _aiNotice,
                        OnProgress = _tokenProgress
                    }));
            // ★ 用户点「停止」时，Service 会把**已经生成的部分**原样带回来
            //   （见 AiResult.CanceledWithPartial）。续写是**追加**，把已写的接上去
            //   不会破坏任何既有内容 —— 而用户按停止的意图通常正是"就写到这里"。
            //   所以这里接受取消态的部分内容。（覆盖型的润色**不能**这样，见 PolishTextAsync。）
            if (result != null && (result.IsUsable || result.CanceledWithPartial))
            {
                chapter.Content += "\n\n" + result.Text;
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("续写", requirement, result.Text);
                RecordAiInteraction("续写", requirement, result.Text);
                ShowNotification(result.CanceledWithPartial
                    ? "已停止，保留的部分已追加到章节"
                    : "续写完成，已追加到章节");
            }
            return result;
        }

        private async Task<AiResult?> PolishTextAsync()
        {
            var chapter = GetSelectedChapter();
            if (chapter == null)
            {
                MessageBox.Show("请先选择要编辑的章节", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (string.IsNullOrEmpty(chapter.Content))
            {
                MessageBox.Show("当前章节内容为空，请先写一些内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            TakeSnapshot("润色前备份");
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(AiPrompts.Keys.Polish, AiPrompts.Task.Polish));
            var requirement = _aiPanel.InputTextBox.Text.Trim();

            string? presetStyle = null;
            if (_aiPanel.StyleToggle.IsChecked == true)
            {
                var styleText = _aiPanel.StyleTextBox.Text?.Trim();
                if (!string.IsNullOrEmpty(styleText))
                    presetStyle = styleText;
            }

            string? combinedStyle;
            if (presetStyle != null && !string.IsNullOrEmpty(requirement))
                combinedStyle = $"{presetStyle}，{requirement}";
            else if (presetStyle != null)
                combinedStyle = presetStyle;
            else if (!string.IsNullOrEmpty(requirement))
                combinedStyle = requirement;
            else
                combinedStyle = null;

            var userPrompt = AiPrompts.Section($"待润色的正文（第{chapter.ChapterNumber}章 {chapter.Title}）", chapter.Content);
            userPrompt += string.IsNullOrEmpty(combinedStyle)
                ? "\n请润色这段正文。"
                : $"\n请按以下要求润色：{combinedStyle}";

            // ★ 输出上限按原文长度给。润色是**整章覆盖**（见下 result → chapter.Content），
            //   写死 1000 时，一份 3000 字的章节会被拦腰截断，然后拿这半截去覆盖全文 ——
            //   这是会丢内容的，靠"润色前备份"快照捞回来属于补救而不是设计。
            //   续写/人名/聊天都是追加，短一点只是少写几句，不构成数据损失，故不在此列。
            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(userPrompt, systemPrompt,
                    new CompletionOptions
                    {
                        MaxTokens = CompletionOptions.BudgetForRewrite(chapter.Content),
                        CancellationToken = _aiCts!.Token,
                        OnNotice = _aiNotice,
                        OnProgress = _tokenProgress
                    }));
            if (IsUsable(result))
            {
                if (result.Truncated)
                {
                    // 服务端明确回了 finish_reason=length：正文不完整，**不覆盖原文**。
                    // 结果留在回复框里，用户想用可以自己复制；原文保持不动。
                    _aiPanel.ResultTextBox.Text = result.Text;
                    _chatLogger?.Log("润色(被截断未替换)", combinedStyle ?? "默认", result.Text);
                    MessageBox.Show(
                        "这次润色被输出上限截断了，**没有替换原文**（已保留原标题内容）。\n\n"
                        + "结果已放在右侧「AI 回复」框里，可以自行复制取用。\n"
                        + "建议：整章润色改成按段落做，或先把章节拆分后再润色。",
                        "润色结果不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return result;
                }

                chapter.Content = result.Text;
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("润色", combinedStyle ?? "默认", result.Text);
                RecordAiInteraction("润色", combinedStyle ?? "", result.Text);
                ShowNotification("润色完成，已替换章节内容");
            }
            return result;
        }

        private async Task<AiResult?> GenerateNameAsync()
        {
            var chapter = GetSelectedChapter();
            if (chapter == null)
            {
                MessageBox.Show("请先选择要编辑的章节", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            TakeSnapshot("人名生成前备份");
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(AiPrompts.Keys.Name, AiPrompts.Task.Name),
                ResolveContractText(AiPrompts.Keys.Name, AiPrompts.StructuredOutput));
            var input = _aiPanel.InputTextBox.Text.Trim();
            var userPrompt = string.IsNullOrEmpty(input)
                ? "请为这部小说生成一批角色名字。"
                : $"请为这部小说生成一批角色名字。要求：{input}";

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(userPrompt, systemPrompt,
                    new CompletionOptions { CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress }));
            if (IsUsable(result))
            {
                chapter.Content += $"\n\n【生成的角色名 — {DateTime.Now:HH:mm}】\n{result.Text}\n";
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("人名生成", input, result.Text);
                RecordAiInteraction("人名生成", input, result.Text);
                ShowNotification("人名生成完成，已追加到章节");
            }
            return result;
        }

        /// <summary>
        /// 万能聊天。与其余功能最大的区别：**它是连续对话** —— 会把此前聊过的轮次
        /// 一并带上，AI 因此记得你上一句说了什么。
        ///
        /// 关于"上下文"的两点事实（容易误解，写在这免得又当成 bug 去查）：
        /// 1. 大模型 API 是**无状态**的，服务端不记得上一句话。所谓"连续对话"，
        ///    就是客户端每次请求都把完整历史重发一遍 —— 每次都要"重读"是协议决定的，
        ///    不可避免；真正会出错的是**压根没带历史**（本方法此前就是这样，
        ///    所以每问一句都等于开了一个全新对话）。
        /// 2. 当前章节正文每轮都带，且只带一份（历史里存的是用户的原始输入，
        ///    不含正文包装）—— 这样既保证 AI 看到的是最新章节，又不会在
        ///    上下文里出现两份正文。你改完正文再问，AI 用的是新的。
        /// </summary>
        private async Task<AiResult?> ChatAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input))
            {
                MessageBox.Show("请在输入框中输入你想让AI写的内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var chapter = GetSelectedChapter();
            var baseSystemPrompt = BuildSystemPrompt(
                ResolveTaskText(AiPrompts.Keys.Chat, AiPrompts.Task.Chat),
                ResolveContractText(AiPrompts.Keys.Chat, AiPrompts.StructuredOutput));

            // 当前章节正文作为"待处理文本"放进 user 侧；设定类内容已在 system 里
            var userPrompt = chapter != null && !string.IsNullOrWhiteSpace(chapter.Content)
                ? AiPrompts.Section($"当前章节（第{chapter.ChapterNumber}章 {chapter.Title}）", chapter.Content) + "\n" + input
                : input;

            if (chapter != null)
                TakeSnapshot("万能写作前备份");

            var result = await CallAiFunctionWithResult(async (apiService) =>
            {
                // ★ 发之前先过一遍上下文预算，装不下就压缩：先本地省略旧回复（免费），
                //   再把更早的对话交给模型写成摘要（一次调用）。压缩会更新 _chatSession
                //   里的摘要，所以摘要块必须在**压缩之后**才拼进 system —— 否则用的是旧摘要。
                var history = await EnsureChatContextBudgetAsync(
                    baseSystemPrompt, userPrompt, _aiCts?.Token ?? CancellationToken.None);

                var systemPrompt = AppendChatSummaryBlock(baseSystemPrompt);

                return await apiService.CompleteTextAsync(userPrompt, systemPrompt,
                    new CompletionOptions { CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress },
                    history);
            });
            if (IsUsable(result))
            {
                if (chapter != null)
                {
                    chapter.Content += $"\n\n{result.Text}\n";
                    ShowNotification("已按输入生成内容并追加到章节");
                }
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("万能聊天", input, result.Text);
                RecordAiInteraction("万能聊天", input, result.Text);

                // ★ 记进历史的是**用户的原始输入**，不是包了正文的 userPrompt ——
                //   正文每轮都会重新带一份，历史里再存一份等于白烧一遍 token，
                //   而且章节改过之后，历史里那份旧正文还会跟新正文打架。
                _chatSession?.Add(input, result.Text);

                // 记下服务端报回的**真实**输入 token：下一轮做预算判断时，
                // 「上轮真实输入 + 本轮新增内容的估算」比纯本地估算准得多
                //（对应 ZCode 的 provider_usage 优先策略）。
                if (_compactor != null)
                    _compactor.LastRequestInputTokens = result.InputTokens > 0 ? result.InputTokens : null;

                RefreshChatMemoryInfo();
            }
            return result;
        }

        // ---- 快速生成上下文 ----

        private async Task<AiResult?> GenOutlineAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();
            var chapter = GetSelectedChapter();

            // 新建大纲时弹出规模选择
            string? scaleHint = null;
            string existingOutline = GetSettingValue(SettingSection.FullOutline).Trim();
            if (string.IsNullOrEmpty(existingOutline))
            {
                var scaleDialog = new NovelScaleDialog { Owner = this };
                if (scaleDialog.ShowDialog() == true)
                    scaleHint = scaleDialog.ScaleDescription;
            }

            var isExpand = !string.IsNullOrEmpty(existingOutline);
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.Outline,
                    isExpand ? AiPrompts.Task.Expand : AiPrompts.Task.Outline),
                ResolveContractText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.Outline,
                    AiPrompts.StructuredOutput));

            string userPrompt;
            if (isExpand)
            {
                userPrompt = AiPrompts.Section("已有的全文大纲", existingOutline);
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n修改要求：{input}";
            }
            else
            {
                userPrompt = "";
                if (chapter != null && !string.IsNullOrWhiteSpace(chapter.Content))
                    userPrompt += AiPrompts.Section("当前章节内容", chapter.Content) + "\n";
                if (!string.IsNullOrEmpty(scaleHint))
                    userPrompt += $"小说规模：{scaleHint}\n请据此合理规划章节数量、情节复杂度与人物数量。\n\n";
                userPrompt += "请为这部小说生成一份详细的全文大纲。";
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n额外要求：{input}";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(userPrompt, systemPrompt, new CompletionOptions { MaxTokens = 2000, Temperature = 0.5, CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress }));
            if (IsUsable(result))
            {
                SetSettingValue(SettingSection.FullOutline, result.Text);
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("生成大纲", input, result.Text);
                RecordAiInteraction("生成大纲", input, result.Text);
                ShowNotification("已生成全文大纲");
            }
            return result;
        }

        private async Task<AiResult?> GenCharacterAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();

            string existing = GetSettingValue(SettingSection.CharacterSettings).Trim();
            var isExpand = !string.IsNullOrEmpty(existing);
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.Character,
                    isExpand ? AiPrompts.Task.Expand : AiPrompts.Task.Character),
                ResolveContractText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.Character,
                    AiPrompts.StructuredOutput));

            string userPrompt;
            if (isExpand)
            {
                userPrompt = AiPrompts.Section("已有的人物设定", existing);
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n修改要求：{input}";
            }
            else
            {
                userPrompt = "请为这部小说生成主要角色设定。";
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n额外要求：{input}";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(userPrompt, systemPrompt, new CompletionOptions { MaxTokens = 2000, Temperature = 0.5, CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress }));
            if (IsUsable(result))
            {
                SetSettingValue(SettingSection.CharacterSettings, result.Text);
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("生成人物", input, result.Text);
                RecordAiInteraction("生成人物", input, result.Text);
                ShowNotification("已生成人物设定");
            }
            return result;
        }

        private async Task<AiResult?> GenBackgroundAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();

            string existing = GetSettingValue(SettingSection.BackgroundSettings).Trim();
            var isExpand = !string.IsNullOrEmpty(existing);
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.Background,
                    isExpand ? AiPrompts.Task.Expand : AiPrompts.Task.Background),
                ResolveContractText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.Background,
                    AiPrompts.StructuredOutput));

            string userPrompt;
            if (isExpand)
            {
                userPrompt = AiPrompts.Section("已有的背景设定", existing);
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n修改要求：{input}";
            }
            else
            {
                userPrompt = "请为这部小说生成世界观和背景设定。";
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n额外要求：{input}";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(userPrompt, systemPrompt, new CompletionOptions { MaxTokens = 2000, Temperature = 0.4, CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress }));
            if (IsUsable(result))
            {
                SetSettingValue(SettingSection.BackgroundSettings, result.Text);
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("生成背景", input, result.Text);
                RecordAiInteraction("生成背景", input, result.Text);
                ShowNotification("已生成背景设定");
            }
            return result;
        }

        private async Task<AiResult?> GenChapterOutlineAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();
            var chapter = GetSelectedChapter();

            string existing = GetSettingValue(SettingSection.ChapterOutline).Trim();
            var isExpand = !string.IsNullOrEmpty(existing);
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.ChapterOutline,
                    isExpand ? AiPrompts.Task.Expand : AiPrompts.Task.ChapterOutline),
                ResolveContractText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.ChapterOutline,
                    AiPrompts.StructuredOutput));

            string userPrompt;
            if (isExpand)
            {
                userPrompt = AiPrompts.Section("已有的章节大纲", existing);
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n修改要求：{input}";
            }
            else
            {
                userPrompt = "";
                if (chapter != null && !string.IsNullOrWhiteSpace(chapter.Content))
                    userPrompt += AiPrompts.Section($"当前章节（第{chapter.ChapterNumber}章 {chapter.Title}）", chapter.Content) + "\n";
                userPrompt += "请为这部小说生成详细的章节大纲。";
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n额外要求：{input}";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(userPrompt, systemPrompt, new CompletionOptions { MaxTokens = 2000, Temperature = 0.5, CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress }));
            if (IsUsable(result))
            {
                SetSettingValue(SettingSection.ChapterOutline, result.Text);
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("生成章节大纲", input, result.Text);
                RecordAiInteraction("生成章节大纲", input, result.Text);
                ShowNotification("已生成章节大纲");
            }
            return result;
        }

        /// <summary>
        /// 生成文风设定。首次生成时优先拿作者已写的正文当样本，
        /// 从自己的文字里提炼风格，比凭空规定一种文风更贴合。
        /// </summary>
        private async Task<AiResult?> GenWritingStyleAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();

            string existing = GetSettingValue(SettingSection.WritingStyle).Trim();
            var isExpand = !string.IsNullOrEmpty(existing);
            var systemPrompt = BuildSystemPrompt(
                ResolveTaskText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.WriteStyle,
                    isExpand ? AiPrompts.Task.Expand : AiPrompts.Task.WriteStyle),
                ResolveContractText(isExpand ? AiPrompts.Keys.Expand : AiPrompts.Keys.WriteStyle,
                    AiPrompts.StructuredOutput));

            string userPrompt;
            if (isExpand)
            {
                userPrompt = AiPrompts.Section("已有的文风设定", existing);
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n修改要求：{input}";
            }
            else
            {
                userPrompt = "";
                var chapter = GetSelectedChapter();
                if (chapter != null && !string.IsNullOrWhiteSpace(chapter.Content))
                    userPrompt += AiPrompts.Section("作者已有的文字（风格样本）", chapter.Content) + "\n";
                userPrompt += "请据此提炼这部小说的文风设定。";
                if (!string.IsNullOrEmpty(input)) userPrompt += $"\n额外要求：{input}";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(userPrompt, systemPrompt, new CompletionOptions { MaxTokens = 1500, Temperature = 0.5, CancellationToken = _aiCts!.Token, OnNotice = _aiNotice, OnProgress = _tokenProgress }));
            if (IsUsable(result))
            {
                SetSettingValue(SettingSection.WritingStyle, result.Text);
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("生成文风", input, result.Text);
                RecordAiInteraction("生成文风", input, result.Text);
                ShowNotification("已生成文风设定");
            }
            return result;
        }

        // ---- AI 回复操作 ----

        private void CopyAiResult()
        {
            var text = _aiPanel.ResultTextBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                ShowNotification("AI 回复为空", isError: true);
                return;
            }
            System.Windows.Clipboard.SetText(text);
            ShowNotification("已复制到剪贴板");
        }

        private void ApplyToContext(System.Windows.Controls.Button btn)
        {
            var text = _aiPanel.ResultTextBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                ShowNotification("AI 回复为空，请先生成内容", isError: true);
                return;
            }

            var menu = new System.Windows.Controls.ContextMenu();

            var targets = new (string Label, SettingSection Kind)[]
            {
                ("全文大纲", SettingSection.FullOutline),
                ("章节大纲", SettingSection.ChapterOutline),
                ("主要人物设定", SettingSection.CharacterSettings),
                ("主要背景设定", SettingSection.BackgroundSettings),
                ("文风设定", SettingSection.WritingStyle),
            };

            foreach (var (label, kind) in targets)
            {
                var item = new System.Windows.Controls.MenuItem { Header = label };
                item.Click += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(GetSettingValue(kind).Trim()))
                    {
                        var answer = MessageBox.Show($"「{label}」已有内容，是否覆盖？", "确认",
                            MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes) return;
                    }
                    SetSettingValue(kind, text);
                    ShowNotification($"已应用到「{label}」");
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private async void ApiSettings_Click(object sender, RoutedEventArgs e)
        {
            ShowApiSettings();
        }

        private void ProjectManager_Checked(object sender, RoutedEventArgs e) => ProjectPanelColumn.Width = new GridLength(250);
        private void ProjectManager_Unchecked(object sender, RoutedEventArgs e) => ProjectPanelColumn.Width = new GridLength(0);
        private void AiPanel_Checked(object sender, RoutedEventArgs e)
        {
            if (_isDetached && _floatingWindow != null)
                _floatingWindow.Show();
            else
                AiPanelColumn.Width = new GridLength(300);
        }

        private void AiPanel_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isDetached && _floatingWindow != null)
                _floatingWindow.Hide();
            else
                AiPanelColumn.Width = new GridLength(0);
        }

        // ---- AI 面板分离/合并 ----

        private void ToggleDetachAiPanel()
        {
            if (_isDetached)
                AttachAiPanel();
            else
                DetachAiPanel();
        }

        private void DetachAiPanel()
        {
            if (_isDetached) return;

            // 从主窗口移除面板
            AiPanelHost.Content = null;

            // 创建浮动窗口并放入面板
            var win = new FloatingAiWindow { Owner = this };
            win.SetContent(_aiPanel);
            win.ClosingRequested += OnFloatingWindowClosing;
            win.Closed += OnFloatingWindowClosed;
            _floatingWindow = win;

            // 收起主窗口的 AI 面板列
            AiPanelColumn.Width = new GridLength(0);

            _isDetached = true;
            _aiPanel.SetDetached(true);
            win.Show();
        }

        /// <summary>
        /// 主动合并：关闭浮动窗口并把面板收回主窗口（面板上的「📌 合并」按钮）。
        /// </summary>
        private void AttachAiPanel()
        {
            if (!_isDetached) return;

            var win = _floatingWindow;
            if (win != null)
            {
                // Close() 会同步触发 Closing → ClosingRequested。必须先解绑，
                // 否则会递归回到本方法：第二次 Close() 落在「窗口关闭期间」，
                // WPF 的 Window.VerifyNotClosing() 会抛 InvalidOperationException。
                win.ClosingRequested -= OnFloatingWindowClosing;
                win.Closed -= OnFloatingWindowClosed;
                _floatingWindow = null;
                win.Close();
            }

            ReturnPanelToMainWindow();
        }

        /// <summary>
        /// 浮动窗口自身正在关闭（用户点标题栏 X / Alt+F4，或主窗口退出时连带关闭）。
        /// 此刻窗口处于 Closing 流程中，严禁再调用 Show／Close／改 Visibility，
        /// 否则同样会抛 InvalidOperationException。这里只负责把面板搬回主窗口。
        /// </summary>
        private void OnFloatingWindowClosing()
        {
            var win = _floatingWindow;
            if (win != null)
            {
                win.ClosingRequested -= OnFloatingWindowClosing;
                win.Closed -= OnFloatingWindowClosed;
                _floatingWindow = null;
            }

            ReturnPanelToMainWindow();
        }

        /// <summary>窗口关闭后的兜底：清理引用；若面板还没收回则一并收回（幂等）。</summary>
        private void OnFloatingWindowClosed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(_floatingWindow, sender))
                _floatingWindow = null;

            ReturnPanelToMainWindow();
        }

        /// <summary>把 AI 面板从浮动窗口摘出、挂回主窗口，并复位分离状态（幂等）。</summary>
        private void ReturnPanelToMainWindow()
        {
            if (!_isDetached) return;
            _isDetached = false;

            // 面板此刻的逻辑父级仍是浮动窗口的 HostGrid。
            // 不先摘除就赋给 AiPanelHost，会抛「元素已是另一个元素的子元素」。
            if (_aiPanel.Parent is Panel oldHost)
                oldHost.Children.Remove(_aiPanel);

            AiPanelHost.Content = _aiPanel;
            AiPanelColumn.Width = new GridLength(300);
            _aiPanel.SetDetached(false);
        }

        private void About_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TdxClaw AI写作助手 v1.0", "关于", MessageBoxButton.OK, MessageBoxImage.Information);

        // 其他事件
        private void ProjectTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (ProjectTreeView.SelectedItem is Chapter chapter)
                OpenChapterTab(chapter);
        }

        private void ProjectTreeView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 双击章节标题触发重命名
            if (ProjectTreeView.SelectedItem is Chapter chapter)
                RenameChapter(chapter);
        }

        private void OpenChapterTab(Chapter chapter)
        {
            var existingTab = EditorTabControl.Items.OfType<TabItem>()
                .FirstOrDefault(t => t.Tag is Chapter c && c.ChapterId == chapter.ChapterId);

            if (existingTab == null)
            {
                var headerBlock = new System.Windows.Controls.TextBlock();
                System.Windows.Data.BindingOperations.SetBinding(headerBlock,
                    System.Windows.Controls.TextBlock.TextProperty,
                    new System.Windows.Data.Binding("Title") { Source = chapter });

                var tabItem = new TabItem
                {
                    Header = headerBlock,
                    Content = chapter,
                    ContentTemplate = (System.Windows.DataTemplate)FindResource("ChapterEditorTemplate"),
                    Tag = chapter
                };
                EditorTabControl.Items.Add(tabItem);
                existingTab = tabItem;
            }

            EditorTabControl.SelectedItem = existingTab;
            UpdateChapterInfo(chapter);
        }

        private void EditorTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var chapter = GetSelectedChapter();
            if (chapter != null)
            {
                UpdateChapterInfo(chapter);
                UpdateWordCount(chapter.Content);
            }
        }

        // 辅助方法
        private void AppearanceSettings_Click(object sender, RoutedEventArgs e)
        {
            var current = _appearanceManager.Load();
            var dialog = new AppearanceSettingsWindow(this, _appearanceManager, current);
            if (dialog.ShowDialog() == true)
            {
                ApplyAppearance(dialog.Result);
                _appearanceManager.Save(dialog.Result);
            }
        }

        private void ApplyAppearance(AppearanceConfig config)
        {
            var preset = AppearanceManager.GetPreset(config.PresetName)
                ?? AppearanceManager.BuiltInPresets[0];

            // 1) 统一注入设计 token —— 颜色全部写到全局资源字典顶层，
            //    XAML 侧一律用 DynamicResource 消费，一处生效全局（含浮动 AI 面板窗口）。
            //    这同时修掉了旧实现的两个 bug：TextColor / SplitterBg 从未被应用。
            ThemeTokens.Apply(preset);

            // 2) 背景图（可选）：图片铺满内容区，两侧面板改为半透明以透出图片
            var hasImage = !string.IsNullOrEmpty(config.BackgroundImagePath)
                           && File.Exists(config.BackgroundImagePath);

            if (hasImage)
            {
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage(new Uri(config.BackgroundImagePath!));
                    MainContentGrid.Background = new System.Windows.Media.ImageBrush(img)
                    {
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                        Opacity = 0.35
                    };
                    ProjectPanelBorder.Background = preset.GetSemiTransparentPanelBg(0.88);
                    AiPanelBorder.Background = preset.GetSemiTransparentPanelBg(0.88);
                    return;
                }
                catch
                {
                    // 图片损坏/路径失效：静默回落到底色，不让外观设置整块失败
                }
            }

            // 恢复为 token 引用（不能用 ClearValue —— XAML 里设的值本身就是本地值，会被一起清掉）
            // MainContentGrid 恢复成透明：底纹由画布层提供，内容区不再盖一层实色
            MainContentGrid.Background = System.Windows.Media.Brushes.Transparent;
            ProjectPanelBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
            AiPanelBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
        }

        // ---- 路径配置 ----

        private PathsConfig LoadPathsConfig()
        {
            try
            {
                if (File.Exists(_pathsConfigFile))
                {
                    var json = File.ReadAllText(_pathsConfigFile);
                    var cfg = JsonSerializer.Deserialize<PathsConfig>(json, _jsonOptions);
                    if (cfg != null)
                    {
                        var projects = cfg.ProjectsDirectory;
                        var config = cfg.ConfigDirectory;
                        if (!string.IsNullOrWhiteSpace(projects)) Directory.CreateDirectory(projects);
                        if (!string.IsNullOrWhiteSpace(config)) Directory.CreateDirectory(config);
                        return new PathsConfig
                        {
                            ProjectsDirectory = !string.IsNullOrWhiteSpace(projects) ? projects : GetDefaultProjectsDir(),
                            ConfigDirectory = !string.IsNullOrWhiteSpace(config) ? config : GetDefaultConfigDir()
                        };
                    }
                }
            }
            catch { }

            return new PathsConfig
            {
                ProjectsDirectory = GetDefaultProjectsDir(),
                ConfigDirectory = GetDefaultConfigDir()
            };
        }

        private static void SavePathsConfig(PathsConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(_pathsConfigFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_pathsConfigFile, JsonSerializer.Serialize(config, _jsonOptions));
            }
            catch { }
        }

        private static string GetDefaultProjectsDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TdxClaw", "Projects");

        private static string GetDefaultConfigDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");

        private void PathSettings_Click(object sender, RoutedEventArgs e)
        {
            var current = new PathsConfig
            {
                ProjectsDirectory = _projectsPath,
                ConfigDirectory = _configDir
            };

            var dialog = new PathSettingsWindow(this, current);
            if (dialog.ShowDialog() == true)
            {
                var result = dialog.Result;
                SavePathsConfig(result);

                // 先保存当前项目
                if (_currentProject != null) SaveProject();

                // 重新初始化路径
                _projectsPath = result.ProjectsDirectory ?? GetDefaultProjectsDir();
                _configDir = result.ConfigDirectory ?? GetDefaultConfigDir();
                Directory.CreateDirectory(_projectsPath);
                Directory.CreateDirectory(_configDir);

                // 重新加载配置
                ReloadConfigurations();

                ShowNotification("路径已更新");
            }
        }

        private void ReloadConfigurations()
        {
            // 重新加载润色预设
            _aiPanel.LoadPolishPresets();

            // 重新加载外观
            _appearanceManager = new AppearanceManager(_configDir);
            ApplyAppearance(_appearanceManager.Load());

            // 重新加载 API 配置方案
            _profileManager = new ApiProfileManager(Path.Combine(_configDir, "api_profiles.json"));
            _profileManager.Load();
            _profileManager.ProfilesChanged += OnProfilesChanged;
            _profileManager.ActiveProfileChanged += OnActiveProfileChanged;
            RefreshProfileSwitcher();
            ApplyActiveProfile();

            // 配置目录变了，提示词覆写也要从新目录重读
            InitPromptStore();
        }

        private void ShowApiSettings()
        {
            var dialog = new ApiSettingsWindow(this, _profileManager, _promptStore);
            if (dialog.ShowDialog() == true)
            {
                RefreshProfileSwitcher();
                RefreshPresetIndicator();   // 提示词方案可能被切换/改名/删除了
            }
            RefreshSkills();    // 技能保存即落盘，关闭后重载（含用户可能只点了右上角 X）
        }

        /// <summary>
        /// 重载技能列表到 AI 面板下拉（设置页关闭后 / 方案切换后调用）。
        /// 只列出**适用当前方案**的技能（<see cref="NovelSkill.Presets"/> 空 = 全方案可用），
        /// 并恢复该方案记住的技能；记住的技能已不适用（用户改了归属）时清掉这条失效记忆。
        /// </summary>
        private void RefreshSkills()
        {
            _skills = NovelSkillStore.Load(_configDir);
            var presetId = AiPrompts.ActivePresetId;
            var remembered = _promptStore?.GetSkillFor(presetId);

            var combo = _aiPanel.SkillCombo;
            var options = new List<SkillOption> { new(null) };
            options.AddRange(NovelSkillStore.AvailableFor(_skills, presetId).Select(s => new SkillOption(s)));

            var target = options.FirstOrDefault(o => o.Skill?.Id == remembered) ?? options[0];

            _suppressSkillWriteback = true;
            try
            {
                combo.ItemsSource = options;
                combo.DisplayMemberPath = nameof(SkillOption.Display);
                combo.SelectedItem = target;
            }
            finally { _suppressSkillWriteback = false; }

            // 记忆指向的技能被过滤掉了 → 顺手清掉，别让脏数据一直留在 system_prompts.json 里
            if (_promptStore != null && remembered != null && target.Skill == null
                && _promptStore.GetSkillFor(presetId) != null)
            {
                _promptStore.SetSkillFor(presetId, null);
                try { _promptStore.Save(); } catch { }
            }
        }

        /// <summary>
        /// 加载用户的提示词覆写并注入 <see cref="AiPrompts"/>。
        /// AiPrompts 的每个属性都是实时的（每次调用现取覆写值），所以设置里改完
        /// 立即对下一次 AI 调用生效，不需要重启。
        /// </summary>
        private void InitPromptStore()
        {
            _promptStore = new SystemPromptStore(_configDir);
            _promptStore.Load();
            AiPrompts.Store = _promptStore;
            RefreshPresetIndicator();
            RefreshSkills();   // 技能下拉按方案过滤 + 恢复该方案记住的技能（首次初始化时 store 可能还没就绪）
        }

        /// <summary>
        /// 把当前生效的提示词方案刷到 AI 面板头上。启动、切换配置目录、
        /// 以及 AI 设置对话框保存后都要调一次——面板上看不到方案名时，
        /// 用户切换方案后会觉得"AI 突然不对劲"。
        /// </summary>
        private void RefreshPresetIndicator()
        {
            if (_promptStore == null) return;

            var id = _promptStore.ActivePresetId;
            _aiPanel.SetPresetOptions(AiPrompts.PresetOptions(_promptStore), id);
        }

        private async Task CallAiFunction(Func<IApiService, Task<AiResult>> function)
        {
            if (_apiService == null)
            {
                var result = MessageBox.Show("请先设置API Key", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (result == MessageBoxResult.OK)
                    ShowApiSettings();
                return;
            }

            _aiCts = new CancellationTokenSource();
            _aiPanel.SetStopButtonVisible(true);
            try
            {
                UpdateStatus("正在调用AI...");
                var aiResult = await function(_apiService);
                _aiPanel.ResultTextBox.Text = aiResult.Text;
                ShowTokenUsage(aiResult);
                UpdateStatus("AI调用成功");
            }
            catch (OperationCanceledException)
            {
                _aiPanel.ResultTextBox.Text = "[已停止生成]";
                UpdateStatus("已停止");
            }
            catch (Exception ex)
            {
                _aiPanel.ResultTextBox.Text = $"AI调用失败: {ex.Message}";
                MessageBox.Show($"AI调用失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("AI调用失败");
            }
            finally
            {
                _aiCts.Dispose();
                _aiCts = null;
                _tokenProgress = null;
                _aiPanel.SetStopButtonVisible(false);
            }
        }

        private async Task<AiResult?> CallAiFunctionWithResult(Func<IApiService, Task<AiResult>> function)
        {
            if (_apiService == null)
            {
                var result = MessageBox.Show("请先设置API Key", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (result == MessageBoxResult.OK)
                    ShowApiSettings();
                return null;
            }

            _aiCts = new CancellationTokenSource();
            _tokenProgress = (input, output) =>
                Dispatcher.Invoke(() =>
                    TokenUsageTextBlock.Text = $"Token: 输入 {input} + 输出 {output} = {input + output}");
            _aiNotice = message => Dispatcher.Invoke(() => UpdateStatus(message));
            _aiPanel.SetStopButtonVisible(true);
            try
            {
                UpdateStatus("正在调用AI...");
                var aiResult = await function(_apiService);

                // Service 不抛异常，失败也包成 AiResult 返回 —— 在这里统一收口：
                // 让用户看得见原因，同时把它标成"不可写回"，免得调用方拿去覆盖正文。
                if (!aiResult.IsUsable)
                {
                    // ★ 用户点「停止」时，Service 会把**已经流式收到的内容**原样带回来
                    //   （见 AiResult.CanceledWithPartial）。那段内容在用户眼里就是
                    //   "已经写出来的东西"，拿一句「[已停止生成]」盖掉等于让他白等一场。
                    if (aiResult.CanceledWithPartial)
                    {
                        _aiPanel.ResultTextBox.Text = aiResult.Text;
                        ShowTokenUsage(aiResult);
                        UpdateStatus($"已停止 · 保留了本次已生成的 {aiResult.Text.Trim().Length} 字");
                        return aiResult;
                    }

                    _aiPanel.ResultTextBox.Text = aiResult.IsCanceled ? "[已停止生成]" : aiResult.Text;
                    UpdateStatus(aiResult.IsCanceled ? "已停止" : "AI调用失败");
                    if (!aiResult.IsCanceled)
                        MessageBox.Show(aiResult.Text, "AI 调用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return aiResult;
                }

                ShowTokenUsage(aiResult);
                return aiResult;
            }
            catch (OperationCanceledException)
            {
                _aiPanel.ResultTextBox.Text = "[已停止生成]";
                UpdateStatus("已停止");
                return new AiResult { Text = "[已停止生成]" };
            }
            catch (Exception ex)
            {
                _aiPanel.ResultTextBox.Text = $"AI调用失败: {ex.Message}";
                MessageBox.Show($"AI调用失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("AI调用失败");
                return null;
            }
            finally
            {
                _aiCts.Dispose();
                _aiCts = null;
                _tokenProgress = null;
                _aiNotice = null;
                _aiPanel.SetStopButtonVisible(false);
            }
        }

        /// <summary>
        /// 结果能不能拿来写回正文 / 设定。
        ///
        /// ★ 两个 Service 把**失败与取消**也包成 AiResult 返回（Text 里是"API调用失败: …"
        /// 这类文案），并不抛异常。所以"结果非 null"从来就不等于成功 —— 所有把
        /// result.Text 写回章节正文或设定的地方，都必须先过这一道；否则一次 401
        /// 就能把整章正文、整份大纲、整个人物设定换成一行错误说明，而且还要靠
        /// 版本历史去捞回来。
        /// </summary>
        /// <remarks>
        /// 带 <see cref="NotNullWhenAttribute"/> 是必要的：不加的话编译器无法从
        /// `if (IsUsable(result))` 推出 result 非空，9 处写回点会各报一个 CS8602。
        /// </remarks>
        private static bool IsUsable([NotNullWhen(true)] AiResult? result) => result != null && result.IsUsable;

        private void ShowTokenUsage(AiResult result)
        {
            if (result.TotalTokens <= 0) return;

            // 缓存命中部分单独标出来。它**已经包含在"输入"里**，但只按 0.1 倍计价 ——
            // 不显示的话，用户看到"输入 12000"会以为每次都这么贵，
            // 而实际上其中一大半可能来自命中缓存的系统提示词。
            var cache = result.CachedInputTokens > 0
                ? $"（其中缓存命中 {result.CachedInputTokens}）"
                : "";

            TokenUsageTextBlock.Text =
                $"Token: 输入 {result.InputTokens}{cache} + 输出 {result.OutputTokens} = {result.TotalTokens}";
        }

        private void StopAi()
        {
            _aiCts?.Cancel();
        }

        /// <summary>
        /// 挂接「随项目走」的服务：快照、对话日志、AI 记忆、对话记忆。
        ///
        /// 新建项目与打开项目两条路径都必须调它 —— 以前是各写一遍，
        /// 漏掉一处的后果是"打开的项目没有对话记忆"这类只在特定路径复现的怪问题。
        /// </summary>
        private void AttachProjectServices()
        {
            if (_currentProject == null) return;

            _snapshotManager = new ProjectSnapshotManager(_currentProject.FilePath);
            _chatLogger = new ChatLogger(_currentProject.FilePath);
            _memoryManager = new AiMemoryManager(_currentProject.FilePath);
            _memoryManager.Load();
            _aiPanel.MemoryTextBox.Text = _memoryManager.GetRawMemory();

            _chatSession = new ChatSessionStore(_currentProject.FilePath);

            // 压缩器挂在对话记忆上（阈值要按当前模型算，所以也依赖模型名）。
            // 它自己会在空项目/无服务时安全退化，不需要额外的判空。
            AttachCompactor();

            RefreshChatMemoryInfo();
        }

        /// <summary>把对话记忆的轮数刷到 AI 面板上（让用户看得见"它还记不记得"）。</summary>
        // RefreshChatMemoryInfo 见 MainWindow.ContextBudget.cs（连同上下文预算机制）

        private void ClearChatSession()
        {
            if (_chatSession == null || _chatSession.IsEmpty)
            {
                ShowNotification("当前没有对话记录");
                return;
            }

            var r = MessageBox.Show(
                $"确定清空与 AI 的对话记忆吗？（当前 {_chatSession.RoundCount} 轮）\n"
                + "清空后 AI 不再记得之前聊过的内容，已写进章节的正文不受影响。",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            _chatSession.Clear();
            RefreshChatMemoryInfo();
            ShowNotification("对话记忆已清空");
        }

        private void RefreshProjectView()
        {
            if (_currentProject != null)
            {
                ProjectTreeView.ItemsSource = null;
                ProjectTreeView.ItemsSource = new List<NovelProject> { _currentProject };
                _aiPanel.RefreshChapterList(_currentProject.Chapters);
            }
        }

        private void UpdateStatus(string status) => StatusTextBlock.Text = status;

        private void UpdateChapterInfo(Chapter chapter)
        {
            ChapterInfoTextBlock.Text = $"章节: {chapter.Title}";
        }

        private void UpdateWordCount(string? content)
        {
            var wordCount = string.IsNullOrEmpty(content) ? 0 : content.Length;
            WordCountTextBlock.Text = $"字数: {wordCount}";
        }

        private string? ShowInputDialog(string title, string message, string defaultValue = "")
        {
            var dialog = new InputDialog(title, message, defaultValue)
            {
                Owner = this
            };
            return dialog.ShowDialog() == true ? dialog.InputText : null;
        }

        // ==================== 设定窗口 ====================
        // 全文大纲 / 章节大纲 / 人物 / 背景 / 文风这五项已归属 SettingsWindow。
        // 下面两个方法名保持不变（散落各处的调用点无需改动），内部改为转发给窗口：
        //   写 → 把窗口里尚未落盘的内容推回项目（窗口内编辑已即时同步，这里是幂等兜底）
        //   读 → 让窗口重新载入项目内容（新建 / 打开 / 切换项目后调用）

        private void SyncAiContextToProject()
        {
            _settingsWindow?.SyncToProject(_currentProject);
        }

        private void SyncAiContextFromProject()
        {
            _settingsWindow?.LoadFrom(_currentProject);
        }

        /// <summary>打开（或前置）「设定」窗口。</summary>
        private void ShowSettingsWindow()
        {
            try
            {
                if (_settingsWindow == null)
                {
                    var win = new SettingsWindow { Owner = this };
                    win.OnRequestAi = RequestAiForSettingAsync;

                    // 关闭后只回收引用：此刻窗口已关，不能再碰窗口 API。
                    // 内容无损失 —— 窗口内编辑是即时写回项目的。
                    win.Closed += (_, _) => _settingsWindow = null;

                    win.LoadFrom(_currentProject);
                    _settingsWindow = win;
                    win.Show();
                }
                else
                {
                    // 已经开着：刷新数据并置前（可能刚切换过项目）
                    _settingsWindow.LoadFrom(_currentProject);
                    if (_settingsWindow.WindowState == WindowState.Minimized)
                        _settingsWindow.WindowState = WindowState.Normal;
                    _settingsWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开设定窗口失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsWindow_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

        // ==================== 设定集 ====================

        /// <summary>打开（或前置）「设定集」窗口。</summary>
        private void ShowSettingsBookWindow()
        {
            try
            {
                if (_settingsBookWindow == null)
                {
                    var win = new SettingsBookWindow { Owner = this };
                    win.OnRequestAi = GenerateSettingsBookChapterAsync;
                    win.ExportRequested += ExportSettingsBook;

                    // 关闭后只回收引用；内容无损失 —— 窗口内编辑是即时写回项目的
                    win.Closed += (_, _) => _settingsBookWindow = null;

                    win.LoadFrom(_currentProject);
                    _settingsBookWindow = win;
                    win.Show();
                }
                else
                {
                    _settingsBookWindow.LoadFrom(_currentProject);
                    if (_settingsBookWindow.WindowState == WindowState.Minimized)
                        _settingsBookWindow.WindowState = WindowState.Normal;
                    _settingsBookWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开设定集窗口失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SettingsBookWindow_Click(object sender, RoutedEventArgs e) => ShowSettingsBookWindow();

        // ------------------------------------------------------------------
        // 参考文献库
        // ------------------------------------------------------------------

        /// <summary>打开（或前置）「参考文献库」窗口。编辑即时写回项目，无关闭确认需求。</summary>
        private void ShowLiteratureWindow()
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先创建或打开项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                if (_literatureWindow == null)
                {
                    var win = new LiteratureWindow(_currentProject, IsAcademicPreset()) { Owner = this };
                    win.Closed += (_, _) => _literatureWindow = null;
                    _literatureWindow = win;
                    win.Show();
                }
                else
                {
                    if (_literatureWindow.WindowState == WindowState.Minimized)
                        _literatureWindow.WindowState = WindowState.Normal;
                    _literatureWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开参考文献库失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LiteratureWindow_Click(object sender, RoutedEventArgs e) => ShowLiteratureWindow();

        /// <summary>
        /// 当前生效方案是否「学术论文系」（内置论文，或基于论文的自定义方案）。
        /// 在线检索入口只在论文系方案下开放。
        /// </summary>
        private bool IsAcademicPreset()
        {
            if (_promptStore == null) return false;
            return _promptStore.ResolveBasePreset(_promptStore.ActivePresetId) == PromptPresets.IdAcademic;
        }

        /// <summary>
        /// 设定集窗口里点「AI 生成 / 完善」：为某一章生成（或补全）内容。
        /// 素材 = 项目设定块（5 项设定 + AI 记忆）+ 作品简介 + 该章已有内容；
        /// 章节主题任务说明来自模板（自定义章用通用说明）。
        /// </summary>
        private async Task GenerateSettingsBookChapterAsync(SettingsBookChapter chapter)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先创建或打开项目", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var tpl = SettingsBookTemplates.Find(chapter.SourceKey);
            string taskText = tpl?.AiTask ?? "把与本章主题相关的已有设定整理成条目式内容，忠于已有事实，"
                + "需要补全而素材未明说的内容标注【推断】。";

            string userPrompt = AiPrompts.Section($"目标章节", $"{chapter.Title}\n{taskText}");
            userPrompt += "\n" + AiPrompts.Section("作品简介", _currentProject.Description);
            userPrompt += "\n" + BuildProjectContext();

            if (!string.IsNullOrWhiteSpace(chapter.Content))
                userPrompt += "\n" + AiPrompts.Section("本章已有内容（在此基础上完善，不要推翻）", chapter.Content);

            userPrompt += "\n请直接输出这一章的成稿内容。";

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(
                userPrompt,
                BuildSystemPrompt(
                    ResolveTaskText(AiPrompts.Keys.SettingBook, AiPrompts.Task.SettingBook),
                    ResolveContractText(AiPrompts.Keys.SettingBook, AiPrompts.StructuredOutput)),
                new CompletionOptions
                {
                    MaxTokens = 3000,
                    Temperature = 0.5,
                    CancellationToken = _aiCts!.Token,
                    OnNotice = _aiNotice,
                    OnProgress = _tokenProgress
                }));

            if (IsUsable(result))
            {
                chapter.Content = result.Text;
                chapter.IsAiGenerated = true;
                chapter.ModifiedDate = DateTime.Now;
                _settingsBookWindow?.RefreshCurrent();
                _chatLogger?.Log("设定集·" + chapter.Title, "", result.Text);
                RecordAiInteraction("设定集·" + chapter.Title, "", result.Text);
                ShowNotification($"已生成「{chapter.Title}」");
            }
        }

        /// <summary>设定集窗口点「导出为 …」：选路径 → 写文件 → 询问是否打开。</summary>
        private void ExportSettingsBook(string kind)
        {
            if (_currentProject == null)
            {
                ShowNotification("请先创建或打开项目", isError: true);
                return;
            }

            SyncAiContextToProject();
            SettingsBookTemplates.EnsureBook(_currentProject);
            var project = _currentProject;

            string ext = kind switch { "PDF" => ".pdf", "TXT" => ".txt", _ => ".docx" };
            string filter = kind switch
            {
                "PDF" => "PDF 文档 (*.pdf)|*.pdf",
                "TXT" => "文本文件 (*.txt)|*.txt",
                _ => "Word 文档 (*.docx)|*.docx"
            };

            var dialog = new SaveFileDialog
            {
                Filter = filter,
                FileName = $"{SanitizeFileName(project.ProjectName)}_设定集{ext}",
                InitialDirectory = _projectsPath
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                switch (kind)
                {
                    case "PDF": SettingsBookExportService.ExportPdf(dialog.FileName, project); break;
                    case "TXT": SettingsBookExportService.ExportTxt(dialog.FileName, project); break;
                    default: SettingsBookExportService.ExportWord(dialog.FileName, project); break;
                }

                ShowNotification($"已导出设定集为 {kind}：{Path.GetFileName(dialog.FileName)}");
                var open = MessageBox.Show("导出成功！是否立即打开文件？", "导出完成",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dialog.FileName,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 设定窗口里点「AI 生成 / 完善」的入口。
        /// 复用 AI 面板那套既有生成流程（含 API 校验、进度反馈、记忆记录），
        /// 设定窗口本身不另起一套 AI 交互 —— 与 AI 交互的入口始终只有 AI 面板一个。
        /// </summary>
        private async Task RequestAiForSettingAsync(SettingSection kind)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先创建或打开项目", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            switch (kind)
            {
                case SettingSection.FullOutline: await GenOutlineAsync(); break;
                case SettingSection.ChapterOutline: await GenChapterOutlineAsync(); break;
                case SettingSection.CharacterSettings: await GenCharacterAsync(); break;
                case SettingSection.BackgroundSettings: await GenBackgroundAsync(); break;
                case SettingSection.WritingStyle: await GenWritingStyleAsync(); break;
            }
        }

        /// <summary>
        /// 写入某项设定：改项目数据 + 同步到已打开的设定窗口。
        /// 「AI 生成结果」与「应用到 ▷ 某项设定」都走这里，保证两条路径行为一致。
        /// </summary>
        private void SetSettingValue(SettingSection kind, string text)
        {
            if (_currentProject == null) return;

            switch (kind)
            {
                case SettingSection.FullOutline: _currentProject.FullOutline = text; break;
                case SettingSection.ChapterOutline: _currentProject.ChapterOutline = text; break;
                case SettingSection.CharacterSettings: _currentProject.CharacterSettings = text; break;
                case SettingSection.BackgroundSettings: _currentProject.BackgroundSettings = text; break;
                case SettingSection.WritingStyle: _currentProject.WritingStyle = text; break;
            }

            _settingsWindow?.SetValue(kind, text);
            // 窗口开着就切到该项，让作者直接看到刚生成的内容；没开则不打扰
            _settingsWindow?.NavigateTo(kind);
        }

        private string GetSettingValue(SettingSection kind)
        {
            if (_currentProject == null) return "";
            return kind switch
            {
                SettingSection.FullOutline => _currentProject.FullOutline ?? "",
                SettingSection.ChapterOutline => _currentProject.ChapterOutline ?? "",
                SettingSection.CharacterSettings => _currentProject.CharacterSettings ?? "",
                SettingSection.BackgroundSettings => _currentProject.BackgroundSettings ?? "",
                SettingSection.WritingStyle => _currentProject.WritingStyle ?? "",
                _ => ""
            };
        }

        /// <summary>
        /// 构建项目设定块 —— 这部分进 **system** 消息。
        ///
        /// 与正文分开的原因（见 AiPrompts 的注释）：设定是"参考资料"，
        /// 和"需要处理的文本"混在同一条 user 消息里，模型分不清该改哪段。
        /// </summary>
        private string BuildProjectContext()
        {
            SyncAiContextToProject();
            if (_currentProject == null) return "";

            return AiPrompts.BuildContextBlock(
                _currentProject.FullOutline,
                _currentProject.ChapterOutline,
                _currentProject.CharacterSettings,
                _currentProject.BackgroundSettings,
                _currentProject.WritingStyle,
                _memoryManager?.GetRawMemory());
        }

        /// <summary>
        /// 构建"相关章节"参考块 —— 作者在 AI 面板里勾选的其他章节，同样进 **system**。
        /// </summary>
        private string BuildRelatedChaptersContext()
        {
            if (_currentProject == null) return "";
            var selectedIds = _aiPanel.GetSelectedChapterIds();
            if (selectedIds.Count == 0) return "";

            var rows = _currentProject.Chapters
                .Where(c => selectedIds.Contains(c.ChapterId))
                .Select(c => (c.ChapterNumber, c.Title, c.Content ?? ""));
            return AiPrompts.BuildRelatedChapters(rows);
        }

        /// <summary>
        /// 组装完整的 system 提示词：身份 + 项目设定 + 参考章节 + 本次任务 + 输出契约。
        ///
        /// 返回**分段**结果而不是拼好的字符串：设定段跨请求基本不变（可打缓存断点），
        /// 勾选章节段随时可能变（不能打）。分段的意义与代价见 <see cref="AiPrompts.BuildSections"/>。
        /// </summary>
        private SystemPrompt BuildSystemPrompt(string task, string? outputContract = null)
        {
            var stable = BuildProjectContext();

            // 参考文献库非空时追加引用块：正文引 [n]、只能引列表内文献（防编造）。
            // 挂在这个单一收口点上，全部生成功能（含万能聊天）自动生效。
            var literature = _currentProject?.LiteratureLibrary;
            if (literature != null && literature.Count > 0)
                stable = (stable.Length > 0 ? stable + "\n\n" : "") + LiteratureFormatter.BuildContextBlock(literature);

            var related = BuildRelatedChaptersContext();
            return AiPrompts.BuildSections(task, stable, related, outputContract);
        }

        private void RecordAiInteraction(string type, string input, string result)
        {
            _memoryManager?.RecordInteraction(type, input, result);
            _ = TryUpdateMemoryAsync();
        }

        private async Task TryUpdateMemoryAsync()
        {
            if (_memoryManager == null || !_memoryManager.ShouldUpdateMemory() || _apiService == null)
                return;

            try
            {
                UpdateStatus("正在更新 AI 记忆...");
                var prompt = _memoryManager.BuildMemoryUpdatePrompt();
                var aiResult = await _apiService.CompleteTextAsync(prompt, null,
                    new CompletionOptions { MaxTokens = 1500 });
                if (!string.IsNullOrWhiteSpace(aiResult.Text) && !aiResult.Text.StartsWith("API调用失败") && !aiResult.Text.StartsWith("API错误"))
                {
                    _memoryManager.SaveMemory(aiResult.Text);
                    _aiPanel.MemoryTextBox.Text = _memoryManager.GetRawMemory();
                    UpdateStatus("AI 记忆已更新");
                }
                else
                {
                    UpdateStatus("就绪");
                }
            }
            catch
            {
                UpdateStatus("就绪");
            }
        }

        private void SaveMemory()
        {
            if (_memoryManager == null) return;
            _memoryManager.SetMemory(_aiPanel.MemoryTextBox.Text);
            ShowNotification("AI 记忆已保存");
        }

        private void ClearMemory()
        {
            if (_memoryManager == null) return;
            var result = MessageBox.Show("确定清空当前项目的所有 AI 记忆吗？\n此操作不可撤销。",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _memoryManager.ClearMemory();
            _aiPanel.MemoryTextBox.Text = "";
            ShowNotification("AI 记忆已清空");
        }

        private void ShowComingSoon(string feature)
        {
            MessageBox.Show($"{feature}功能即将推出", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---- 润色预设管理（已移至 AiPanelControl）----

        // ---- 窗口通知 ----

        private void ShowNotification(string message, bool isError = false)
        {
            NotificationBorder.Background = isError
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
            NotificationText.Text = message;
            NotificationBorder.Height = double.NaN; // auto
            NotificationBorder.Visibility = Visibility.Visible;
            Motion.PlayDropIn(NotificationBorder, -10);   // 自上而下滑入
            _notificationTimer.Stop();
            _notificationTimer.Start();
        }

        // ---- 快照管理 ----

        private void TakeSnapshot(string description)
        {
            if (_currentProject == null || _snapshotManager == null) return;
            SyncAiContextToProject();
            _snapshotManager.SaveSnapshot(_currentProject, description);
            RefreshSnapshotList();
        }

        private void RefreshSnapshotList()
        {
            var entries = _snapshotManager?.LoadIndex() ?? new List<SnapshotEntry>();
            // 时间倒序，最新在上
            entries.Reverse();
            SnapshotListBox.ItemsSource = entries;
            RestoreSnapshotBtn.IsEnabled = false;
        }

        private void ApplyProjectFromSnapshot(NovelProject snapshot)
        {
            if (_currentProject == null) return;

            var filePath = _currentProject.FilePath;
            _currentProject = snapshot;
            _currentProject.FilePath = filePath;

            // 刷新编辑器标签（关闭所有已有标签页）
            EditorTabControl.Items.Clear();

            // 刷新树形视图
            RefreshProjectView();
            SyncAiContextFromProject();

            // 刷新字数统计和章节信息
            var chapter = GetSelectedChapter();
            if (chapter != null)
            {
                UpdateChapterInfo(chapter);
                UpdateWordCount(chapter.Content);
            }

            ShowNotification("已恢复至选中版本");
        }

        private void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (SnapshotListBox.SelectedItem is not SnapshotEntry entry || _snapshotManager == null)
                return;

            var snapshot = _snapshotManager.LoadSnapshot(entry);
            if (snapshot == null)
            {
                MessageBox.Show("无法读取该版本快照，文件可能已被删除", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show($"确定恢复到 [{entry.Description}] ({entry.Timestamp:yyyy-MM-dd HH:mm:ss}) 时的版本？\n当前未保存的修改将丢失。",
                "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            ApplyProjectFromSnapshot(snapshot);
        }

        private void SnapshotListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RestoreSnapshot_Click(sender, e);
        }

        private void SnapshotListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RestoreSnapshotBtn.IsEnabled = SnapshotListBox.SelectedItem is SnapshotEntry;
        }
    }
}
