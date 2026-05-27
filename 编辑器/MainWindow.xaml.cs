using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
    public partial class MainWindow : Window
    {
        private NovelProject? _currentProject;
        private string _projectsPath = null!;
        private string _configDir = null!;
        private static readonly string _pathsConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw", "paths.json");
        private IApiService? _apiService;
        private ApiProfileManager _profileManager = null!;
        private AppearanceManager _appearanceManager = null!;
        private ProjectSnapshotManager? _snapshotManager;
        private ChatLogger? _chatLogger;
        private AiMemoryManager? _memoryManager;
        private readonly DispatcherTimer _notificationTimer = new();
        private CancellationTokenSource? _aiCts;
        private Action<int, int>? _tokenProgress;

        // AI 面板分离
        private AiPanelControl _aiPanel = null!;
        private FloatingAiWindow? _floatingWindow;
        private bool _isDetached;
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
                NotificationBorder.Visibility = Visibility.Collapsed;
                NotificationBorder.Height = 0;
                _notificationTimer.Stop();
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
            _aiPanel.OnCopyResult = CopyAiResult;
            _aiPanel.OnApplyToContext = ApplyToContext;
            _aiPanel.OnSaveMemory = SaveMemory;
            _aiPanel.OnClearMemory = ClearMemory;
            _aiPanel.OnStopAi = StopAi;
        }

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
                _apiService = KnownProviders.UsesAnthropicFormat(active.Provider)
                    ? new AnthropicService(active)
                    : new OpenAIService(active);
                UpdateStatus($"API: {_profileManager.ActiveProfileName} ({active.Provider} / {active.Model})");
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
                    _snapshotManager = new ProjectSnapshotManager(_currentProject.FilePath);
                    _chatLogger = new ChatLogger(_currentProject.FilePath);
                    _memoryManager = new AiMemoryManager(_currentProject.FilePath);
                    _memoryManager.Load();
                    _aiPanel.MemoryTextBox.Text = _memoryManager.GetRawMemory();
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
                    _snapshotManager = new ProjectSnapshotManager(_currentProject.FilePath);
                    _chatLogger = new ChatLogger(_currentProject.FilePath);
                    _memoryManager = new AiMemoryManager(_currentProject.FilePath);
                    _memoryManager.Load();
                    _aiPanel.MemoryTextBox.Text = _memoryManager.GetRawMemory();
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
        private void ExportPdf_Click(object sender, RoutedEventArgs e) => ShowComingSoon("导出PDF");
        private void ExportTxt_Click(object sender, RoutedEventArgs e) => ShowComingSoon("导出TXT");
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
            var context = BuildAiContext();
            var requirement = _aiPanel.InputTextBox.Text.Trim();
            var direction = string.IsNullOrEmpty(requirement) ? "" : $"\n\n续写要求：{requirement}";
            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.ContinueWritingAsync(context + chapter.Content + direction, ct: _aiCts!.Token, onProgress: _tokenProgress));
            if (result != null)
            {
                chapter.Content += "\n\n" + result.Text;
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("续写", requirement, result.Text);
                RecordAiInteraction("续写", requirement, result.Text);
                ShowNotification("续写完成，已追加到章节");
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
            var context = BuildAiContext();
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

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.PolishTextAsync(context + chapter.Content, combinedStyle, _aiCts!.Token, _tokenProgress));
            if (result != null)
            {
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
            var context = BuildAiContext();
            var input = _aiPanel.InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input)) input = "请生成适合小说风格的中文人名";
            var prompt = $"{context}请为小说生成角色名字。要求：{input}\n\n返回一组适合的名字，每个名字附简短说明。";

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(prompt, new CompletionOptions { CancellationToken = _aiCts!.Token, OnProgress = _tokenProgress }));
            if (result != null)
            {
                chapter.Content += $"\n\n【生成的角色名 — {DateTime.Now:HH:mm}】\n{result.Text}\n";
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("人名生成", input, result.Text);
                RecordAiInteraction("人名生成", input, result.Text);
                ShowNotification("人名生成完成，已追加到章节");
            }
            return result;
        }

        private async Task<AiResult?> ChatAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input))
            {
                MessageBox.Show("请在输入框中输入你想让AI写的内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var chapter = GetSelectedChapter();
            var context = BuildAiContext();
            var prompt = $"{context}{input}";

            if (chapter != null)
                TakeSnapshot("万能写作前备份");

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(prompt, new CompletionOptions { CancellationToken = _aiCts!.Token, OnProgress = _tokenProgress }));
            if (result != null)
            {
                if (chapter != null)
                {
                    chapter.Content += $"\n\n{result.Text}\n";
                    ShowNotification("已按输入生成内容并追加到章节");
                }
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("万能聊天", input, result.Text);
                RecordAiInteraction("万能聊天", input, result.Text);
            }
            return result;
        }

        // ---- 快速生成上下文 ----

        private async Task<AiResult?> GenOutlineAsync()
        {
            var input = _aiPanel.InputTextBox.Text.Trim();
            var chapter = GetSelectedChapter();
            var existingContext = BuildAiContext();

            // 新建大纲时弹出规模选择
            string? scaleHint = null;
            string existingOutline = _aiPanel.OutlineTextBox.Text.Trim();
            if (string.IsNullOrEmpty(existingOutline))
            {
                var scaleDialog = new NovelScaleDialog { Owner = this };
                if (scaleDialog.ShowDialog() == true)
                    scaleHint = scaleDialog.ScaleDescription;
            }

            string prompt;
            if (!string.IsNullOrEmpty(existingOutline))
            {
                prompt = $"以下是已有的全文大纲：\n\n{existingOutline}\n\n" +
                         $"请在此基础上扩展、完善或修改该大纲。{(string.IsNullOrEmpty(input) ? "" : $"要求：{input}")}\n" +
                         "请直接输出修改后的完整大纲，不要额外说明。";
            }
            else
            {
                var chapterContent = chapter?.Content ?? "";
                var topicHint = !string.IsNullOrEmpty(chapterContent)
                    ? $"以下是当前章节内容，请据此生成全文大纲：\n\n{chapterContent}\n\n"
                    : "";
                var scaleLine = !string.IsNullOrEmpty(scaleHint)
                    ? $"小说规模：{scaleHint}\n请根据此规模合理规划章节数量、情节复杂度和人物数量。\n\n"
                    : "";
                prompt = $"{existingContext}{topicHint}{scaleLine}" +
                         $"请为这部小说生成一份详细的全文大纲，包含主要情节线、冲突、转折点和结局。" +
                         $"{(string.IsNullOrEmpty(input) ? "" : $"额外要求：{input}")}\n" +
                         "请直接输出大纲内容，不要额外说明。";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000, CancellationToken = _aiCts!.Token, OnProgress = _tokenProgress }));
            if (result != null)
            {
                _aiPanel.OutlineTextBox.Text = result.Text;
                _aiPanel.OutlineExpander.IsExpanded = true;
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
            var existingContext = BuildAiContext();

            string existing = _aiPanel.CharacterTextBox.Text.Trim();
            string prompt;
            if (!string.IsNullOrEmpty(existing))
            {
                prompt = $"以下是已有的人物设定：\n\n{existing}\n\n" +
                         $"请在此基础上扩展、完善或添加新人物。{(string.IsNullOrEmpty(input) ? "" : $"要求：{input}")}\n" +
                         "请直接输出完整的人物设定，不要额外说明。";
            }
            else
            {
                prompt = $"{existingContext}" +
                         $"请为这部小说生成主要角色设定，每个角色包含：姓名、年龄、性别、外貌、性格、背景故事、角色定位。" +
                         $"{(string.IsNullOrEmpty(input) ? "" : $"额外要求：{input}")}\n" +
                         "请直接输出人物设定，不要额外说明。";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000, CancellationToken = _aiCts!.Token, OnProgress = _tokenProgress }));
            if (result != null)
            {
                _aiPanel.CharacterTextBox.Text = result.Text;
                _aiPanel.CharacterExpander.IsExpanded = true;
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
            var existingContext = BuildAiContext();

            string existing = _aiPanel.BackgroundTextBox.Text.Trim();
            string prompt;
            if (!string.IsNullOrEmpty(existing))
            {
                prompt = $"以下是已有的背景设定：\n\n{existing}\n\n" +
                         $"请在此基础上扩展、完善。{(string.IsNullOrEmpty(input) ? "" : $"要求：{input}")}\n" +
                         "请直接输出完整的背景设定，不要额外说明。";
            }
            else
            {
                prompt = $"{existingContext}" +
                         $"请为这部小说生成详细的世界观和背景设定，包含：时代背景、地理环境、社会体系、特殊设定（如魔法/科技体系）等。" +
                         $"{(string.IsNullOrEmpty(input) ? "" : $"额外要求：{input}")}\n" +
                         "请直接输出背景设定，不要额外说明。";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000, CancellationToken = _aiCts!.Token, OnProgress = _tokenProgress }));
            if (result != null)
            {
                _aiPanel.BackgroundTextBox.Text = result.Text;
                _aiPanel.BackgroundExpander.IsExpanded = true;
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
            var existingContext = BuildAiContext();
            var chapter = GetSelectedChapter();

            string existing = _aiPanel.ChapterTextBox.Text.Trim();
            string prompt;
            if (!string.IsNullOrEmpty(existing))
            {
                prompt = $"以下是已有的章节大纲：\n\n{existing}\n\n" +
                         $"请在此基础上扩展、完善。{(string.IsNullOrEmpty(input) ? "" : $"要求：{input}")}\n" +
                         "请直接输出完整的章节大纲，不要额外说明。";
            }
            else
            {
                var chapterHint = chapter != null
                    ? $"当前章节「{chapter.Title}」的内容：\n{chapter.Content}\n\n"
                    : "";
                prompt = $"{existingContext}{chapterHint}" +
                         $"请为这部小说生成详细的章节大纲，列出每一章的标题、主要事件和推进方向。" +
                         $"{(string.IsNullOrEmpty(input) ? "" : $"额外要求：{input}")}\n" +
                         "请直接输出章节大纲，不要额外说明。";
            }

            var result = await CallAiFunctionWithResult(async (api) => await api.CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 2000, CancellationToken = _aiCts!.Token, OnProgress = _tokenProgress }));
            if (result != null)
            {
                _aiPanel.ChapterTextBox.Text = result.Text;
                _aiPanel.ChapterExpander.IsExpanded = true;
                _aiPanel.ResultTextBox.Text = result.Text;
                _chatLogger?.Log("生成章节大纲", input, result.Text);
                RecordAiInteraction("生成章节大纲", input, result.Text);
                ShowNotification("已生成章节大纲");
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

            var targets = new (string Label, TextBox Box, Expander Expander)[]
            {
                ("全文大纲", _aiPanel.OutlineTextBox, _aiPanel.OutlineExpander),
                ("章节大纲", _aiPanel.ChapterTextBox, _aiPanel.ChapterExpander),
                ("主要人物设定", _aiPanel.CharacterTextBox, _aiPanel.CharacterExpander),
                ("主要背景设定", _aiPanel.BackgroundTextBox, _aiPanel.BackgroundExpander),
                ("文风设定", _aiPanel.WritingStyleTextBox, _aiPanel.WritingStyleExpander),
            };

            foreach (var (label, box, expander) in targets)
            {
                var item = new System.Windows.Controls.MenuItem { Header = label };
                item.Click += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(box.Text.Trim()))
                    {
                        var answer = MessageBox.Show($"「{label}」已有内容，是否覆盖？", "确认",
                            MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes) return;
                    }
                    box.Text = text;
                    expander.IsExpanded = true;
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
            _floatingWindow = new FloatingAiWindow();
            _floatingWindow.Owner = this;
            _floatingWindow.SetContent(_aiPanel);
            _floatingWindow.ClosingRequested += AttachAiPanel;
            _floatingWindow.Closed += (_, _) => { _floatingWindow = null; };

            // 收起主窗口的 AI 面板列
            AiPanelColumn.Width = new GridLength(0);

            _isDetached = true;
            _aiPanel.SetDetached(true);
            _floatingWindow.Show();
        }

        private void AttachAiPanel()
        {
            if (!_isDetached) return;

            // 从浮动窗口移除面板
            _floatingWindow?.ClearContent();
            _floatingWindow?.Close();
            _floatingWindow = null;

            // 放回主窗口
            AiPanelHost.Content = _aiPanel;
            AiPanelColumn.Width = new GridLength(300);

            _isDetached = false;
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

            // 背景图片
            if (!string.IsNullOrEmpty(config.BackgroundImagePath) && File.Exists(config.BackgroundImagePath))
            {
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage(new Uri(config.BackgroundImagePath));
                    MainContentGrid.Background = new System.Windows.Media.ImageBrush(img)
                    {
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                        Opacity = 0.35
                    };
                    // 面板半透明，透出背景图
                    ProjectPanelBorder.Background = preset.GetSemiTransparentPanelBg(0.88);
                    AiPanelBorder.Background = preset.GetSemiTransparentPanelBg(0.88);
                }
                catch
                {
                    MainContentGrid.Background = preset.WindowBgBrush;
                    ProjectPanelBorder.Background = preset.PanelBgBrush;
                    AiPanelBorder.Background = preset.PanelBgBrush;
                }
            }
            else
            {
                MainContentGrid.Background = preset.WindowBgBrush;
                ProjectPanelBorder.Background = preset.PanelBgBrush;
                AiPanelBorder.Background = preset.PanelBgBrush;
            }

            // 菜单栏
            MainMenu.Background = preset.MenuBgBrush;

            // 状态栏
            MainStatusBar.Background = preset.StatusBarBgBrush;

            // 编辑区背景（DynamicResource，实时影响 TabControl 和模板内 TextBox）
            Resources["EditorBgBrush"] = preset.EditorBgBrush;

            // 编辑器标签页容器背景（TabControl 内容区未覆盖时）
            EditorTabControl.Background = preset.EditorBgBrush;

            // 边框颜色
            ProjectPanelBorder.BorderBrush = preset.BorderColorBrush;
            AiPanelBorder.BorderBrush = preset.BorderColorBrush;
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
        }

        private void ShowApiSettings()
        {
            var dialog = new ApiSettingsWindow(this, _profileManager);
            if (dialog.ShowDialog() == true)
            {
                RefreshProfileSwitcher();
            }
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
            _aiPanel.SetStopButtonVisible(true);
            try
            {
                UpdateStatus("正在调用AI...");
                var aiResult = await function(_apiService);
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
                _aiPanel.SetStopButtonVisible(false);
            }
        }

        private void ShowTokenUsage(AiResult result)
        {
            if (result.TotalTokens > 0)
                TokenUsageTextBlock.Text = $"Token: 输入 {result.InputTokens} + 输出 {result.OutputTokens} = {result.TotalTokens}";
        }

        private void StopAi()
        {
            _aiCts?.Cancel();
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

        // AI 创作上下文管理
        private void SyncAiContextToProject()
        {
            if (_currentProject == null) return;
            _currentProject.FullOutline = _aiPanel.OutlineTextBox.Text;
            _currentProject.ChapterOutline = _aiPanel.ChapterTextBox.Text;
            _currentProject.CharacterSettings = _aiPanel.CharacterTextBox.Text;
            _currentProject.BackgroundSettings = _aiPanel.BackgroundTextBox.Text;
            _currentProject.WritingStyle = _aiPanel.WritingStyleTextBox.Text;
        }

        private void SyncAiContextFromProject()
        {
            if (_currentProject == null) return;
            _aiPanel.OutlineTextBox.Text = _currentProject.FullOutline ?? "";
            _aiPanel.ChapterTextBox.Text = _currentProject.ChapterOutline ?? "";
            _aiPanel.CharacterTextBox.Text = _currentProject.CharacterSettings ?? "";
            _aiPanel.BackgroundTextBox.Text = _currentProject.BackgroundSettings ?? "";
            _aiPanel.WritingStyleTextBox.Text = _currentProject.WritingStyle ?? "";
        }

        private string BuildAiContext()
        {
            SyncAiContextToProject();
            if (_currentProject == null) return "";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_currentProject.FullOutline))
                parts.Add($"[全文大纲]\n{_currentProject.FullOutline}");
            if (!string.IsNullOrWhiteSpace(_currentProject.ChapterOutline))
                parts.Add($"[章节大纲]\n{_currentProject.ChapterOutline}");
            if (!string.IsNullOrWhiteSpace(_currentProject.CharacterSettings))
                parts.Add($"[主要人物设定]\n{_currentProject.CharacterSettings}");
            if (!string.IsNullOrWhiteSpace(_currentProject.BackgroundSettings))
                parts.Add($"[主要背景设定]\n{_currentProject.BackgroundSettings}");
            if (!string.IsNullOrWhiteSpace(_currentProject.WritingStyle))
                parts.Add($"[文风设定]\n{_currentProject.WritingStyle}");

            if (parts.Count == 0 && (_memoryManager == null || string.IsNullOrWhiteSpace(_memoryManager.GetRawMemory())))
                return "";

            var context = "===== 创作上下文（项目设定） =====\n\n"
                + string.Join("\n\n---\n\n", parts)
                + "\n\n====================================\n\n";

            // 注入选中章节内容
            var selectedIds = _aiPanel.GetSelectedChapterIds();
            if (selectedIds.Count > 0)
            {
                var chapterParts = new List<string>();
                foreach (var ch in _currentProject.Chapters.Where(c => selectedIds.Contains(c.ChapterId)))
                {
                    if (!string.IsNullOrWhiteSpace(ch.Content))
                        chapterParts.Add($"[第{ch.ChapterNumber}章: {ch.Title}]\n{ch.Content}");
                }
                if (chapterParts.Count > 0)
                {
                    context += "===== 相关章节内容 =====\n\n"
                        + string.Join("\n\n---\n\n", chapterParts)
                        + "\n\n==========================\n\n";
                }
            }

            // 注入 AI 记忆
            if (_memoryManager != null)
                context += _memoryManager.GetMemoryContext();

            return context;
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
                var aiResult = await _apiService.CompleteTextAsync(prompt, new CompletionOptions { MaxTokens = 1500 });
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
