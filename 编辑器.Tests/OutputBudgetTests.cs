using System;
using System.Reflection;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 输出预算（max_tokens）的两条兜底判定。
///
/// 统一把生成类任务的预算给到 10 万之后，这两条就成了"能不能用"的保险丝：
///   ① 判定错（把普通 400 当成超限）→ 白白多试一次，无害但浪费；
///   ② 判定漏（真的超限却没认出来）→ 硬上限更小的服务商一律失败，等于不能用。
/// 所以宁可让它更敏感一点，也不能漏。
/// </summary>
public class OutputBudgetTests
{
    private static readonly MethodInfo LooksLike =
        typeof(StreamingApiServiceBase).GetMethod(
            "LooksLikeOversizedMaxTokens",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool Judge(string? body) => (bool)LooksLike.Invoke(null, new object?[] { body })!;

    // ---- 应当判为「超限」：各家文案不同，抓的是"提到 max_tokens + 在抱怨大小" ----

    [Theory]
    [InlineData("{\"error\":{\"message\":\"max_tokens must be less than or equal to 65536\"}}")]
    [InlineData("{\"error\":{\"message\":\"The maximum number of tokens is 32768\"}}")]
    [InlineData("{\"error\":{\"message\":\"max_completion_tokens is too large: 100000\"}}")]
    [InlineData("max_tokens exceeds the maximum allowed value for this model")]
    [InlineData("{\"error\":\"max_tokens 不得超过 65536\"}")]
    [InlineData("{\"error\":\"max_tokens 超过上限\"}")]
    [InlineData("{\"error\":\"max_tokens 过大\"}")]
    [InlineData("max_tokens must be at most 32768")]
    [InlineData("max_tokens cannot be greater than 65536")]
    [InlineData("max_tokens is larger than the model supports")]
    public void 认得出_max_tokens_超限(string body) => Assert.True(Judge(body), body);

    // ---- 不能误判：别的 400 不应当被当成"参数给大了"而去重试 ----

    [Theory]
    [InlineData("{\"error\":{\"message\":\"insufficient balance\"}}")]
    [InlineData("{\"error\":{\"message\":\"The model `deepseek-v4` does not exist\"}}")]
    [InlineData("{\"error\":{\"message\":\"invalid api key\"}}")]
    [InlineData("{\"error\":{\"message\":\"context length exceeded: 200000 tokens\"}}")]  // 输入太长，不是 max_tokens
    [InlineData("")]
    [InlineData(null)]
    public void 不会把别的错误当成超限(string? body) => Assert.False(Judge(body), body ?? "(null)");

    // ---- 预算常量本身 ----

    [Fact]
    public void 生成类预算给到十万()
    {
        Assert.Equal(100_000, CompletionOptions.GenerousBudget);
        // CompletionOptions 的默认值也要跟上：调用点没显式设时不能退回 8192
        Assert.Equal(CompletionOptions.GenerousBudget, new CompletionOptions().MaxTokens);
    }

    [Fact]
    public void 改写类预算按原文长度增长且不小于下限()
    {
        // 空原文也要拿到下限（10 万），不能退回 8192 那一档
        Assert.Equal(CompletionOptions.GenerousBudget, CompletionOptions.BudgetForRewrite(null));
        Assert.Equal(CompletionOptions.GenerousBudget, CompletionOptions.BudgetForRewrite(""));
        // 超长原文：上限就是 10 万，不会再往上
        Assert.Equal(CompletionOptions.GenerousBudget,
            CompletionOptions.BudgetForRewrite(new string('字', 200_000)));
    }
}
