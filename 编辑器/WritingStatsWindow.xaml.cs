using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using 编辑器.Services;

namespace 编辑器
{
    /// <summary>
    /// 写作统计窗口：今日 / 连续天数 / 最近 14 天的量。
    ///
    /// 只做展示，不做编辑——统计是流水账，改它没有任何意义，只会让人骗自己。
    /// </summary>
    public partial class WritingStatsWindow : HandyControl.Controls.Window
    {
        private const int RecentDays = 14;
        private const double MaxBarWidth = 220;

        public WritingStatsWindow(WritingStatsService stats, int totalWords)
        {
            InitializeComponent();
            Owner = System.Windows.Application.Current.MainWindow;

            TodayValue.Text = stats.TodayWords.ToString("N0", CultureInfo.CurrentCulture);
            StreakValue.Text = stats.StreakDays.ToString(CultureInfo.CurrentCulture);
            ActiveValue.Text = stats.ActiveDays.ToString(CultureInfo.CurrentCulture);

            var days = stats.RecentDays(RecentDays);
            var peak = days.Count == 0 ? 0 : days.Max(d => d.Words);

            var rows = new List<DayRow>();
            foreach (var (date, words) in days)
            {
                var d = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                rows.Add(new DayRow
                {
                    DateText = d.ToString("M月d日 ddd", CultureInfo.CurrentCulture),
                    WordsText = words == 0 ? "—" : $"+{words:N0}",
                    // 没有峰值时（全是 0）就不画条，避免出现一排零宽矩形看着像坏了
                    BarWidth = peak <= 0 || words <= 0 ? 0 : Math.Max(3, words / (double)peak * MaxBarWidth),
                });
            }
            DayList.ItemsSource = rows;

            MetaText.Text = $"全书共 {totalWords:N0} 字 · 统计存于项目目录 .stats/";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        internal sealed class DayRow
        {
            public string DateText { get; init; } = "";
            public string WordsText { get; init; } = "";
            public double BarWidth { get; init; }
        }
    }
}
