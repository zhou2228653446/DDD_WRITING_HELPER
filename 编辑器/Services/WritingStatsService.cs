using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>
    /// 每日写作统计：今天写了多少字、连续写了几天。
    ///
    /// 为什么单独做，而不去算"项目总字数"：
    /// 总字数只告诉你"写了多少"，不告诉你"今天有没有写"。而写长篇真正需要被盯住的
    /// 是**节奏**——连续天数一断，往往就是这本书烂尾的开始。所以这里记的是
    /// **每天相对当天起点的净增量**，存在项目目录下的 .stats/ 里。
    ///
    /// 口径说明（几个容易吵起来的地方，这里定死）：
    ///   · 以"打开软件时的全书总字数"作为当天起点，之后 TodayWords = 当前总字数 − 起点；
    ///   · 删稿会把增量往下减（存真实值，可能为负），但对外展示时按 0 计——
    ///     删改不算倒扣字数，多数码字工具都是这个口径，否则改稿改到心碎；
    ///   · 只有 &gt; 0 的天才算"写了"，连续天数据此累加。
    /// </summary>
    public class WritingStatsService
    {
        private readonly string _file;
        private WritingStatsData _data = new();
        private DateTime _lastWrite = DateTime.MinValue;

        /// <summary>距上次落盘超过这么久才真的写文件——字数每敲一下都在变，不能每次都写。</summary>
        private static readonly TimeSpan WriteThrottle = TimeSpan.FromSeconds(20);

        public WritingStatsService(string projectFilePath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
            _file = Path.Combine(dir, ".stats", "stats.json");
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                var json = File.ReadAllText(_file);
                _data = JsonSerializer.Deserialize<WritingStatsData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WritingStatsData();
            }
            catch
            {
                _data = new WritingStatsData();   // 统计文件坏了不影响写作本身
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_file, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
                _lastWrite = DateTime.Now;
            }
            catch
            {
                // 统计写不进去无所谓，绝不能因为它打断写稿
            }
        }

        /// <summary>
        /// 记录当前全书总字数。每次字数变化都可调，内部自己节流。
        /// </summary>
        public void RecordTotal(int totalWords)
        {
            var today = Key(DateTime.Today);

            if (_data.BaselineDate != today)
            {
                // 新的一天：以"此刻的总字数"作为新起点。跨天前的成果已经落在 Days 里了，
                // 不把"隔了一夜的差额"算成今天写的。
                _data.BaselineDate = today;
                _data.BaselineWords = totalWords;
                if (!_data.Days.ContainsKey(today)) _data.Days[today] = 0;
                Save();
                return;
            }

            _data.Days[today] = totalWords - _data.BaselineWords;

            if (DateTime.Now - _lastWrite >= WriteThrottle) Save();
        }

        /// <summary>今天写的字数（删改不倒扣，最低 0）。</summary>
        public int TodayWords => Math.Max(0, WordsOn(DateTime.Today));

        /// <summary>
        /// 连续写稿天数。今天还没动笔时从昨天往前数——
        /// 否则每天早上打开软件都会先看到"连续 0 天"，很打击人。
        /// </summary>
        public int StreakDays
        {
            get
            {
                int streak = 0;
                var day = DateTime.Today;
                if (WordsOn(day) <= 0) day = day.AddDays(-1);
                while (WordsOn(day) > 0)
                {
                    streak++;
                    day = day.AddDays(-1);
                }
                return streak;
            }
        }

        /// <summary>最近 n 天（含今天）的记录，按日期正序。</summary>
        public List<(string Date, int Words)> RecentDays(int count)
        {
            var list = new List<(string, int)>();
            for (int i = count - 1; i >= 0; i--)
            {
                var d = DateTime.Today.AddDays(-i);
                list.Add((Key(d), Math.Max(0, WordsOn(d))));
            }
            return list;
        }

        /// <summary>累计写稿天数（有正增量的日子）。</summary>
        public int ActiveDays => _data.Days.Values.Count(v => v > 0);

        private int WordsOn(DateTime day) =>
            _data.Days.TryGetValue(Key(day), out var v) ? v : 0;

        private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    }

    /// <summary>统计数据的落盘形状。字段名就是 JSON 名，改了会读不到老数据。</summary>
    public class WritingStatsData
    {
        /// <summary>当天起点日期。跨天时整体重置。</summary>
        public string BaselineDate { get; set; } = "";

        /// <summary>当天起点时的全书总字数。</summary>
        public int BaselineWords { get; set; }

        /// <summary>每天相对当天起点的净增量，键为 yyyy-MM-dd。</summary>
        public Dictionary<string, int> Days { get; set; } = new();
    }
}
