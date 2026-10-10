using System.Globalization;

namespace 编辑器.Services;

/// <summary>
/// 项目「版本号」（stamp）在**前后端之间**的表示。
///
/// ★ 为什么必须走字符串，而不是直接发数字：
///   stamp 是 <c>File.GetLastWriteTimeUtc().Ticks</c>，量级 6.4e17；
///   而 JS 的 Number 只能精确表示到 2^53-1（≈9.0e15）。
///   一旦作为 JSON 数字发到浏览器再原样传回来，中间会被四舍五入 ——
///   服务端一比对必然不等，表现就是**每次从浏览器保存都被判成
///   "这本书已经被别处改过了"**，用户敲的字一个字也存不进去，
///   而且界面上只有一句含糊的报错，根本看不出是精度问题。
///
///   字符串往返无损；前端对 stamp 只做透传、不做算术，所以换成字符串是安全的。
///
/// 桌面端不走 JSON，用不到这里（它是进程内直接比 long）。
/// </summary>
public static class StampToken
{
    /// <summary>把 ticks 变成可安全传输的字符串。</summary>
    public static string From(long ticks) => ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>还原成 ticks。认不出来（空 / 旧格式 / 被改坏）就返回 null，按"没带版本号"处理。</summary>
    public static long? Parse(string? token) =>
        long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// 这个 ticks 能不能安全地做成 JSON 数字给浏览器？
    /// 只用于自检与测试——业务代码永远用字符串，不要依赖这个判断。
    /// </summary>
    public static bool IsJavaScriptSafe(long ticks) =>
        ticks is >= -9_007_199_254_740_991L and <= 9_007_199_254_740_991L;
}
