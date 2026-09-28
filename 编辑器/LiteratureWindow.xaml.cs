using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using HandyControl.Controls;
using static 编辑器.BibtexParser;

namespace 编辑器
{
    /// <summary>
    /// 参考文献库窗口：左侧列表（编号与 AI 上下文里的 [n] 一致）、右侧编辑表单。
    /// 编辑即时写回项目（与设定集窗口同款模式），删除 / 导入直接改库并刷新列表。
    /// </summary>
    public partial class LiteratureWindow : HandyControl.Controls.Window
    {
        private NovelProject _project = null!;
        private LiteratureEntry? _current;
        private bool _loading;
        private List<ListItem> _items = new();

        /// <summary>列表条目包装：编号 [n] 与 AI 上下文引用编号一致。</summary>
        private sealed class ListItem
        {
            public LiteratureEntry Entry { get; }
            public int No { get; }
            public string Display => $"[{No + 1}] {Entry.Title}";
            public string Sub
            {
                get
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(Entry.Authors)) parts.Add(Entry.Authors);
                    if (!string.IsNullOrWhiteSpace(Entry.Venue)) parts.Add(Entry.Venue);
                    return parts.Count > 0 ? string.Join(" · ", parts) : Entry.CitationKey;
                }
            }
            public ListItem(LiteratureEntry entry, int no) { Entry = entry; No = no; }
        }

        public LiteratureWindow(NovelProject project)
        {
            InitializeComponent();
            _project = project;
            RefreshList(keep: null);
        }

        // ------------------------------------------------------------------
        // 列表
        // ------------------------------------------------------------------

        private void RefreshList(LiteratureEntry? keep)
        {
            var library = _project.LiteratureLibrary;
            _items = library.Select((e, i) => new ListItem(e, i)).ToList();

            _loading = true;
            try
            {
                EntryList.ItemsSource = _items;
                CountText.Text = library.Count == 0
                    ? "文献库为空。手动「新增」，或从 Zotero / 知网导出的 .bib 文件「导入 BibTeX」。"
                    : $"共 {library.Count} 条文献。AI 生成正文时引用处会标 [1]..[{library.Count}]，库里没有的只写【需引文献】占位。";
            }
            finally { _loading = false; }

            if (library.Count == 0)
            {
                _current = null;
                EntryList.SelectedItem = null;
                FormPanel.IsEnabled = false;
                ClearForm();
                return;
            }

            // 在 _loading=false 之后再赋 SelectedItem：让 SelectionChanged 正常触发并回填表单。
            // （在守卫内赋值会被自己的 _loading 拦掉，表单永远是空的——设定集窗口踩过同一个坑。）
            var target = _items.FirstOrDefault(it => ReferenceEquals(it.Entry, keep)) ?? _items[0];
            if (!ReferenceEquals(EntryList.SelectedItem, target))
                EntryList.SelectedItem = target;
        }

        private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (EntryList.SelectedItem is not ListItem item) return;

            FlushEditors(); // 切条目前把上一条的编辑写回
            _current = item.Entry;

            _loading = true;
            try
            {
                FormPanel.IsEnabled = true;
                TitleBox.Text = _current.Title;
                AuthorsBox.Text = _current.Authors;
                YearBox.Text = _current.Year;
                VenueBox.Text = _current.Venue;
                DoiBox.Text = _current.Doi;
                UrlBox.Text = _current.Url;
                AbstractBox.Text = _current.Abstract;
                NoteBox.Text = _current.Note;
            }
            finally { _loading = false; }
        }

        // ------------------------------------------------------------------
        // 编辑即时写回
        // ------------------------------------------------------------------

        private void Field_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Title = TitleBox.Text.Trim();
            _current.Authors = AuthorsBox.Text.Trim();
            _current.Year = YearBox.Text.Trim();
            _current.Venue = VenueBox.Text.Trim();
            _current.Doi = DoiBox.Text.Trim();
            _current.Url = UrlBox.Text.Trim();
            _current.Abstract = AbstractBox.Text;
            _current.Note = NoteBox.Text.Trim();
            RefreshCurrentRow();
        }

        /// <summary>当前行的展示文案刷新（编号 / 标题变化时保持列表同步）。</summary>
        private void RefreshCurrentRow()
        {
            if (EntryList.SelectedItem is ListItem item)
            {
                var idx = _items.IndexOf(item);
                if (idx >= 0) EntryList.Items.Refresh();
            }
        }

        private void FlushEditors()
        {
            if (_current == null) return;
            _current.Title = TitleBox.Text.Trim();
            _current.Authors = AuthorsBox.Text.Trim();
            _current.Year = YearBox.Text.Trim();
            _current.Venue = VenueBox.Text.Trim();
            _current.Doi = DoiBox.Text.Trim();
            _current.Url = UrlBox.Text.Trim();
            _current.Abstract = AbstractBox.Text;
            _current.Note = NoteBox.Text.Trim();
        }

        private void ClearForm()
        {
            _loading = true;
            try
            {
                TitleBox.Text = ""; AuthorsBox.Text = ""; YearBox.Text = "";
                VenueBox.Text = ""; DoiBox.Text = ""; UrlBox.Text = "";
                AbstractBox.Text = ""; NoteBox.Text = "";
            }
            finally { _loading = false; }
        }

        // ------------------------------------------------------------------
        // 新增 / 删除 / 导入
        // ------------------------------------------------------------------

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            FlushEditors();
            var entry = new LiteratureEntry { Title = "新文献" };
            _project.LiteratureLibrary.Add(entry);
            _project.Save();
            RefreshList(keep: entry);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (EntryList.SelectedItem is not ListItem item) return;
            var entry = item.Entry;

            var confirm = HandyControl.Controls.MessageBox.Show(
                $"确定删除文献「{(string.IsNullOrWhiteSpace(entry.Title) ? "(无标题)" : entry.Title)}」吗？",
                "删除文献", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            _project.LiteratureLibrary.Remove(entry);
            _project.Save();
            RefreshList(keep: null);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 BibTeX 文件",
                Filter = "BibTeX 文件 (*.bib;*.txt)|*.bib;*.txt|所有文件 (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;

            FlushEditors();
            int imported = 0, skipped = 0;
            foreach (var path in dialog.FileNames)
            {
                try
                {
                    var text = System.IO.File.ReadAllText(path);
                    var entries = BibtexParser.Parse(text);
                    // 引用键去重：库里已有同键的条目跳过，避免重复导入翻倍
                    var existingKeys = new HashSet<string>(
                        _project.LiteratureLibrary.Select(x => x.CitationKey), StringComparer.Ordinal);
                    foreach (var entry in entries)
                    {
                        if (!string.IsNullOrEmpty(entry.CitationKey) && !existingKeys.Add(entry.CitationKey))
                        {
                            skipped++;
                            continue;
                        }
                        _project.LiteratureLibrary.Add(entry);
                        imported++;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    System.Diagnostics.Debug.WriteLine($"[Literature] 导入 {path} 失败：{ex.Message}");
                }
            }

            _project.Save();
            RefreshList(keep: null);

            var msg = imported > 0 ? $"成功导入 {imported} 条文献" : "没有可导入的文献";
            if (skipped > 0) msg += $"（跳过 {skipped} 条：解析失败或引用键重复）";
            HandyControl.Controls.MessageBox.Info(msg, "导入 BibTeX");
        }

        // ------------------------------------------------------------------
        // 复制参考文献表
        // ------------------------------------------------------------------

        private void CopyList_Click(object sender, RoutedEventArgs e)
        {
            FlushEditors();
            if (_project.LiteratureLibrary.Count == 0)
            {
                HandyControl.Controls.MessageBox.Warning("文献库为空，先添加或导入文献。", "复制参考文献表");
                return;
            }
            try
            {
                Clipboard.SetText(LiteratureFormatter.FormatReferenceList(_project.LiteratureLibrary));
                HandyControl.Controls.MessageBox.Success("参考文献表已复制到剪贴板，粘贴进论文即可。", "复制参考文献表");
            }
            catch (Exception ex)
            {
                HandyControl.Controls.MessageBox.Error("复制失败：" + ex.Message, "复制参考文献表");
            }
        }
    }
}
