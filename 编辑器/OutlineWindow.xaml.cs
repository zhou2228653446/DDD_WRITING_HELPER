using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace 编辑器
{
    /// <summary>
    /// 大纲视图：把一本书的结构摊开成卡片墙。
    ///
    /// 为什么需要它：大纲原先只是设定窗口里的一个文本字段。写到三四十章之后，
    /// 作者真正想知道的不是"大纲写的啥"，而是——哪章还没动笔、哪章写完却没留梗概、
    /// 顺序排得对不对。这些是结构问题，读一段大纲文本回答不了，得看得见。
    ///
    /// 卡片墙比树更适合这件事：一眼扫过去就能发现"中间空了两章""这几章字数明显偏少"。
    /// </summary>
    public partial class OutlineWindow : HandyControl.Controls.Window
    {
        private readonly NovelProject _project;

        /// <summary>双击卡片：让主窗口打开这一章。</summary>
        private readonly Action<Chapter> _openChapter;

        /// <summary>结构被改动后通知主窗口（刷新章节树、标记待保存）。</summary>
        private readonly Action _changed;

        public OutlineWindow(NovelProject project, Action<Chapter> openChapter, Action changed)
        {
            InitializeComponent();

            _project = project;
            _openChapter = openChapter;
            _changed = changed;

            TitleText.Text = $"大纲 · {project.ProjectName}";
            Refresh();
        }

        // ==================================================================
        // 卡片
        // ==================================================================

        private sealed class Card
        {
            public Chapter Chapter { get; init; } = null!;
            public string NumberText => $"第{Chapter.ChapterNumber}章";
            public string Title => Chapter.Title;
            public string WordsText => Chapter.WordCount == 0 ? "未写" : $"{Chapter.WordCount} 字";
            public string SummaryText => string.IsNullOrWhiteSpace(Chapter.Summary)
                ? "（还没写梗概）"
                : Chapter.Summary;
            public string StateText { get; init; } = "";
            public Brush StateBrush { get; init; } = Brushes.Transparent;
        }

        private void Refresh()
        {
            var ordered = _project.Chapters.OrderBy(c => c.ChapterNumber).ToList();

            var muted = BrushOf("Brush.TextMuted");
            var accent = BrushOf("Brush.Accent");
            var done = BrushOf("Brush.Success");

            var cards = new List<Card>();
            foreach (var c in ordered)
            {
                string state;
                Brush brush;
                if (c.WordCount == 0) { state = "还没动笔"; brush = muted; }
                else if (string.IsNullOrWhiteSpace(c.Summary)) { state = "缺梗概"; brush = accent; }
                else { state = "已完成"; brush = done; }

                cards.Add(new Card { Chapter = c, StateText = state, StateBrush = brush });
            }

            CardList.ItemsSource = FilterBox.SelectedIndex switch
            {
                1 => cards.Where(x => x.Chapter.WordCount == 0).ToList(),
                2 => cards.Where(x => x.Chapter.WordCount > 0 && string.IsNullOrWhiteSpace(x.Chapter.Summary)).ToList(),
                3 => cards.Where(x => x.Chapter.WordCount > 0 && !string.IsNullOrWhiteSpace(x.Chapter.Summary)).ToList(),
                _ => cards,
            };

            var total = ordered.Sum(c => c.WordCount);
            var unwritten = ordered.Count(c => c.WordCount == 0);
            var noSummary = ordered.Count(c => c.WordCount > 0 && string.IsNullOrWhiteSpace(c.Summary));
            StatsText.Text = $"共 {ordered.Count} 章 · 累计 {total} 字 · 未写 {unwritten} 章 · 缺梗概 {noSummary} 章";
        }

        private static Brush BrushOf(string key) =>
            Application.Current?.Resources[key] as Brush ?? Brushes.Gray;

        private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) Refresh();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        // ==================================================================
        // 卡片交互
        // ==================================================================

        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2) return;
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not Card card) return;

            _openChapter(card.Chapter);
        }

        private Card? CardOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as Card;

        private void OpenChapter_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is { } card) _openChapter(card.Chapter);
        }

        private void RenameChapter_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card) return;

            var input = new InputDialog("章节改名", "新的章节标题", card.Chapter.Title) { Owner = this };
            if (input.ShowDialog() != true) return;

            var title = input.InputText?.Trim();
            if (string.IsNullOrWhiteSpace(title)) return;

            card.Chapter.Title = title;
            NotifyChanged();
        }

        private void EditSummary_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card) return;

            var input = new InputDialog("章节梗概",
                "一两句话说清这章发生了什么。长篇写到后面，跨章连贯全靠它。",
                card.Chapter.Summary) { Owner = this };
            if (input.ShowDialog() != true) return;

            card.Chapter.Summary = input.InputText?.Trim() ?? "";
            NotifyChanged();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(CardOf(sender), -1);

        private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(CardOf(sender), +1);

        private void Move(Card? card, int delta)
        {
            if (card == null) return;
            if (!_project.MoveChapter(card.Chapter, delta)) return;   // 已经在头/尾

            NotifyChanged();
        }

        private void DeleteChapter_Click(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not { } card) return;

            var answer = MessageBox.Show(this,
                $"确定删除第{card.Chapter.ChapterNumber}章「{card.Chapter.Title}」吗？\n" +
                $"这一章有 {card.Chapter.WordCount} 字，删除后其余章节会重新编号。",
                "删除章节", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            _project.Chapters.Remove(card.Chapter);
            _project.RenumberChapters();

            NotifyChanged();
        }

        private void NewChapter_Click(object sender, RoutedEventArgs e)
        {
            var next = _project.Chapters.Count == 0
                ? 1
                : _project.Chapters.Max(c => c.ChapterNumber) + 1;

            var input = new InputDialog("新建章节", "章节标题", $"第{next}章") { Owner = this };
            if (input.ShowDialog() != true) return;

            var title = string.IsNullOrWhiteSpace(input.InputText) ? $"第{next}章" : input.InputText!.Trim();
            _project.Chapters.Add(new Chapter
            {
                ChapterNumber = next,
                Title = title,
                Content = "",
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now,
                LastModified = DateTime.Now,
            });
            _project.RenumberChapters();

            NotifyChanged();
        }

        /// <summary>
        /// 结构变了：刷新卡片，并让主窗口刷新章节树、标记待保存。
        /// 不在这里直接写盘——保存交给主窗口的自动保存，避免两处各自 Save 打架。
        /// </summary>
        private void NotifyChanged()
        {
            Refresh();
            _changed();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
