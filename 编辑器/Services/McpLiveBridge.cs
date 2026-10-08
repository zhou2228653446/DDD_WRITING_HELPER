using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace 编辑器.Services
{
    /// <summary>
    /// MCP 实时操作事件：当外部 AI Agent 通过 <c>TdxClaw.Mcp</c> 操作项目时，
    /// 向桌面端（<see cref="MainWindow"/>）实时推送当前动作、流式生成进度与项目变更。
    /// </summary>
    public sealed class McpLiveEvent
    {
        /// <summary>事件阶段：start（开始执行）/ stream（AI 流式生成中）/ done（执行完成）/ error（执行出错）。</summary>
        public string EventType { get; set; } = "done";

        /// <summary>MCP 工具名（如 project_open / chapter_create / chapter_write / ai_write / settings_set 等）。</summary>
        public string ToolName { get; set; } = "";

        /// <summary>ai_write 的子任务名（如 continue / polish / expand / outline 等）。</summary>
        public string? TaskName { get; set; }

        /// <summary>当前操作的 .tdxproj 完整路径。</summary>
        public string? ProjectPath { get; set; }

        /// <summary>目标章节号（> 0 表示定位到该章）。</summary>
        public int ChapterNumber { get; set; }

        /// <summary>目标设定字段或设定集章节键（如 full_outline / characters / world 等）。</summary>
        public string? SettingField { get; set; }

        /// <summary>供界面状态栏与通知横幅展示的中文简述。</summary>
        public string Summary { get; set; } = "";

        /// <summary>流式生成中的实时正文预览，或工具输出摘要（展示在右侧 AI 回复框及章节编辑区）。</summary>
        public string? PreviewText { get; set; }

        /// <summary>流式生成时的输入 token 数。</summary>
        public int InputTokens { get; set; }

        /// <summary>流式生成时的输出 token 数。</summary>
        public int OutputTokens { get; set; }

        /// <summary>本次操作是否修改了磁盘上的 .tdxproj（或切换了项目），需要桌面端重载同步。</summary>
        public bool ProjectModified { get; set; }

        /// <summary>ai_write 是否启用了 writeBack。</summary>
        public bool WriteBack { get; set; }

        /// <summary>写入模式（append / replace）。</summary>
        public string? WriteMode { get; set; }

        /// <summary>UTC 时间戳（Ticks）。</summary>
        public long TimestampTicks { get; set; } = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// 桌面端与 MCP 进程之间的实时同步桥（基于本地命名管道 + 非阻塞后台队列）。
    /// <list type="bullet">
    ///   <item>MCP 侧调用 <see cref="Publish"/>：立即入队返回，不阻塞工具执行；桌面端未启动时静默丢弃。</item>
    ///   <item>桌面端调用 <see cref="StartServer"/>：在后台监听命名管道，收到事件后回调 UI 线程同步展示。</item>
    ///   <item>设置项持久化在 <c>settings.json</c> 的 <c>McpLiveSyncEnabled</c> 字段（默认开启）。</item>
    /// </list>
    /// </summary>
    public static class McpLiveBridge
    {
        public const string PipeName = "TdxClaw.Mcp.LiveBridge";
        public const string SettingKey = "McpLiveSyncEnabled";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly JsonSerializerOptions _prettyJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// 自检（--selftest）时置为 true，避免自检临时项目把用户开着的编辑器窗口刷屏。
        /// </summary>
        public static bool Suppressed { get; set; }

        /// <summary>进程内事件钩子（供单元测试或同进程调试直接订阅）。</summary>
        public static event Action<McpLiveEvent>? InProcessEvent;

        private static readonly Channel<McpLiveEvent> _sendChannel =
            Channel.CreateUnbounded<McpLiveEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private static int _senderStarted;

        // ==================================================================
        // 设置项读写（与 settings.json 中的 SkipWelcome 等字段和平共处）
        // ==================================================================

        public static string DefaultSettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw", "settings.json");

        /// <summary>读取「是否实时显示并跟随 MCP 操作」设置（默认 true）。</summary>
        public static bool LoadLiveSyncEnabled(string? settingsFilePath = null)
        {
            var path = string.IsNullOrWhiteSpace(settingsFilePath) ? DefaultSettingsFilePath : settingsFilePath!;
            try
            {
                if (!File.Exists(path)) return true;
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(SettingKey, out var prop) &&
                    (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
                {
                    return prop.GetBoolean();
                }
            }
            catch
            {
                // 配置文件损坏或不可读时默认开启
            }
            return true;
        }

        /// <summary>保存「是否实时显示并跟随 MCP 操作」设置，保留 settings.json 中的其他已有字段。</summary>
        public static void SaveLiveSyncEnabled(bool enabled, string? settingsFilePath = null)
        {
            var path = string.IsNullOrWhiteSpace(settingsFilePath) ? DefaultSettingsFilePath : settingsFilePath!;
            try
            {
                var dict = ReadSettingsDict(path);
                dict[SettingKey] = enabled;
                WriteSettingsDict(path, dict);
            }
            catch
            {
                // 写配置失败不打断主流程
            }
        }

        /// <summary>更新 settings.json 中的任意布尔字段，保留其他字段不丢失。</summary>
        public static void SaveBoolSetting(string key, bool value, string? settingsFilePath = null)
        {
            var path = string.IsNullOrWhiteSpace(settingsFilePath) ? DefaultSettingsFilePath : settingsFilePath!;
            try
            {
                var dict = ReadSettingsDict(path);
                dict[key] = value;
                WriteSettingsDict(path, dict);
            }
            catch
            {
            }
        }

        private static Dictionary<string, object?> ReadSettingsDict(string path)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!File.Exists(path)) return dict;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        _ => prop.Value.GetRawText(),
                    };
                }
            }
            catch
            {
            }
            return dict;
        }

        private static void WriteSettingsDict(string path, Dictionary<string, object?> dict)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(dict, _prettyJsonOptions));
        }

        // ==================================================================
        // 客户端发送（MCP 进程调用，非阻塞）
        // ==================================================================

        /// <summary>
        /// 推送一条 MCP 实时事件。调用方零等待；后台单线程按严格时间序通过命名管道发往桌面端。
        /// </summary>
        public static void Publish(McpLiveEvent ev, string? pipeName = null)
        {
            if (Suppressed || ev == null) return;

            try { InProcessEvent?.Invoke(ev); } catch { }

            if (!string.IsNullOrEmpty(pipeName) && pipeName != PipeName)
            {
                // 指定了自定义管道名（如单元测试隔离管道）：直接异步发往该管道
                _ = Task.Run(() => SendSingleAsync(ev, pipeName!));
                return;
            }

            EnsureSenderStarted();
            _sendChannel.Writer.TryWrite(ev);
        }

        private static void EnsureSenderStarted()
        {
            if (Interlocked.CompareExchange(ref _senderStarted, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                NamedPipeClientStream? client = null;
                StreamWriter? writer = null;

                await foreach (var ev in _sendChannel.Reader.ReadAllAsync())
                {
                    if (Suppressed) continue;

                    try
                    {
                        if (client == null || !client.IsConnected)
                        {
                            DisposeClient(ref client, ref writer);
                            client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                            await client.ConnectAsync(50);
                            writer = new StreamWriter(client, new UTF8Encoding(false))
                            {
                                AutoFlush = true,
                                NewLine = "\n",
                            };
                        }

                        var line = JsonSerializer.Serialize(ev, _jsonOptions);
                        await writer!.WriteLineAsync(line);
                    }
                    catch
                    {
                        // 桌面端未启动或已关闭：静默断开，下次事件再试
                        DisposeClient(ref client, ref writer);
                    }
                }
            });
        }

        private static async Task SendSingleAsync(McpLiveEvent ev, string targetPipeName)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", targetPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(200);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                await writer.WriteLineAsync(JsonSerializer.Serialize(ev, _jsonOptions));
            }
            catch
            {
            }
        }

        private static void DisposeClient(ref NamedPipeClientStream? client, ref StreamWriter? writer)
        {
            try { writer?.Dispose(); } catch { }
            try { client?.Dispose(); } catch { }
            writer = null;
            client = null;
        }

        // ==================================================================
        // 服务端监听（桌面端 MainWindow 调用）
        // ==================================================================

        /// <summary>
        /// 启动命名管道监听服务，持续接收 MCP 进程发来的实时事件。
        /// </summary>
        public static Task StartServer(Action<McpLiveEvent> onEvent, CancellationToken ct, string? pipeName = null)
        {
            var actualPipe = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName!;

            return Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    NamedPipeServerStream? server = null;
                    try
                    {
                        server = new NamedPipeServerStream(
                            actualPipe,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(ct);

                        // 连接成功后立即交给独立任务读取，主循环马上创建下一个监听实例，保证无缝衔接
                        var connectedServer = server;
                        server = null;
                        _ = ReadClientLoopAsync(connectedServer, onEvent, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        server?.Dispose();
                        break;
                    }
                    catch
                    {
                        server?.Dispose();
                        try { await Task.Delay(200, ct); } catch { break; }
                    }
                }
            }, ct);
        }

        private static async Task ReadClientLoopAsync(
            NamedPipeServerStream server, Action<McpLiveEvent> onEvent, CancellationToken ct)
        {
            using (server)
            {
                try
                {
                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    while (!ct.IsCancellationRequested && server.IsConnected)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var ev = JsonSerializer.Deserialize<McpLiveEvent>(line, _jsonOptions);
                            if (ev != null)
                            {
                                onEvent(ev);
                            }
                        }
                        catch
                        {
                            // 单行解析失败不影响后续事件
                        }
                    }
                }
                catch
                {
                    // 客户端断开或取消
                }
            }
        }
    }
}
