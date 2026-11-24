# 变更日志 (CHANGELOG)

## 2026-10-08
- 初始化项目记忆目录 `memory/`（创建 `PROJECT_OVERVIEW.md`、`CONVERSATION_LOG.md`、`CHANGELOG.md`）。
- 完善 MCP 服务器对 Antigravity 的适配：
  - `编辑器.Mcp/McpServer.cs`：为 `ToolSchema.required` 添加 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` 避免输出非法的 `"required": null`；启用 `UnsafeRelaxedJsonEscaping`；在 `initialize` 响应中补充 `instructions` 引导说明，并在 `resources/templates/list` 中暴露 `tdx://` 资源模板。
  - `编辑器.Mcp/Resources.cs`：新增 `Templates()` 返回三类 `tdx://` 资源 URI 模板。
  - `编辑器.Mcp/NovelTools.cs`：`ListProjects` 默认扫描路径纳入由 `AppContext.BaseDirectory` 上推的仓库根目录。
  - `编辑器.Mcp/AiTools.cs`：`IsAnthropic` 改为复用 `ApiProviders.ResolveWire` 保持与桌面端协议判定完全一致。
  - `编辑器.Mcp/SelfTest.cs`：新增 `initialize.instructions` 与无 `"required":null` 的自检断言（共 62 项断言通过）。
  - `install-mcp.py`：显式将 `stdout`/`stderr` 重配为 UTF-8，修复 Windows GBK 控制台下打印 emoji 报 `UnicodeEncodeError` 中断安装的问题。
  - `docs/mcp-clients/README.md`、`README.md`：同步更新工具数量（28 个）与自检断言数。
- 修复火山方舟（Agent Plan / Coding Plan）预设与双协议支持：
  - `编辑器/Services/ApiProviders.cs`：新增 `volces-plan`（「火山方舟（Agent Plan）」）预设，默认端点 `https://ark.cn-beijing.volces.com/api/plan/v3/chat/completions`，默认模型 `ark-code-latest`，内置 15 个官方支持模型（含 `doubao-seed-2.1-pro`、`deepseek-v4-pro`、`glm-5.3`、`kimi-k3`、`minimax-m3` 等）；新增 `ResolveEndpoint`/`NormalizeEndpoint` 自动将控制台复制的 Base URL（`.../api/plan/v3`、`.../api/plan`、`.../api/coding/v3`、`.../v1`）补齐为完整对话端点；支持方舟下填 Anthropic 端点自动切换 `AnthropicMessages` 协议并统一使用 `Bearer` 认证；新增按量端点与 Plan 端点混用时的显式警告。
  - `编辑器/Services/ModelCatalog.cs`：`DeriveModelsUrl` 支持从 `.../api/plan`、`.../api/coding` 推导 `.../v1/models`；`NonChatMarkers` 补充 `seedream`/`seedance`。
  - `编辑器/Services/StreamingApiServiceBase.cs`、`编辑器/ApiSettingsWindow.xaml.cs`：发请求与测试连接统一走 `ApiProviders.ResolveEndpoint`；`CurrentModelLooksUnknown` 放行 `ark-code-latest` 与 `ep-` 推理接入点 ID；补充思考耗尽 `max_tokens` 的提示。
  - `编辑器/Services/AnthropicService.cs`：`ReadContentDelta` 兼容 `thinking_delta` 事件类型。
  - `编辑器.Mcp/AiTools.cs`：`ai_config_check` 连通性探测 `MaxTokens` 提升至 256，防止强制思考模型耗尽 16 token 返回空正文。
  - 重新发布 `dist\编辑器.exe` 与 `dist\mcp\TdxClaw.Mcp.exe`。
