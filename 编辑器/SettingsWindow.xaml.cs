using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace 编辑器
{
    /// <summary>贯穿全书的设定分区。与 NovelProject 上的字段一一对应。</summary>
    public enum SettingSection
    {
        FullOutline,        // 全文大纲
        ChapterOutline,     // 章节大纲
        CharacterSettings,  // 主要人物设定
        BackgroundSettings, // 主要背景设定
        WritingStyle        // 文风设定
    }

    public class SettingSectionItem
    {
        public SettingSection Kind { get; init; }
        public string Title { get; init; } = "";
        public string Hint { get; init; } = "";
    }

    /// <summary>
    /// 「设定」窗口 —— 全文大纲 / 人物设定这类贯穿全书的设定集中在这里。
    ///
    /// 为什么要独立成窗口：这些设定是全书级的参考资料，写正文时会被反复查阅，
    /// 塞在 AI 面板的一串折叠区里既占地方又要来回滚动；而 AI 面板只留"交互"这一件事。
    ///
    /// 与 AI 的关系（两条链，都不经过本窗口的额外 UI）：
    ///   · 读：主窗口 BuildProjectContext() 直接读 NovelProject 的同名字段 → 进 system 消息
    ///   · 写：主窗口 SetSettingValue() 改字段后调用本窗口的 SetValue() 同步显示
    /// 所以本窗口没有任何"对话"入口，AI 交互仍然只有 AI 面板那一个。
    ///
    /// 数据同步策略：**编辑即时写回 NovelProject**（TextChanged 同步），
    /// 不做"关闭时保存"——那样用户忘记关窗口或程序异常退出就会丢内容。
    /// </summary>
    public partial class SettingsWindow : HandyControl.Controls.Window
    {
        /// <summary>请求主窗口对指定分区执行「AI 生成 / 完善」。由 MainWindow 注入。</summary>
        public Func<SettingSection, Task>? OnRequestAi { get; set; }

        private const string DefaultStatus = "编辑内容会立即同步到项目，保存项目即可写入文件。";

        private readonly List<SettingSectionItem> _sections = new()
        {
            new SettingSectionItem
            {
                Kind = SettingSection.FullOutline, Title = "全文大纲",
                Hint = "整部书的走向、主要情节节点与结局"
            },
            new SettingSectionItem
            {
                Kind = SettingSection.ChapterOutline, Title = "章节大纲",
                Hint = "当前章节要写的内容与节奏"
            },
            new SettingSectionItem
            {
                Kind = SettingSection.CharacterSettings, Title = "主要人物设定",
                Hint = "主角与重要配角：身份、性格、动机、关系"
            },
            new SettingSectionItem
            {
                Kind = SettingSection.BackgroundSettings, Title = "主要背景设定",
                Hint = "世界观、时代、地理、规则体系"
            },
            new SettingSectionItem
            {
                Kind = SettingSection.WritingStyle, Title = "文风设定",
                Hint = "叙述视角、语言风格、节奏与禁忌"
            },
        };

        private NovelProject? _project;
        private SettingSectionItem? _current;

        /// <summary>程序填充编辑框期间置位：避免把"加载"当成"用户编辑"回写。</summary>
        private bool _loading;
        private bool _busy;

        public SettingsWindow()
        {
            InitializeComponent();

            SectionList.ItemsSource = _sections;
            SectionList.SelectedIndex = 0;
            _current = _sections[0];
            UpdateCharCount();
        }

        // ==================== 对外接口（MainWindow 调用） ====================

        /// <summary>把项目内容载入窗口。切换项目 / 打开项目后调用。</summary>
        public void LoadFrom(NovelProject? project)
        {
            _project = project;
            ReloadCurrentSection();
        }

        /// <summary>
        /// 把编辑框内容写回项目。因为编辑是即时同步的，这里主要是兜底（幂等）。
        ///
        /// ⚠ 若传入的项目不是窗口当前绑定的那个（用户中途切了项目），
        /// 说明编辑框里还是旧项目的内容 —— 此时**绝不能写进新项目**，改为重新载入。
        /// </summary>
        public void SyncToProject(NovelProject? project)
        {
            if (project == null) return;

            if (!ReferenceEquals(project, _project))
            {
                LoadFrom(project);
                return;
            }

            if (_current != null)
                WriteToProject(_current.Kind, ContentBox.Text);
        }

        /// <summary>
        /// AI 写入：更新数据并把结果反映到界面。
        /// 正在显示该分区就刷新编辑框，否则只落数据（用户切过去时自然看到）。
        /// </summary>
        public void SetValue(SettingSection kind, string text)
        {
            WriteToProject(kind, text);

            if (_current != null && _current.Kind == kind)
            {
                _loading = true;
                ContentBox.Text = text;
                _loading = false;
                UpdateCharCount();
            }
        }

        /// <summary>读取当前生效值（优先取项目里的数据）。</summary>
        public string GetValue(SettingSection kind) => _project == null ? "" : ReadFromProject(kind);

        /// <summary>底部状态文字，供主窗口反馈生成进度。</summary>
        public void SetStatus(string text) => StatusText.Text = text;

        /// <summary>切到指定分区（AI 刚写入某项时，让作者直接看到）。</summary>
        public void NavigateTo(SettingSection kind)
        {
            var index = _sections.FindIndex(s => s.Kind == kind);
            if (index >= 0 && SectionList.SelectedIndex != index)
                SectionList.SelectedIndex = index;   // 触发 SelectionChanged → 重新载入
        }

        // ==================== 分区导航 ====================

        private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SectionList.SelectedItem is not SettingSectionItem item) return;
            _current = item;
            ReloadCurrentSection();
        }

        private void ReloadCurrentSection()
        {
            if (_current == null) return;

            _loading = true;
            SectionTitleText.Text = _current.Title;
            SectionHintText.Text = _current.Hint;
            ContentBox.Text = _project == null ? "" : ReadFromProject(_current.Kind);
            _loading = false;

            UpdateCharCount();
        }

        // ==================== 编辑即同步 ====================

        private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;   // 程序填充，不是用户编辑
            WriteToProject(_current.Kind, ContentBox.Text);
            UpdateCharCount();
        }

        private void UpdateCharCount()
        {
            var n = ContentBox.Text.Length;
            CharCountText.Text = n == 0 ? "空" : $"{n} 字";
        }

        // ==================== 按钮 ====================

        private async void GenWithAi_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || OnRequestAi == null || _busy) return;

            _busy = true;
            GenWithAiBtn.IsEnabled = false;
            StatusText.Text = $"正在让 AI 处理「{_current.Title}」…";
            try
            {
                await OnRequestAi(_current.Kind);
                StatusText.Text = DefaultStatus;
            }
            catch (Exception ex)
            {
                StatusText.Text = "生成失败：" + ex.Message;
            }
            finally
            {
                _busy = false;
                GenWithAiBtn.IsEnabled = true;
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || ContentBox.Text.Length == 0) return;

            var answer = MessageBox.Show($"确定清空「{_current.Title}」的内容吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            ContentBox.Text = "";   // TextChanged 会写回项目
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ==================== 数据存取 ====================

        private string ReadFromProject(SettingSection kind) => kind switch
        {
            SettingSection.FullOutline => _project?.FullOutline ?? "",
            SettingSection.ChapterOutline => _project?.ChapterOutline ?? "",
            SettingSection.CharacterSettings => _project?.CharacterSettings ?? "",
            SettingSection.BackgroundSettings => _project?.BackgroundSettings ?? "",
            SettingSection.WritingStyle => _project?.WritingStyle ?? "",
            _ => ""
        };

        private void WriteToProject(SettingSection kind, string text)
        {
            if (_project == null) return;

            switch (kind)
            {
                case SettingSection.FullOutline: _project.FullOutline = text; break;
                case SettingSection.ChapterOutline: _project.ChapterOutline = text; break;
                case SettingSection.CharacterSettings: _project.CharacterSettings = text; break;
                case SettingSection.BackgroundSettings: _project.BackgroundSettings = text; break;
                case SettingSection.WritingStyle: _project.WritingStyle = text; break;
            }
        }
    }
}
