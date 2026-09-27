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

        // 注：全文大纲 / 章节大纲 / 人物 / 背景 / 文风这五项设定已移到独立的
        // SettingsWindow，本面板不再持有它们的控件——设定只留一个归属地，
        // 避免两处编辑同一字段带来的同步问题。

        public Expander MemExpander => MemoryExpander;
        public TextBox MemoryTextBox => MemoryTb;
        public Action? OnSaveMemory { get; set; }
        public Action? OnClearMemory { get; set; }

        /// <summary>清空「万能聊天」的对话记忆（MainWindow 负责落盘与提示）。</summary>
        public Action? OnClearChat { get; set; }

        // MainWindow 的回调（AI 功能委托给 MainWindow 处理）
        public Func<Task<AiResult?>>? OnContinueWriting { get; set; }
        public Func<Task<AiResult?>>? OnPolishText { get; set; }
        public Func<Task<AiResult?>>? OnGenerateName { get; set; }
        public Func<Task<AiResult?>>? OnChat { get; set; }
        public Func<Task<AiResult?>>? OnGenOutline { get; set; }
        public Func<Task<AiResult?>>? OnGenCharacter { get; set; }
        public Func<Task<AiResult?>>? OnGenBackground { get; set; }
        public Func<Task<AiResult?>>? OnGenChapterOutline { get; set; }
        public Func<Task<AiResult?>>? OnGenWritingStyle { get; set; }
        public Action? OnCopyResult { get; set; }
        public Action<Button>? OnApplyToContext { get; set; }
        public Action? OnStopAi { get; set; }
        public Action? OnOpenSettings { get; set; }

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

        private async void GenWritingStyle_Click(object sender, RoutedEventArgs e)
        {
            if (OnGenWritingStyle != null) await OnGenWritingStyle();
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e) => OnOpenSettings?.Invoke();

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
        /// <summary>
        /// 在面板头上显示当前生效的提示词方案（小说创作 / 学术论文 / 公文公告 / 自定义…）。
        /// 方案决定 AI 的人设与输出取向，面板上不显示的话，用户切换后会觉得"AI 突然不对劲"。
        /// </summary>
        public void SetPresetName(string name, bool isBuiltIn)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "默认";

            PresetHintText.Text = "方案：" + name;
            PresetHintText.ToolTip = $"当前使用「{name}」{(isBuiltIn ? "内置" : "自定义")}提示词方案。\n"
                                   + "在「AI 设置 → 系统提示词」里切换或修改。";
        }

        /// <summary>
        /// 刷「万能聊天」的对话记忆轮数。
        ///
        /// 为什么要在界面上显示轮数：大模型服务端不保存任何会话状态，"AI 记得前文"
        /// 完全靠客户端每轮把历史重发一遍。轮数看不见时，用户分不清
        /// "AI 把刚才那句话忘了" 和 "本来就没记"。
        /// </summary>
        /// <summary>
        /// 刷新「聊天记忆」提示行。
        ///
        /// 展示的是**对话记忆**这一部分：摘要（已压缩的轮数）+ 仍以原文保留的轮数
        /// + 它的估算 token 占用。这里**不含**每轮都会重新带上的设定与章节正文 ——
        /// 完整规模看状态栏那行由服务端报回的真实 Token 数。
        /// </summary>
        public void SetChatMemoryInfo(int rounds, int summarizedRounds, int usedTokens, int contextWindow)
        {
            if (rounds <= 0 && summarizedRounds <= 0)
            {
                ChatMemoryText.Text = "聊天记忆：暂无 —— 用「万能聊天」问一句就记一句";
            }
            else
            {
                var parts = new List<string>();
                if (summarizedRounds > 0) parts.Add($"摘要含已压缩 {summarizedRounds} 轮");
                if (rounds > 0) parts.Add($"原文 {rounds} 轮");

                var body = parts.Count > 0 ? string.Join(" + ", parts) : "暂无";
                var usage = contextWindow > 0
                    ? $" · 约 {FormatTokenCount(usedTokens)} / {FormatTokenCount(contextWindow)}"
                    : "";

                ChatMemoryText.Text = $"聊天记忆：{body}{usage}";
            }

            ChatMemoryText.ToolTip =
                "只对「万能聊天」生效；续写、润色、生成类都是一次性任务，不带历史。\n"
                + "接近模型窗口上限时会**自动压缩**：先把较早的回复省略成占位符，"
                + "再把更早的对话写成摘要 —— 摘要里的设定与硬性要求会保留，细节会有损失。\n"
                + "这里显示的是对话记忆的占用，不含设定与章节正文。";
        }

        private static string FormatTokenCount(int tokens) =>
            tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M"
            : tokens >= 1000 ? $"{tokens / 1000.0:0.#}K"
            : tokens.ToString();

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

        // ---- 技能选择器 ----

        /// <summary>技能下拉，暴露给主窗口填充选项与读取选中项。</summary>
        public ComboBox SkillCombo => SkillComboBox;

        /// <summary>技能切换时触发（主窗口据此更新当前技能与提示）。</summary>
        public event Action? SkillChanged;

        /// <summary>显示当前技能的输入提示（无技能时清空）。</summary>
        public void SetSkillHint(string? text) => SkillHintText.Text = text ?? "";

        private void SkillComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SkillChanged?.Invoke();
        }

        private void PolishStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PolishStyleCombo.SelectedItem is string style && style != "（无预设）")
                PolishStyleTextBox.Text = style;
        }

        private void SaveMemory_Click(object sender, RoutedEventArgs e) => OnSaveMemory?.Invoke();
        private void ClearMemory_Click(object sender, RoutedEventArgs e) => OnClearMemory?.Invoke();
        private void ClearChat_Click(object sender, RoutedEventArgs e) => OnClearChat?.Invoke();

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
