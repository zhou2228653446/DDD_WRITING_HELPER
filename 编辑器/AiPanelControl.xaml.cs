using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 编辑器.Services;

namespace 编辑器
{
    public class ChapterSelectItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Title { get; set; } = "";
        public string ChapterId { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        }
    }
    public partial class AiPanelControl : UserControl
    {
        public event Action? DetachRequested;

        private readonly ObservableCollection<string> _polishPresets = new();
        private static readonly string[] DefaultPolishPresets = { "正式严谨", "简洁干练", "优美文学", "口语化", "古风雅韵", "幽默风趣" };

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private string _configDir = null!;

        // 暴露控件供 MainWindow 访问
        public TextBox InputTextBox => AiInputTextBox;
        public TextBox ResultTextBox => AiResultTextBox;
        public CheckBox StyleToggle => PolishStyleToggle;
        public ComboBox StyleCombo => PolishStyleCombo;
        public TextBox StyleTextBox => PolishStyleTextBox;
        public Button ApplyBtn => ApplyToContextBtn;

        public Expander OutlineExpander => FullOutlineExpander;
        public TextBox OutlineTextBox => FullOutlineTextBox;
        public Expander ChapterExpander => ChapterOutlineExpander;
        public TextBox ChapterTextBox => ChapterOutlineTextBox;
        public Expander CharacterExpander => CharacterSettingsExpander;
        public TextBox CharacterTextBox => CharacterSettingsTextBox;
        public Expander BackgroundExpander => BackgroundSettingsExpander;
        public TextBox BackgroundTextBox => BackgroundSettingsTextBox;
        public Expander WritingStyleExpander => WritingStyleExp;
        public TextBox WritingStyleTextBox => WritingStyleTb;

        public Expander MemExpander => MemoryExpander;
        public TextBox MemoryTextBox => MemoryTb;
        public Action? OnSaveMemory { get; set; }
        public Action? OnClearMemory { get; set; }

        // MainWindow 的回调（AI 功能委托给 MainWindow 处理）
        public Func<Task<AiResult?>>? OnContinueWriting { get; set; }
        public Func<Task<AiResult?>>? OnPolishText { get; set; }
        public Func<Task<AiResult?>>? OnGenerateName { get; set; }
        public Func<Task<AiResult?>>? OnChat { get; set; }
        public Func<Task<AiResult?>>? OnGenOutline { get; set; }
        public Func<Task<AiResult?>>? OnGenCharacter { get; set; }
        public Func<Task<AiResult?>>? OnGenBackground { get; set; }
        public Func<Task<AiResult?>>? OnGenChapterOutline { get; set; }
        public Action? OnCopyResult { get; set; }
        public Action<Button>? OnApplyToContext { get; set; }
        public Action? OnStopAi { get; set; }

        public AiPanelControl()
        {
            InitializeComponent();
        }

        public void Initialize(string configDir)
        {
            _configDir = configDir;
            LoadPolishPresets();
        }

        private void DetachBtn_Click(object sender, RoutedEventArgs e) => DetachRequested?.Invoke();

        private async void ContinueWriting_Click(object sender, RoutedEventArgs e)
        {
            if (OnContinueWriting != null) await OnContinueWriting();
        }

        private async void PolishText_Click(object sender, RoutedEventArgs e)
        {
            if (OnPolishText != null) await OnPolishText();
        }

        private async void GenerateName_Click(object sender, RoutedEventArgs e)
        {
            if (OnGenerateName != null) await OnGenerateName();
        }

        private async void Chat_Click(object sender, RoutedEventArgs e)
        {
            if (OnChat != null) await OnChat();
        }

        private async void GenOutline_Click(object sender, RoutedEventArgs e)
        {
            if (OnGenOutline != null) await OnGenOutline();
        }

        private async void GenCharacter_Click(object sender, RoutedEventArgs e)
        {
            if (OnGenCharacter != null) await OnGenCharacter();
        }

        private async void GenBackground_Click(object sender, RoutedEventArgs e)
        {
            if (OnGenBackground != null) await OnGenBackground();
        }

        private async void GenChapterOutline_Click(object sender, RoutedEventArgs e)
        {
            if (OnGenChapterOutline != null) await OnGenChapterOutline();
        }

        private void CopyAiResult_Click(object sender, RoutedEventArgs e) => OnCopyResult?.Invoke();

        private void ApplyToContext_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) OnApplyToContext?.Invoke(btn);
        }

        // ---- 润色预设 ----

        public void LoadPolishPresets()
        {
            if (string.IsNullOrEmpty(_configDir)) return;

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

            foreach (var p in DefaultPolishPresets)
                _polishPresets.Add(p);
            SavePolishPresets();

            PolishStyleCombo.ItemsSource = _polishPresets;
            PolishStyleCombo.SelectedIndex = 0;
        }

        public void SavePolishPresets()
        {
            if (string.IsNullOrEmpty(_configDir)) return;
            var presetsPath = Path.Combine(_configDir, "polish_presets.json");
            var toSave = _polishPresets.Where(p => p != "（无预设）").ToList();
            File.WriteAllText(presetsPath, JsonSerializer.Serialize(toSave, _jsonOptions));
        }

        public void AddPolishPreset_Click(object sender, RoutedEventArgs e)
        {
            var text = PolishStyleCombo.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text == "（无预设）") return;
            if (!_polishPresets.Contains(text))
            {
                _polishPresets.Add(text);
                SavePolishPresets();
            }
        }

        public void RemovePolishPreset_Click(object sender, RoutedEventArgs e)
        {
            var text = PolishStyleCombo.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text == "（无预设）") return;
            if (DefaultPolishPresets.Contains(text)) return;
            if (_polishPresets.Remove(text))
            {
                PolishStyleCombo.SelectedIndex = 0;
                SavePolishPresets();
            }
        }

        // 更新分离按钮状态
        public void SetDetached(bool isDetached)
        {
            DetachBtn.Content = isDetached ? "📌 合并" : "📌 弹出";
            DetachBtn.ToolTip = isDetached ? "将AI面板合并回主窗口" : "将AI面板分离为独立窗口";
        }

        // 停止按钮显隐
        public void SetStopButtonVisible(bool visible)
        {
            StopAiBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StopAi_Click(object sender, RoutedEventArgs e) => OnStopAi?.Invoke();

        private void PolishStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PolishStyleCombo.SelectedItem is string style && style != "（无预设）")
                PolishStyleTextBox.Text = style;
        }

        private void SaveMemory_Click(object sender, RoutedEventArgs e) => OnSaveMemory?.Invoke();
        private void ClearMemory_Click(object sender, RoutedEventArgs e) => OnClearMemory?.Invoke();

        // ---- 章节选择器 ----

        private readonly ObservableCollection<ChapterSelectItem> _chapterItems = new();

        public void RefreshChapterList(IEnumerable<Chapter> chapters)
        {
            // 保留已选状态
            var selectedIds = _chapterItems.Where(c => c.IsSelected).Select(c => c.ChapterId).ToHashSet();

            _chapterItems.Clear();
            foreach (var ch in chapters)
            {
                _chapterItems.Add(new ChapterSelectItem
                {
                    Title = ch.Title,
                    ChapterId = ch.ChapterId,
                    IsSelected = selectedIds.Contains(ch.ChapterId)
                });
            }
            ChapterListBox.ItemsSource = _chapterItems;
        }

        /// <summary>获取所有选中章节的 ID</summary>
        public HashSet<string> GetSelectedChapterIds()
        {
            return _chapterItems.Where(c => c.IsSelected).Select(c => c.ChapterId).ToHashSet();
        }

        private void SelectAllChapters_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _chapterItems) item.IsSelected = true;
        }

        private void DeselectAllChapters_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _chapterItems) item.IsSelected = false;
        }
    }
}
