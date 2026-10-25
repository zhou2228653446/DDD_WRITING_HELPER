using 编辑器.Services;
using Xunit;

namespace 编辑器.Tests;

/// <summary>
/// 采样参数档位。这里钉的是"什么任务该有多自由"，改错了不会报错，
/// 只会在几个月后发现"怎么又变成套话了"——所以要用测试锁住。
/// </summary>
public class SamplingPresetTests
{
    [Fact]
    public void 续写要高温加重复惩罚()
    {
        var s = SamplingPresets.For("continue");

        Assert.Equal(0.95, s.Temperature);
        Assert.Equal(0.95, s.TopP);
        Assert.True(s.Frequency > 0, "创意任务必须有频率惩罚");
        Assert.True(s.Presence > 0, "创意任务必须有存在惩罚");
    }

    [Fact]
    public void 审稿必须保持稳定()
    {
        var s = SamplingPresets.For("review");

        Assert.True(s.Temperature <= 0.3, $"审稿温度 {s.Temperature} 太高，结论会飘");
        Assert.Equal(0, s.Frequency);
        Assert.Equal(0, s.Presence);
    }

    [Fact]
    public void 结构化任务在中间档()
    {
        var outline = SamplingPresets.For("outline");
        var polish = SamplingPresets.For("polish");

        Assert.InRange(outline.Temperature, 0.6, 0.8);
        Assert.InRange(polish.Temperature, 0.7, 0.85);
        Assert.True(polish.Temperature < SamplingPresets.For("continue").Temperature);
    }

    [Fact]
    public void 旧参数档_用于_AB_对照()
    {
        var creative = SamplingPresets.Legacy("continue");
        var review = SamplingPresets.Legacy("review");

        Assert.Equal(0.70, creative.Temperature);
        Assert.Null(creative.TopP);          // 旧版不传 top_p
        Assert.Equal(0, creative.Frequency); // 旧版没有惩罚
        Assert.Equal(0.30, review.Temperature);
    }

    [Fact]
    public void 套到_options_上时四个字段都生效()
    {
        var o = SamplingPresets.Apply(new CompletionOptions(), "continue");

        Assert.Equal(0.95, o.Temperature);
        Assert.Equal(0.95, o.TopP);
        Assert.Equal(0.25, o.FrequencyPenalty);
        Assert.Equal(0.15, o.PresencePenalty);
    }
}
