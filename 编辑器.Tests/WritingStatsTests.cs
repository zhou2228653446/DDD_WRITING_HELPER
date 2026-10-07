using System;
using System.IO;
using System.Text.Json;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 写作统计的口径。
/// 这些规则都是刻意的取舍（删改不倒扣、今天没动笔从昨天数），
/// 一旦被改坏不会报错，只是数字悄悄不对——正需要测试钉住。
/// </summary>
public class WritingStatsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _projFile;

    public WritingStatsTests()
    {
        _projFile = _dir.File("book.tdxproj");
        File.WriteAllText(_projFile, "{}");
    }

    public void Dispose() => _dir.Dispose();

    private WritingStatsService New() => new(_projFile);

    [Fact]
    public void 首次记录_当天起点为当前字数_今日为0()
    {
        var s = New();
        s.Load();
        s.RecordTotal(1000);

        // 刚打开时写了多少还不清楚，不能把"打开时的总字数"算成今天的成果
        Assert.Equal(0, s.TodayWords);
    }

    [Fact]
    public void 继续写_今日增量正确()
    {
        var s = New();
        s.Load();
        s.RecordTotal(1000);
        s.RecordTotal(1500);

        Assert.Equal(500, s.TodayWords);
    }

    [Fact]
    public void 删稿后_增量回落()
    {
        var s = New();
        s.Load();
        s.RecordTotal(1000);
        s.RecordTotal(1500);
        s.RecordTotal(1200);

        Assert.Equal(200, s.TodayWords);
    }

    [Fact]
    public void 对外展示不倒扣_删到低于起点时仍为0()
    {
        var s = New();
        s.Load();
        s.RecordTotal(1000);
        s.RecordTotal(500);      // 删掉大半

        Assert.Equal(0, s.TodayWords);
    }

    [Fact]
    public void 连续天数_今天还没动笔时从昨天往前数()
    {
        WriteData(new WritingStatsData
        {
            BaselineDate = DaysAgo(1),
            BaselineWords = 1000,
            Days =
            {
                [DaysAgo(1)] = 500,
                [DaysAgo(2)] = 300,
            }
        });

        var s = New();
        s.Load();

        // 早上打开软件还没写一个字，不该显示"连续 0 天"——那很打击人
        Assert.Equal(2, s.StreakDays);
    }

    [Fact]
    public void 连续天数_中间断了一天就断签()
    {
        WriteData(new WritingStatsData
        {
            BaselineDate = DaysAgo(1),
            BaselineWords = 1000,
            Days =
            {
                [DaysAgo(1)] = 500,
                // 前天没写
                [DaysAgo(3)] = 300,
            }
        });

        var s = New();
        s.Load();
        Assert.Equal(1, s.StreakDays);
    }

    [Fact]
    public void 连续天数_没有任何记录时为0()
    {
        var s = New();
        s.Load();
        Assert.Equal(0, s.StreakDays);
    }

    [Fact]
    public void 跨天后重新起算_不把隔夜差额算成今天的()
    {
        WriteData(new WritingStatsData
        {
            BaselineDate = DaysAgo(1),
            BaselineWords = 1000,
            Days = { [DaysAgo(1)] = 500 }
        });

        var s = New();
        s.Load();
        s.RecordTotal(5000);     // 隔了一夜，总字数涨了 3500，但那不是今天写的

        Assert.Equal(0, s.TodayWords);
        Assert.Equal(1, s.StreakDays);   // 昨天的 500 字仍算数
    }

    [Fact]
    public void 累计动笔天数_只数有正增量的()
    {
        WriteData(new WritingStatsData
        {
            BaselineDate = DaysAgo(1),
            BaselineWords = 0,
            Days =
            {
                [DaysAgo(1)] = 500,
                [DaysAgo(2)] = 0,      // 开了软件但没写
                [DaysAgo(3)] = 120,
            }
        });

        var s = New();
        s.Load();
        Assert.Equal(2, s.ActiveDays);
    }

    [Fact]
    public void 最近N天_按日期正序返回()
    {
        var s = New();
        s.Load();
        var recent = s.RecentDays(7);

        Assert.Equal(7, recent.Count);
        Assert.Equal(Today(), recent[^1].Date);
    }

    private static string Today() => DateTime.Today.ToString("yyyy-MM-dd");
    private static string DaysAgo(int n) => DateTime.Today.AddDays(-n).ToString("yyyy-MM-dd");

    private void WriteData(WritingStatsData data)
    {
        var dir = System.IO.Path.Combine(_dir.Path, ".stats");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            System.IO.Path.Combine(dir, "stats.json"),
            JsonSerializer.Serialize(data));
    }
}
