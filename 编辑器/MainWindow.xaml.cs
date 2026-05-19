using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
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
        private readonly DispatcherTimer _notificationTimer = new();

        private readonly ObservableCollection<string> _polishPresets = new();
        private static readonly string[] DefaultPolishPresets = { "正式严谨", "简洁干练", "优美文学", "口语化", "古风雅韵", "幽默风趣" };
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

            // 初始化润色风格预设
            LoadPolishPresets();

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
                _apiService = new OpenAIService(active);
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

        private void ExportWord_Click(object sender, RoutedEventArgs e) => ShowComingSoon("导出Word");
        private void ExportPdf_Click(object sender, RoutedEventArgs e) => ShowComingSoon("导出PDF");
        private void ExportTxt_Click(object sender, RoutedEventArgs e) => ShowComingSoon("导出TXT");
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        // AI功能
        private async void ContinueWriting_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapter();
            if (chapter != null && !string.IsNullOrEmpty(chapter.Content))
            {
                TakeSnapshot("续写前备份");
                var context = BuildAiContext();
                var requirement = AiInputTextBox.Text.Trim();
                var direction = string.IsNullOrEmpty(requirement) ? "" : $"\n\n续写要求：{requirement}";
                var result = await CallAiFunctionWithResult(async (apiService) =>
                    await apiService.ContinueWritingAsync(context + chapter.Content + direction));
                if (result != null)
                {
                    chapter.Content += "\n\n" + result;
                    AiResultTextBox.Text = result;
                    _chatLogger?.Log("续写", requirement, result);
                    ShowNotification("续写完成，已追加到章节");
                }
            }
            else if (chapter == null)
            {
                MessageBox.Show("请先选择要编辑的章节", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("当前章节内容为空，请先写一些内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void PolishText_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapter();
            if (chapter != null && !string.IsNullOrEmpty(chapter.Content))
            {
                TakeSnapshot("润色前备份");
                var context = BuildAiContext();

                // 读取用户额外要求
                var requirement = AiInputTextBox.Text.Trim();

                // 读取预设风格（受开关控制）
                string? presetStyle = null;
                if (PolishStyleToggle.IsChecked == true)
                {
                    var styleText = PolishStyleCombo.Text?.Trim();
                    if (styleText != "（无预设）" && !string.IsNullOrEmpty(styleText))
                        presetStyle = styleText;
                }

                // 组合风格：预设 + 用户要求
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
                    await apiService.PolishTextAsync(context + chapter.Content, combinedStyle));
                if (result != null)
                {
                    chapter.Content = result;
                    AiResultTextBox.Text = result;
                    _chatLogger?.Log("润色", combinedStyle ?? "默认", result);
                    ShowNotification("润色完成，已替换章节内容");
                }
            }
            else if (chapter == null)
            {
                MessageBox.Show("请先选择要编辑的章节", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("当前章节内容为空，请先写一些内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void GenerateName_Click(object sender, RoutedEventArgs e)
        {
            var chapter = GetSelectedChapter();
            if (chapter == null)
            {
                MessageBox.Show("请先选择要编辑的章节", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TakeSnapshot("人名生成前备份");
            var context = BuildAiContext();
            var input = AiInputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input)) input = "请生成适合小说风格的中文人名";
            var prompt = $"{context}请为小说生成角色名字。要求：{input}\n\n返回一组适合的名字，每个名字附简短说明。";

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(prompt));
            if (result != null)
            {
                chapter.Content += $"\n\n【生成的角色名 — {DateTime.Now:HH:mm}】\n{result}\n";
                AiResultTextBox.Text = result;
                _chatLogger?.Log("人名生成", input, result);
                ShowNotification("人名生成完成，已追加到章节");
            }
        }

        private async void Chat_Click(object sender, RoutedEventArgs e)
        {
            var input = AiInputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input))
            {
                MessageBox.Show("请在输入框中输入你想让AI写的内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var chapter = GetSelectedChapter();
            var context = BuildAiContext();
            var prompt = $"{context}{input}";

            if (chapter != null)
                TakeSnapshot("万能写作前备份");

            var result = await CallAiFunctionWithResult(async (apiService) =>
                await apiService.CompleteTextAsync(prompt));
            if (result != null)
            {
                if (chapter != null)
                {
                    chapter.Content += $"\n\n{result}\n";
                    ShowNotification("已按输入生成内容并追加到章节");
                }
                AiResultTextBox.Text = result;
                _chatLogger?.Log("万能聊天", input, result);
            }
        }

        private async void ApiSettings_Click(object sender, RoutedEventArgs e)
        {
            ShowApiSettings();
        }

        private void ProjectManager_Checked(object sender, RoutedEventArgs e) => ProjectPanelColumn.Width = new GridLength(250);
        private void ProjectManager_Unchecked(object sender, RoutedEventArgs e) => ProjectPanelColumn.Width = new GridLength(0);
        private void AiPanel_Checked(object sender, RoutedEventArgs e) => AiPanelColumn.Width = new GridLength(300);
        private void AiPanel_Unchecked(object sender, RoutedEventArgs e) => AiPanelColumn.Width = new GridLength(0);

        private void About_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TdxClaw AI写作助手 v1.0", "关于", MessageBoxButton.OK, MessageBoxImage.Information);

        // 其他事件
        private void ProjectTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (ProjectTreeView.SelectedItem is Chapter chapter)
            {
                // 检查该章节是否已在标签页中打开
                var existingTab = EditorTabControl.Items.OfType<TabItem>()
                    .FirstOrDefault(t => t.Tag is Chapter c && c.ChapterId == chapter.ChapterId);

                if (existingTab == null)
                {
                    // 创建绑定到章节标题的标签头（实时跟随标题修改）
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
            LoadPolishPresets();

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

        private async Task CallAiFunction(Func<IApiService, Task<string>> function)
        {
            if (_apiService == null)
            {
                var result = MessageBox.Show("请先设置API Key", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (result == MessageBoxResult.OK)
                    ShowApiSettings();
                return;
            }

            try
            {
                UpdateStatus("正在调用AI...");
                var result = await function(_apiService);
                AiResultTextBox.Text = result;
                UpdateStatus("AI调用成功");
            }
            catch (Exception ex)
            {
                AiResultTextBox.Text = $"AI调用失败: {ex.Message}";
                MessageBox.Show($"AI调用失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("AI调用失败");
            }
        }

        private async Task<string?> CallAiFunctionWithResult(Func<IApiService, Task<string>> function)
        {
            if (_apiService == null)
            {
                var result = MessageBox.Show("请先设置API Key", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (result == MessageBoxResult.OK)
                    ShowApiSettings();
                return null;
            }

            try
            {
                UpdateStatus("正在调用AI...");
                return await function(_apiService);
            }
            catch (Exception ex)
            {
                AiResultTextBox.Text = $"AI调用失败: {ex.Message}";
                MessageBox.Show($"AI调用失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("AI调用失败");
                return null;
            }
        }

        private void RefreshProjectView()
        {
            if (_currentProject != null)
            {
                ProjectTreeView.ItemsSource = null;
                ProjectTreeView.ItemsSource = new List<NovelProject> { _currentProject };
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
            _currentProject.FullOutline = FullOutlineTextBox.Text;
            _currentProject.ChapterOutline = ChapterOutlineTextBox.Text;
            _currentProject.CharacterSettings = CharacterSettingsTextBox.Text;
            _currentProject.BackgroundSettings = BackgroundSettingsTextBox.Text;
            _currentProject.WritingStyle = WritingStyleTextBox.Text;
        }

        private void SyncAiContextFromProject()
        {
            if (_currentProject == null) return;
            FullOutlineTextBox.Text = _currentProject.FullOutline ?? "";
            ChapterOutlineTextBox.Text = _currentProject.ChapterOutline ?? "";
            CharacterSettingsTextBox.Text = _currentProject.CharacterSettings ?? "";
            BackgroundSettingsTextBox.Text = _currentProject.BackgroundSettings ?? "";
            WritingStyleTextBox.Text = _currentProject.WritingStyle ?? "";
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

            if (parts.Count == 0) return "";

            return "===== 创作上下文（项目设定） =====\n\n"
                + string.Join("\n\n---\n\n", parts)
                + "\n\n====================================\n\n";
        }

        private void ShowComingSoon(string feature)
        {
            MessageBox.Show($"{feature}功能即将推出", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---- 润色预设管理 ----

        private void LoadPolishPresets()
        {
            _polishPresets.Clear();
            _polishPresets.Add("（无预设）");

            var presetsPath = Path.Combine(_configDir, "polish_presets.json");
            try
            {
                if (File.Exists(presetsPath))
                {
                    var json = File.ReadAllText(presetsPath);
                    var saved = JsonSerializer.Deserialize<List<string>>(json, _jsonOptions);
                    if (saved != null && saved.Count > 0)
                    {
                        foreach (var p in saved)
                            _polishPresets.Add(p);
                        PolishStyleCombo.ItemsSource = _polishPresets;
                        PolishStyleCombo.SelectedIndex = 0;
                        return;
                    }
                }
            }
            catch { }

            // 首次运行或加载失败：使用默认预设
            foreach (var p in DefaultPolishPresets)
                _polishPresets.Add(p);
            SavePolishPresets();

            PolishStyleCombo.ItemsSource = _polishPresets;
            PolishStyleCombo.SelectedIndex = 0;
        }

        private void SavePolishPresets()
        {
            var presetsPath = Path.Combine(_configDir, "polish_presets.json");
            var toSave = _polishPresets.Where(p => p != "（无预设）").ToList();
            File.WriteAllText(presetsPath, JsonSerializer.Serialize(toSave, _jsonOptions));
        }

        private void AddPolishPreset_Click(object sender, RoutedEventArgs e)
        {
            var text = PolishStyleCombo.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text == "（无预设）") return;
            if (!_polishPresets.Contains(text))
            {
                _polishPresets.Add(text);
                SavePolishPresets();
                ShowNotification($"已添加预设：{text}");
            }
        }

        private void RemovePolishPreset_Click(object sender, RoutedEventArgs e)
        {
            var text = PolishStyleCombo.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text == "（无预设）") return;
            if (DefaultPolishPresets.Contains(text))
            {
                ShowNotification("默认预设不可删除", isError: true);
                return;
            }
            if (_polishPresets.Remove(text))
            {
                PolishStyleCombo.SelectedIndex = 0;
                SavePolishPresets();
                ShowNotification($"已删除预设：{text}");
            }
        }

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
