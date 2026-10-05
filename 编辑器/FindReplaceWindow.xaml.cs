using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace 编辑器
{
    /// <summary>
    /// 全书查找 / 替换。
    ///
    /// 为什么值得单独做一个窗口而不是"能用就行"：
    /// 改角色名、把某个设定词统一成另一个说法、查一处伏笔究竟埋在哪几章——
    /// 这些是长篇写作里最高频的操作，没有它就得一章章点开肉眼找。
    ///
    /// 两条刻意的设计：
    /// ① **替换前先存快照**：替换是直接改 Chapter.Content，绕过了编辑框的撤销栈，
    ///    Ctrl+Z 撤不回来。所以每次查找会话内在第一次替换时存一份快照，
    ///    「版本历史」里能整本回退。同一批替换只存一次，不刷屏。
    /// ② **非模态**：边查边改才顺手，不打断定位流程。
    /// </summary>
    public partial class FindReplaceWindow : HandyControl.Controls.Window
    {
        /// <summary>一次查找最多列多少处。长篇全书搜一个常用词可能几千处，全列出来会卡。</summary>
        private const int MaxHits = 500;

        private readonly NovelProject _project;
        private Chapter? _currentChapter;
        private string _lastFind = "";
        private bool _snapshotted;

        /// <summary>替换前请求主窗口存一份快照（参数为快照说明）。</summary>
        public Action<string>? SnapshotRequested { get; set; }

        /// <summary>双击结果时请求跳转：章节 / 起始索引 / 长度。</summary>
        public Action<Chapter, int, int>? JumpRequested { get; set; }

        /// <summary>正文被改写后通知主窗口（刷新字数、标记待保存）。</summary>
        public Action? ContentChanged { get; set; }

        public FindReplaceWindow(NovelProject project, Chapter? currentChapter)
        {
            InitializeComponent();
            _project = project;
            _currentChapter = currentChapter;
            Owner = System.Windows.Application.Current.MainWindow;
            UpdateScopeHint();
            Loaded += (_, _) => FindBox.Focus();
        }

        /// <summary>主窗口切章时同步过来，「只查当前章」才知道当前是哪一章。</summary>
        public void SetCurrentChapter(Chapter? chapter)
        {
            _currentChapter = chapter;
            UpdateScopeHint();
        }

        private bool WholeBook => ScopeBookRadio.IsChecked == true;

        private StringComparison Comparison =>
            CaseSensitiveBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private IEnumerable<Chapter> Targets()
        {
            if (WholeBook) return _project.Chapters.OrderBy(c => c.ChapterNumber);
            return _currentChapter == null ? Enumerable.Empty<Chapter>() : new[] { _currentChapter };
        }

        private void UpdateScopeHint()
        {
            ScopeHintText.Text = WholeBook
                ? $"共 {_project.Chapters.Count} 章"
                : _currentChapter == null
                    ? "（当前没有打开章节）"
                    : $"第{_currentChapter.ChapterNumber}章「{_currentChapter.Title}」";
        }

        // ==================================================================
        // 查找
        // ==================================================================

        private void FindBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; Find(); }
        }

        private void Find_Click(object sender, RoutedEventArgs e) => Find();

        private void Scope_Changed(object sender, RoutedEventArgs e)
        {
            UpdateScopeHint();
            if (ResultList.ItemsSource != null) Find();   // 改了范围就重查，别让结果和设置对不上
        }

        private void Find()
        {
            var find = FindBox.Text;
            if (find != _lastFind) { _snapshotted = false; _lastFind = find; }

            var hits = new List<Hit>();
            if (find.Length > 0)
            {
                var cmp = Comparison;
                foreach (var c in Targets())
                {
                    var text = c.Content ?? "";
                    if (text.Length == 0) continue;

                    int idx = 0, n = 0;
                    while ((idx = text.IndexOf(find, idx, cmp)) >= 0)
                    {
                        n++;
                        hits.Add(new Hit
                        {
                            Chapter = c,
                            Index = idx,
                            Location = $"第{c.ChapterNumber}章「{c.Title}」 · 第 {n} 处",
                            Snippet = MakeSnippet(text, idx, find.Length),
                        });
                        idx += find.Length;
                        if (hits.Count >= MaxHits) break;
                    }
                    if (hits.Count >= MaxHits) break;
                }
            }

            ResultList.ItemsSource = hits;
            if (hits.Count > 0) ResultList.SelectedIndex = 0;

            SetStatus(find.Length == 0
                ? "填个词开始查找。"
                : hits.Count == 0
                    ? $"没有找到「{find}」。"
                    : $"命中 {hits.Count} 处" + (hits.Count >= MaxHits ? $"（只列前 {MaxHits} 处）" : "")
                      + " · 双击跳到那一处");
        }

        /// <summary>命中处前后各取一段，命中词本身用【】括出来——长片段里一眼能看到匹配在哪。</summary>
        private static string MakeSnippet(string text, int idx, int len)
        {
            const int pad = 45;
            int from = Math.Max(0, idx - pad);
            int to = Math.Min(text.Length, idx + len + pad);

            var sb = new StringBuilder();
            if (from > 0) sb.Append('…');
            sb.Append(Flatten(text[from..idx]));
            sb.Append('【').Append(Flatten(text.Substring(idx, len))).Append('】');
            sb.Append(Flatten(text[(idx + len)..to]));
            if (to < text.Length) sb.Append('…');
            return sb.ToString();

            static string Flatten(string s) => s.Replace("\r", " ").Replace("\n", " ");
        }

        private static int CountOf(string text, string find, StringComparison cmp)
        {
            if (find.Length == 0 || text.Length == 0) return 0;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(find, idx, cmp)) >= 0)
            {
                count++;
                idx += find.Length;
            }
            return count;
        }

        // ==================================================================
        // 替换
        // ==================================================================

        private void ReplaceOne_Click(object sender, RoutedEventArgs e)
        {
            var find = FindBox.Text;
            if (find.Length == 0) { SetStatus("先填要查找的内容。"); return; }
            if (ResultList.SelectedItem is not Hit hit)
            {
                SetStatus("先在结果里选一条，再点「替换选中」。");
                return;
            }

            var text = hit.Chapter.Content ?? "";
            // 正文可能已经被别处改过，索引会失效——失效就重查，别盲改
            if (hit.Index + find.Length > text.Length ||
                string.Compare(text, hit.Index, find, 0, find.Length, Comparison) != 0)
            {
                SetStatus("这一处已经不在原位了，已重新查找。");
                Find();
                return;
            }

            SnapshotOnce();
            hit.Chapter.Content = text.Remove(hit.Index, find.Length).Insert(hit.Index, ReplaceBox.Text);
            Touch();
            Find();
            SetStatus("已替换 1 处。");
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            var find = FindBox.Text;
            if (find.Length == 0) { SetStatus("先填要查找的内容。"); return; }

            var targets = Targets().ToList();
            var cmp = Comparison;
            var total = targets.Sum(c => CountOf(c.Content ?? "", find, cmp));
            if (total == 0) { SetStatus("没有匹配内容，未做任何改动。"); return; }

            var answer = MessageBox.Show(this,
                $"将在 {targets.Count} 章中替换 {total} 处。\n\n" +
                "替换会直接改写正文，Ctrl+Z 撤不回来（不走编辑框的撤销栈）；" +
                "已自动存快照，可在主窗口「版本历史」里整本回退。\n\n确定继续？",
                "全部替换", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            SnapshotOnce();
            foreach (var c in targets)
            {
                if (string.IsNullOrEmpty(c.Content)) continue;
                c.Content = c.Content.Replace(find, ReplaceBox.Text, cmp);
            }
            Touch();
            Find();
            SetStatus($"已替换 {total} 处。");
        }

        private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultList.SelectedItem is Hit hit)
                JumpRequested?.Invoke(hit.Chapter, hit.Index, FindBox.Text.Length);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 同一批查找只在第一次替换时存快照。
        /// 逐处替换存十几个快照会把版本历史冲掉，而一次会话里回退一次就够了。
        /// </summary>
        private void SnapshotOnce()
        {
            if (_snapshotted) return;
            SnapshotRequested?.Invoke($"查找替换前（{FindBox.Text}）");
            _snapshotted = true;
        }

        private void Touch() => ContentChanged?.Invoke();

        private void SetStatus(string text) => StatusText.Text = text;

        internal sealed class Hit
        {
            public Chapter Chapter { get; init; } = null!;
            public int Index { get; init; }
            public string Location { get; init; } = "";
            public string Snippet { get; init; } = "";
        }
    }
}