- 基于 10,000 字短篇小说实测反馈完成 6 项核心改进 + 外观设置实时预览与取消回滚：
  - **输出上限（`MaxTokens`）翻倍提升**：
    - `编辑器/Services/IApiService.cs`：`CompletionOptions.MaxTokens` 默认值由 `1000` 提升至 `8192`；`BudgetForRewrite` 下限由 `1200` 提升至 `8192`、上限由 `8000` 提升至 `16384`。
    - `编辑器/MainWindow.xaml.cs`：续写、对话、AI 审稿、设定集章节生成 `MaxTokens` 提升至 `16384`；全文大纲、章节大纲、人物设定、背景设定、文风设定、AI 记忆更新 `MaxTokens` 提升至 `8192`；叙事视角生成提升至 `4096`。
    - `编辑器.Mcp/AiTools.cs`：`continue` / `expand` / `chat` / `setting_book` 默认 `maxTokens` 提升至 `16384`，`polish` / `review` 动态范围提升至 `8192 ~ 16384`，其余设定生成任务默认提升至 `8192`。
  - **上下文全方位打通（人物卡 + 设定集 + 章节梗概链 + 上章末尾）**：
    - `编辑器/Models/NovelProject.cs`：新增 `BuildEffectiveCharacterSettings()`（自动合并自由文本与结构化人物卡）、`BuildSettingsBookContextBlock()`（提取已勾选导出的设定集非空章节）、`BuildPriorChapterBrief()`（优先注入前序章节 `Chapter.Summary` 梗概链 + 紧邻上一章末尾 800 字原文，解决此前只截上章开头 800 字导致丢失结尾悬念的问题）。
    - `编辑器/MainWindow.xaml.cs`、`编辑器.Mcp/AiTools.cs`、`编辑器.Mcp/NovelTools.cs`：统一接入上述三项上下文组装方法，并在 `project_open`、`project_status`、`settings_get` 中识别结构化人物卡。
  - **结构化人物卡扩展与参数容错**：
    - `Character` 模型与 `character_upsert` / `characters_list` 新增 `abilities`（能力）、`relationships`（关系）、`notes`（备注）字段；`NovelTools.Int` 支持解析 `"27"` 或 `"27岁"` 等字符串整数，`NovelTools.Bool` 支持字符串布尔值。
  - **批量设定写入与建书路径容错**：
    - `settings_set` 支持单次调用同时传入多项设定字段（`full_outline`、`chapter_outline`、`characters`、`background`、`writing_style`、`narrative_viewpoint`、`description`）批量写入。
    - `project_create` 支持 `path` 别名、自动拆分完整 `.tdxproj` 文件路径，并支持建书时顺带写入初始贯穿设定。
  - **并发安全锁与快照防刷屏**：
    - `McpServer.cs` 在 `DispatchAsync` 外层加 `SemaphoreSlim(1, 1)` 串行锁防止并发工具调用踩踏内存与文件指纹；修复 `CreateChapter` 重复快照、`ReorderChapter` 与 `UpsertCharacter` 快照晚于内存修改的问题；`Session.Snapshot` 对 30 秒内连续高频轻量修改（设定/人物卡/梗概）自动去重合并。
  - **外观设置随改随变 + 取消还原**：
    - `编辑器/AppearanceSettingsWindow.xaml`、`AppearanceSettingsWindow.xaml.cs`、`MainWindow.xaml.cs`：新增实时预览回调 `_onPreview`，切换配色、材质、滑块强度、背景图开关/浏览/移除时立即在主窗口实时生效；新增「取消」按钮并处理右上角关闭/Esc，未点「确定」关闭时自动恢复打开前的原始外观且不写盘。
- 修复 `deploy.bat` 双击或命令行执行失败问题：
  - 将 `deploy.bat` 改为纯 ASCII + `CRLF` 批处理包装层调用 PowerShell 执行发布逻辑，彻底避开 Windows `cmd.exe` 在 GBK/UTF-8 代码页下解析多字节中文与 `LF` 换行时的字节偏移错位 Bug（此前导致 `编辑器\编辑器.csproj` 被解析为乱码报 `MSB1009`）。
  - 发布前自动停止后台驻留的 `TdxClaw.Mcp.exe` 进程防止文件锁冲突，并自动清理 `dist\` 根目录多余散装文件（仅保留单文件 `dist\编辑器.exe` 与 `dist\mcp\`）。
- 新增 **MCP 操作界面实时可视化与跟随同步（MCP Live Sync）** 及设置开关：
  - `编辑器/Services/McpLiveBridge.cs`：新增跨进程实时事件桥（基于本地命名管道 `TdxClaw.Mcp.LiveBridge` + 后台非阻塞通道 `Channel<McpLiveEvent>`），支持 `start`、`stream`、`done`、`error` 四阶段事件推送，并在 `settings.json` 中持久化 `McpLiveSyncEnabled` 开关（默认开启，且与 `SkipWelcome` 等字段无损共存）。
  - `编辑器/Services/IApiService.cs`、`OpenAIService.cs`、`AnthropicService.cs`：在 `CompletionOptions` 中新增 `OnStreamText` 流式累积文本回调。
  - `编辑器.Mcp/McpServer.cs`、`编辑器.Mcp/AiTools.cs`、`编辑器.Mcp/SelfTest.cs`：在全部 MCP 工具调用前后及 `ai_write` 流式生成期间（180ms 节流）自动广播实时事件（自检 `--selftest` 期间自动静默，不打扰桌面端）。
  - `编辑器/MainWindow.McpLive.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`ApiSettingsWindow.xaml`、`ApiSettingsWindow.xaml.cs`：
    - 在「AI 设置」窗口底部与顶部菜单「视图(V) → 实时跟随 MCP 操作(M)」双入口提供实时同步开关；
    - 开启时，外部 AI 通过 MCP 新建/打开项目、创建/改写章节、修改设定/设定集/人物卡、或调用 `ai_write` 流式生成时，桌面端自动切换/原地无闪烁刷新项目、打开目标章节标签页、在右侧「AI 回复」框与正文编辑框中实时滚动展示流式生成内容与 Token 进度，并在底部状态栏点亮 `MCP 执行中 / MCP 已同步` 徽章与顶部通知横幅；
    - 同步期间自动压制 `_suppressDirtyTracking`，防止桌面端 60 秒自动保存与 MCP 写入产生 `IsStale` 冲突。
  - `编辑器.Tests/McpLiveBridgeTests.cs`：新增设置持久化与命名管道实时推送端到端单元测试（总计 53 项单元测试 + 67 项 MCP 自检全部通过）。


