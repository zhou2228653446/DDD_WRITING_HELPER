# 项目总览 (PROJECT_OVERVIEW)

## 1. 项目定位
**AI 写作助手（DDD_WRITING_HELPER / TdxClaw）** 是一款面向长篇创作（小说、论文、公文、通用写作）的 Windows 桌面编辑器，采用**本地优先 + AI 深度集成**的设计。

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
解决方案 `编辑器.slnx` 包含三个子工程：
1. **`编辑器/`**（主程序）：
   - **窗口与控件**：`MainWindow`、`AiPanelControl`、`FloatingAiWindow`、`SettingsWindow`（五项设定）、`SettingsBookWindow`（12 章设定集）、`LiteratureWindow`（参考文献库）、`OutlineWindow`（大纲卡片墙）、`FindReplaceWindow`（全书查找替换）、`ReviewResultWindow`（一致性审稿）、`WritingStatsWindow`（写作统计）、`ApiSettingsWindow`、`AppearanceSettingsWindow` 等。
   - **Models**：`NovelProject`、`SettingsBook`、`Literature`、`AppearanceConfig`、`PathsConfig`。
   - **Services**：
     - AI 接口与容错：`IApiService`、`StreamingApiServiceBase`、`OpenAIService`、`AnthropicService`、`ApiProviders`（内置 25 家服务商）、`ModelCatalog`、`ModelCacheStore`
     - 提示词与技能：`AiPrompts`、`PromptPresets`（小说/论文/公文/通用 4 套预设 + 13 个可编辑槽位）、`SystemPromptStore`、`NovelSkills`
     - 上下文与记忆压缩：`ChatContextCompactor`、`CompactPolicy`、`MicroCompactor`、`ContextSummarizer`、`TokenEstimator`、`ModelContextCatalog`、`AiMemoryManager`、`ChatSessionStore`
     - 业务与导出：`WordExportService`、`PdfExportService`、`TxtExportService`、`SettingsBookTemplates`、`SettingsBookExportService`、`BibtexParser`、`LiteratureSearchService`、`CharacterAppearanceService`、`ProjectSnapshotManager`、`WritingStatsService`、`TextSearch`
     - 外观与动效：`AppearanceManager`、`ThemeTokens`、`Motion`
2. **`编辑器.Mcp/`**（MCP 服务器 `TdxClaw.Mcp`）：
   - 直接复用主程序 `Services/`，通过 JSON-RPC 2.0 over stdio 暴露 28 个工具与 `tdx://` 资源，支持外部 AI 客户端直接读写书稿、管理设定、调用 `ai_write` 及导出。
3. **`编辑器.Tests/`**（单元测试）：
   - 覆盖章节操作与重排、原子保存与快照、全书查找替换、写作统计等核心纯逻辑。
