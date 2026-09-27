using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using 编辑器.Services;

namespace 编辑器
{
    /// <summary>
    /// 「设定集」窗口 —— 把项目里散落的设定（大纲 / 人物 / 背景 / 文风）统合成一本
    /// 可以「制作」的资料书：选章、排顺序、编辑、让 AI 补全空章，最后导出成书。
    ///
    /// 数据同步策略与「设定」窗口一致：**编辑即时写回 SettingsBook**（TextChanged 同步），
    /// 不做"关闭时保存"。书随项目保存（NovelProject.SettingsBook），首次打开时由
    /// SettingsBookTemplates.EnsureBook 自动成书（只补空、不覆盖用户内容）。
    ///
    /// 与 AI 的关系：只发「为当前章生成/完善」的请求（<see cref="OnRequestAi"/>），
    /// 由 MainWindow 注入实现；导出也交给 MainWindow（<see cref="ExportRequested"/>），
    /// 窗口自身不碰文件与网络。
    /// </summary>
    public partial class SettingsBookWindow : HandyControl.Controls.Window
    {
        /// <summary>请求主窗口对当前章执行「AI 生成 / 完善」。由 MainWindow 注入。</summary>
        public Func<SettingsBookChapter, Task>? OnRequestAi { get; set; }

        /// <summary>请求导出：「Word」/「PDF」/「TXT」。由 MainWindow 注入。</summary>
        public event Action<string>? ExportRequested;

        private NovelProject? _project;
        private SettingsBook? _book;
        private SettingsBookChapter? _current;

        /// <summary>程序填充编辑框期间置位：避免把"加载"当成"用户编辑"回写。</summary>
        private bool _loading;
        private bool _busy;

        public SettingsBookWindow()
        {
            InitializeComponent();
        }

        // ==================== 对外接口（MainWindow 调用） ====================

        /// <summary>把项目内容载入窗口。切换项目 / 打开项目后调用；幂等。</summary>
        public void LoadFrom(NovelProject? project)
        {
            if (project == null) return;

            // 没有书就自动成书（只补空、不覆盖）
            SettingsBookTemplates.EnsureBook(project);

            _project = project;
            _book = project.SettingsBook;

            // 书名 / 副标题回填到输入框（_loading 保护，避免触发 TextChanged 写回）
            _loading = true;
            try
            {
                BookTitleBox.Text = _book!.Title;
                SubtitleBox.Text = _book!.Subtitle;
            }
            finally { _loading = false; }

            RefreshChapterList(preserveSelection: true);
        }

        /// <summary>
        /// 把编辑框内容写回项目（兜底，编辑本身是即时同步的）。
        /// ⚠ 若传入的不是窗口当前绑定的项目，编辑框里还是旧书内容 → 重新载入而不是写回。
        /// </summary>
        public void SyncToProject(NovelProject? project)
        {
            if (project == null || _project == null) return;

            if (!ReferenceEquals(project, _project))
            {
                LoadFrom(project);
                return;
            }

            FlushEditors();
        }

        /// <summary>外部（AI 生成后）改动了章节内容时，把当前显示同步过来。</summary>
        public void RefreshCurrent()
        {
            if (_current == null) return;
            _loading = true;
            try
            {
                ContentBox.Text = _current.Content ?? "";
                UpdateCharCount();
            }
            finally { _loading = false; }
            RefreshChapterList(preserveSelection: true);
        }

        // ==================== 内部：列表与编辑区 ====================

        private sealed class ChapterItem
        {
            public SettingsBookChapter Chapter { get; }
            public string Title => Chapter.Title;
            public bool IsAiGenerated => Chapter.IsAiGenerated;
            public string SourceLabel => SettingsBookTemplates.SourceLabel(Chapter.SourceKey);

            public ChapterItem(SettingsBookChapter c) => Chapter = c;
        }

        private void RefreshChapterList(bool preserveSelection)
        {
            if (_book == null) return;

            var items = _book.Chapters.Select(c => new ChapterItem(c)).ToList();
            var selectedKey = _current?.ChapterId;

            // ⚠ _loading 只包 ItemsSource 赋值与计数文本，**不能**包 SelectedItem：
            //   SelectedItem 赋值会同步触发 SelectionChanged，而它开头有 if(_loading) return 守卫，
            //   包住的话"自动选中第一项"永远不会把内容载入右区。
            _loading = true;
            try
            {
                ChapterList.ItemsSource = items;
                ChapterCountText.Text =
                    $"共 {items.Count} 章 · {items.Count(c => !c.Chapter.IncludeInExport)} 章未包含在导出中";

                if (items.Count == 0)
                {
                    _current = null;
                    ShowEmptyState();
                    return;
                }
            }
            finally { _loading = false; }

            var target = preserveSelection
                ? items.FirstOrDefault(i => i.Chapter.ChapterId == selectedKey) ?? items[0]
                : items[0];

            // SelectionChanged 同步触发；同项不触发时手动补一次载入
            if (!ReferenceEquals(ChapterList.SelectedItem, target))
                ChapterList.SelectedItem = target;
            if (!ReferenceEquals(_current, target.Chapter))
                LoadChapter(target);
        }

