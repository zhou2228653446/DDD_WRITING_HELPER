using 编辑器.Services;

namespace 编辑器.Mcp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // --selftest：不起 stdio 会话，跑一遍内置自检（给开发时验证用，正常模式下不触发）
        if (args.Length > 0 && args[0] == "--selftest")
            return await SelfTest.RunAsync();

        // 提示词方案/技能配置与界面共用同一份文件，进程启动时先载入，
        // 否则 AiPrompts.Task.* 会取不到用户改过的覆写文本。
        try
        {
            AiPrompts.Store?.Load();
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[mcp] 载入提示词配置失败（将用内置默认）：{ex.Message}"); }
            catch { }
        }

        await McpServer.RunAsync();
        return 0;
    }
}
