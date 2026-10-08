using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using 编辑器.Services;

namespace 编辑器.Tests
{
    public class McpLiveBridgeTests
    {
        [Fact]
        public void SettingsPersistence_PreservesOtherKeysAndTogglesLiveSync()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "tdx_live_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var settingsFile = Path.Combine(tempDir, "settings.json");

            try
            {
                // 默认未创建文件时应为开启（true）
                Assert.True(McpLiveBridge.LoadLiveSyncEnabled(settingsFile));

                // 先写入 SkipWelcome
                McpLiveBridge.SaveBoolSetting("SkipWelcome", true, settingsFile);
                Assert.True(McpLiveBridge.LoadLiveSyncEnabled(settingsFile));

                // 关闭 McpLiveSyncEnabled
                McpLiveBridge.SaveLiveSyncEnabled(false, settingsFile);
                Assert.False(McpLiveBridge.LoadLiveSyncEnabled(settingsFile));

                // 验证 SkipWelcome 没有被冲掉
                using (var doc = JsonDocument.Parse(File.ReadAllText(settingsFile)))
                {
                    Assert.True(doc.RootElement.GetProperty("SkipWelcome").GetBoolean());
                    Assert.False(doc.RootElement.GetProperty(McpLiveBridge.SettingKey).GetBoolean());
                }

                // 重新开启
                McpLiveBridge.SaveLiveSyncEnabled(true, settingsFile);
                Assert.True(McpLiveBridge.LoadLiveSyncEnabled(settingsFile));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task NamedPipeServerAndPublish_DeliversLiveEventsInRealTime()
        {
            var customPipe = "TdxClaw.Mcp.TestPipe." + Guid.NewGuid().ToString("N");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var tcs = new TaskCompletionSource<McpLiveEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = McpLiveBridge.StartServer(ev =>
            {
                tcs.TrySetResult(ev);
            }, cts.Token, customPipe);

            // 等待管道进入监听
            await Task.Delay(80);

            var prevSuppressed = McpLiveBridge.Suppressed;
            McpLiveBridge.Suppressed = false;
            try
            {
                McpLiveBridge.Publish(new McpLiveEvent
                {
                    EventType = "stream",
                    ToolName = "ai_write",
                    TaskName = "continue",
                    ChapterNumber = 3,
                    WriteBack = true,
                    InputTokens = 1200,
                    OutputTokens = 450,
                    PreviewText = "风雪夜归人，灯火映孤城。",
                    Summary = "🤖 MCP AI 正在续写第 3 章（已生成 450 token）",
                }, customPipe);

                var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal("stream", received.EventType);
                Assert.Equal("ai_write", received.ToolName);
                Assert.Equal("continue", received.TaskName);
                Assert.Equal(3, received.ChapterNumber);
                Assert.True(received.WriteBack);
                Assert.Equal(1200, received.InputTokens);
                Assert.Equal(450, received.OutputTokens);
                Assert.Equal("风雪夜归人，灯火映孤城。", received.PreviewText);
            }
            finally
            {
                McpLiveBridge.Suppressed = prevSuppressed;
                cts.Cancel();
                try { await serverTask; } catch { }
            }
        }
    }
}
