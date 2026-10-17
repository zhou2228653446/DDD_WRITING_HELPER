using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;
using 编辑器.Web.Services;

namespace 编辑器.Tests;

/// <summary>
/// 账户系统里最容易出事的两件事：**改密码**和**改有效期**。
/// 它们看起来相近，语义完全不同——
///   轮换（RotateTrialNow）换掉密码，已经发出去的立刻失效；
///   续期（RenewTrial）只把有效期重新算满，密码一个字都不动。
/// 用户把密码发给试用者之后想"再保 10 天"，要的正是后者；
/// 搞混的代价是所有人都收到一次"密码已失效"。
/// </summary>
public class AccountStoreTests
{
    private const int Days = 10;

    /// <summary>把 accounts.json 里的 rotatedAt 拨到 N 天前，模拟"快到期"。</summary>
    private static void AgeTrial(string dir, int daysAgo)
    {
        var file = Path.Combine(dir, "accounts.json");
        var node = JsonNode.Parse(File.ReadAllText(file))!;
        node["trial"]!["rotatedAt"] = DateTime.Now.AddDays(-daysAgo).ToString("O");
        File.WriteAllText(file, node.ToJsonString());
    }

    private static AccountStore NewStore(TempDir tmp, out string pwd)
    {
        var store = new AccountStore(tmp.Path);
        store.Initialize("test123456");
        (pwd, _, _) = store.CurrentTrial();
        return store;
    }

    /// <summary>剩余天数——口径必须和 /api/auth/me 里一致（向上取整）。
    /// 注意 CurrentTrial() 的第三个返回值是**周期**（rotateDays），不是剩余。</summary>
    private static int DaysLeft(DateTime next) =>
        Math.Max(0, (int)Math.Ceiling((next - DateTime.Now).TotalDays));

    [Fact]
    public void 续期不换密码只把有效期推满()
    {
        using var tmp = new TempDir();
        var store = NewStore(tmp, out var pwd0);

        AgeTrial(tmp.Path, Days - 1);           // 只剩 1 天
        var (pwd1, next1, period1) = store.CurrentTrial();
        Assert.Equal(pwd0, pwd1);               // 还没到期，不该轮换
        Assert.Equal(Days, period1);            // 周期没变
        Assert.Equal(1, DaysLeft(next1));       // 但只剩 1 天

        var (pwd2, next2, period2) = store.RenewTrial();
        Assert.Equal(pwd0, pwd2);               // ★ 密码必须一个字节都不变
        Assert.Equal(Days, period2);
        Assert.Equal(Days, DaysLeft(next2));    // 有效期重新算满
        Assert.True(next2 > next1);             // 到期时间确实往后推了

        // 落盘了没有：重新读一遍（不许只在内存里改了）
        var reread = new AccountStore(tmp.Path);
        var (pwd3, next3, _) = reread.CurrentTrial();
        Assert.Equal(pwd0, pwd3);
        Assert.Equal(Days, DaysLeft(next3));
        Assert.Equal(next2, next3);
    }

    [Fact]
    public void 轮换会换掉密码_这是与续期的唯一区别()
    {
        using var tmp = new TempDir();
        var store = NewStore(tmp, out var pwd0);

        var pwd1 = store.RotateTrialNow();
        Assert.NotEqual(pwd0, pwd1);
        Assert.Equal(pwd1, store.CurrentTrial().Password);

        // 而且旧密码真的失效了
        Assert.False(store.VerifyTrial(pwd0));
        Assert.True(store.VerifyTrial(pwd1));
    }

    [Fact]
    public void 到期后自动轮换_过期密码立刻作废()
    {
        using var tmp = new TempDir();
        var store = NewStore(tmp, out var pwd0);

        AgeTrial(tmp.Path, Days + 1);           // 已经过了一天
        Assert.False(store.VerifyTrial(pwd0));  // 校验顺带触发轮换，旧密码当场失效

        var (pwd1, _, left) = store.CurrentTrial();
        Assert.NotEqual(pwd0, pwd1);
        Assert.Equal(Days, left);               // 新一轮从满期开始
    }

    [Fact]
    public void 自用密码存的是哈希_不落明文()
    {
        using var tmp = new TempDir();
        var store = NewStore(tmp, out _);

        var raw = File.ReadAllText(Path.Combine(tmp.Path, "accounts.json"));
        Assert.DoesNotContain("test123456", raw);
        Assert.True(store.VerifySelf("test123456"));
        Assert.False(store.VerifySelf("wrong-password"));
    }

    [Fact]
    public void 改自用密码后旧密码失效()
    {
        using var tmp = new TempDir();
        var store = NewStore(tmp, out _);

        store.ChangeSelfPassword("test123456", "another123");
        Assert.False(store.VerifySelf("test123456"));
        Assert.True(store.VerifySelf("another123"));
        Assert.Throws<InvalidOperationException>(() =>
            store.ChangeSelfPassword("test123456", "whatever123"));   // 原密码已不对
    }
}