        private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (ChapterList.SelectedItem is ChapterItem item)
                LoadChapter(item);
        }

        private void LoadChapter(ChapterItem item)
        {
            FlushEditors(); // 切章前把上一章的编辑写回
            _current = item.Chapter;
            _loading = true;
            try
            {
                ChapterTitleBox.Text = _current.Title ?? "";
                ContentBox.Text = _current.Content ?? "";
                IncludeBox.IsChecked = _current.IncludeInExport;
                GenWithAiBtn.IsEnabled = true;
                UpdateCharCount();
            }
            finally { _loading = false; }
        }

        private void ShowEmptyState()
        {
            _loading = true;
            try
            {
                ChapterTitleBox.Text = "";
                ContentBox.Text = "";
                IncludeBox.IsChecked = true;
                GenWithAiBtn.IsEnabled = false;
                UpdateCharCount();
            }
            finally { _loading = false; }
        }

        /// <summary>把编辑框里尚未写回的值写回当前章。</summary>
        private void FlushEditors()
        {
            if (_current == null) return;
            _current.Title = ChapterTitleBox.Text.Trim();
            _current.Content = ContentBox.Text;
            _current.IncludeInExport = IncludeBox.IsChecked == true;
            _current.ModifiedDate = DateTime.Now;
        }

        // ==================== 编辑事件（即时写回） ====================

        private void BookTitleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _book == null) return;
            _book.Title = BookTitleBox.Text.Trim();
        }

        private void SubtitleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _book == null) return;
            _book.Subtitle = SubtitleBox.Text.Trim();
        }

        private void ChapterTitleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Title = ChapterTitleBox.Text.Trim();
            // 只刷新绑定显示，不重建列表 —— 重建会重置选中项，光标也会跳走
            ChapterList.Items.Refresh();
        }

        private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Content = ContentBox.Text;
            _current.ModifiedDate = DateTime.Now;
            UpdateCharCount();
        }

        private void IncludeBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.IncludeInExport = IncludeBox.IsChecked == true;
            ChapterCountText.Text =
                $"共 {_book!.Chapters.Count} 章 · {_book.Chapters.Count(c => !c.IncludeInExport)} 章未包含在导出中";
        }

        private void UpdateCharCount()
        {
            int n = ContentBox.Text.Length;
            CharCountText.Text = $"{n} 字" + (string.IsNullOrWhiteSpace(ContentBox.Text) ? " · 空章可点「AI 生成 / 完善」" : "");
        }

        // ==================== 排序 / 清空 ====================

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _book == null) return;
            int idx = _book.Chapters.FindIndex(c => c.ChapterId == _current.ChapterId);
            if (idx <= 0) return;
            (_book.Chapters[idx - 1], _book.Chapters[idx]) = (_book.Chapters[idx], _book.Chapters[idx - 1]);
            RefreshChapterList(preserveSelection: true);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _book == null) return;
            int idx = _book.Chapters.FindIndex(c => c.ChapterId == _current.ChapterId);
            if (idx < 0 || idx >= _book.Chapters.Count - 1) return;
            (_book.Chapters[idx + 1], _book.Chapters[idx]) = (_book.Chapters[idx], _book.Chapters[idx + 1]);
            RefreshChapterList(preserveSelection: true);
        }

        private async void GenWithAi_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _current == null || OnRequestAi == null) return;
            _busy = true;
            GenWithAiBtn.IsEnabled = false;
            StatusText.Text = $"正在让 AI 生成「{_current.Title}」…";
            try
            {
                await OnRequestAi(_current);
            }
            finally
            {
                _busy = false;
                GenWithAiBtn.IsEnabled = true;
                StatusText.Text = DefaultStatus;
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var confirm = MessageBox.Show($"清空「{_current.Title}」的全部内容？", "清空确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _current.Content = "";
            _current.IsAiGenerated = false;
            _loading = true;
            try { ContentBox.Text = ""; }
            finally { _loading = false; }
            UpdateCharCount();
        }

        // ==================== 导出 ====================

        private void ExportWord_Click(object sender, RoutedEventArgs e) => Export("Word");
        private void ExportPdf_Click(object sender, RoutedEventArgs e) => Export("PDF");
        private void ExportTxt_Click(object sender, RoutedEventArgs e) => Export("TXT");

        private void Export(string kind)
        {
            FlushEditors();
            if (_book == null || _book.Chapters.Count(c => c.IncludeInExport) == 0)
            {
                StatusText.Text = "设定集还没有可导出的章节。";
                return;
            }
            ExportRequested?.Invoke(kind);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            FlushEditors();
            Close();
        }

        private const string DefaultStatus = "编辑内容会立即同步到项目，保存项目即可写入文件。";
    }
}
