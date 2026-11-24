# 项目总览 (PROJECT_OVERVIEW)

## 1. 项目定位
**AI 写作助手（DDD_WRITING_HELPER / TdxClaw）** 是一款面向长篇创作（小说、论文、公文、通用写作）的 Windows 桌面编辑器，采用**本地优先 + AI 深度集成**的设计。同一份书稿还可经内置的网页服务在浏览器里继续写，或由 agent 通过 MCP 直接读写——桌面、浏览器、agent 三端同源。

## 2. 技术栈
- **框架与运行时**：.NET 10 (`net10.0-windows`)，WPF (`UseWPF`)
- **UI 组件库**：HandyControl 3.5.1 + 自定义 Design Token（8 套配色 × 6 种材质 = 48 种观感）
- **导出引擎**：
  - Word (`.docx`)：DocumentFormat.OpenXml 3.5.1
  - PDF (`.pdf`)：QuestPDF 2024.12.3
  - TXT (`.txt`)：UTF-8 with BOM
- **架构风格**：纯事件处理式（无 MVVM、无第三方 DI/日志框架），核心逻辑位于 `MainWindow.xaml.cs` 与 `Services/`
- **测试体系**：xUnit 单元测试（`编辑器.Tests`）+ MCP 协议自检（`TdxClaw.Mcp.exe --selftest`）

## 3. 工程结构
解决方案 `编辑器.slnx` 包含五个子工程：
1. **`编辑器/`**（主程序）：
   - **窗口与控件**：`MainWindow`、`AiPanelControl`、`FloatingAiWindow`、`SettingsWindow`（五项设定）、`SettingsBookWindow`（12 章设定集）、`LiteratureWindow`（参考文献库）、`OutlineWindow`（大纲卡片墙）、`FindReplaceWindow`（全书查找替换）、`ReviewResultWindow`（一致性审稿）、`WritingStatsWindow`（写作统计）、`ApiSettingsWindow`、`AppearanceSettingsWindow` 等。
   - **Models**：`NovelProject`、`SettingsBook`、`Literature`、`AppearanceConfig`、`PathsConfig`。
   - **Services**：
     - AI 接口与容错：`IApiService`、`StreamingApiServiceBase`、`OpenAIService`、`AnthropicService`、`ApiProviders`（内置 25 家服务商）、`ModelCatalog`、`ModelCacheStore`
     - 提示词与技能：`AiPrompts`、`PromptPresets`（小说/论文/公文/通用 4 套预设 + 13 个可编辑槽位）、`SystemPromptStore`、`NovelSkills`
     - 上下文与记忆压缩：`ChatContextCompactor`、`CompactPolicy`、`MicroCompactor`、`ContextSummarizer`、`TokenEstimator`、`ModelContextCatalog`、`AiMemoryManager`、`ChatSessionStore`
     - 业务与导出：`WordExportService`、`PdfExportService`、`TxtExportService`、`SettingsBookTemplates`、`SettingsBookExportService`、`BibtexParser`、`LiteratureSearchService`、`CharacterAppearanceService`、`ProjectSnapshotManager`、`WritingStatsService`、`TextSearch`、`BookDeleter`（整本进回收站）、`EditorTypography`（网页正文排版，桌面/Web/MCP 共用）
     - 外观与动效：`AppearanceManager`、`ThemeTokens`、`Motion`
2. **`编辑器.Mcp/`**（MCP 服务器 `TdxClaw.Mcp`）：
   - 直接复用主程序 `Services/`，通过 JSON-RPC 2.0 over stdio 暴露 **31 个工具**与 `tdx://` 资源，支持外部 AI 客户端直接读写书稿、管理设定、删除作品、调用 `ai_write`、调整网页排版及导出。
3. **`编辑器.Tests/`**（单元测试）：
   - 171 项，覆盖章节操作与重排、原子保存与快照、全书查找替换、写作统计、删书回收站、排版校验、账户与试用等核心纯逻辑。
4. **`编辑器.Web/`**（网页版服务 `TdxClaw.Web`）：
   - ASP.NET Core：全部 `/api` 端点 + `POST /mcp` + SignalR 实时跟随；多账户（自用 / 试用）与独立工作区隔离；`wwwroot/` 为原生 JS 前端。
5. **`编辑器.AgentSetup/`**（Agent 接入助手）：
   - 自包含单文件 exe：登录换令牌后把 `tdxclaw` 写进各家 MCP 客户端配置。
