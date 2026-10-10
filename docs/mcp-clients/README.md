# 各 agent 客户端的 MCP 配置

`TdxClaw.Mcp.exe` 走的是 **stdio 传输**（MCP 最通用的一种），所以任何支持 MCP 的客户端都能接。
区别只在「配置文件放哪、叫什么名字」——下面逐个列出来。

> 最快的方式：双击仓库根的 **`install-mcp.bat`**，它会自动检测本机装了哪些客户端并写入配置（先备份）。
> 命令行等价：`python install-mcp.py --install all`；
> 只装某一家（哪怕客户端还没装也照装）：`python install-mcp.py --install codex`。
>
> 装完脚本会自己拉起服务器做一次 MCP 握手（initialize + tools/list）并报出工具数。
> 也可随时单独自检：`python install-mcp.py --verify`。

## 通用片段

除 VS Code 外，各家都是同一个结构（`mcpServers` + `command`）：

```json
{
  "mcpServers": {
    "tdxclaw-writing": {
      "command": "D:\\编辑器\\dist\\mcp\\TdxClaw.Mcp.exe",
      "args": []
    }
  }
}
```

## 逐家对照

| 客户端 | 配置文件位置 | 本目录里的片段 | 备注 |
|---|---|---|---|
| **Google Antigravity** | 全局 `~/.gemini/config/mcp_config.json`；工作区 `.agents/mcp_config.json` | `antigravity.json` | IDE 里 Settings ▸ Customizations ▸ Open MCP Config 也能打开同一个文件 |
| **Claude Desktop** | `%APPDATA%\Claude\claude_desktop_config.json` | `claude-desktop.json` | 改完必须完全退出 Claude 再启动 |
| **Cursor** | `~/.cursor/mcp.json`（全局）或项目 `.cursor/mcp.json` | `cursor.json` | — |
| **WorkBuddy** | `~/.workbuddy/mcp.json` | `workbuddy.json` | 装完到连接器管理页对新服务器点「信任」 |
| **Cline**（VS Code 扩展） | `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json` | 用 `claude-desktop.json` 同款 | — |
| **VS Code / Copilot** | 工作区 `.vscode/mcp.json` | `vscode-mcp.json` | ★ 这一家用 **`servers`** 键，不是 `mcpServers` |
| **Codex CLI** | `~/.codex/config.toml`（或项目级 `.codex/config.toml`） | `codex-config.toml` | TOML 格式；顶层键是 `mcp_servers`。★ 一键安装做**结构化合并**，不动你其它段；装完 `codex mcp list` 验证 |
| **Continue** | `~/.continue/config.yaml` | `continue.yaml` | YAML 格式 |

## Codex CLI 专属说明

Codex 用 TOML，且**顶层键是 `mcp_servers`**（不是 `mcpServers`），字段也换成
`command` / `args` / `startup_timeout_sec`。三步：

```bat
:: 1. 先装 Codex CLI（需要 Node.js）
npm install -g @openai/codex

:: 2. 在仓库根写入配置（结构化合并，会先备份；客户端没装也照样写）
python install-mcp.py --install codex

:: 3. 验证
codex mcp list
```

`startup_timeout_sec = 30` 是脚本刻意加的：Codex 默认只给 MCP 服务器 10 秒完成
握手，本服务器是自包含 .NET exe，冷启动常超过 10 秒，不改就会报
`handshaking with MCP server failed`。

Codex 也支持项目级配置——只想在某本书的目录里用，把 `codex-config.toml`
放到那个目录的 `.codex/config.toml` 即可。

想反向卸载：`python install-mcp.py --uninstall codex`（能真删掉那一段，不依赖 codex CLI）。

## 装完怎么确认通了

1. 客户端重启后在 MCP/工具列表里应该看到 `tdxclaw-writing`，展开有 28 个工具
   （`project_list` / `project_open` / `project_create` / `project_status` /
   `chapters_list` / `chapter_read` / `chapter_search` / `chapter_write` / `chapter_create` /
   `chapter_rename` / `chapter_delete` / `chapter_reorder` / `chapter_summary_set` /
   `settings_get` / `settings_set` / `settings_book_get` / `settings_book_set` /
   `characters_list` / `character_upsert` / `character_delete` / `characters_stats` /
   `memory_get` / `memory_set` / `snapshot_list` / `snapshot_restore` /
   `project_export` / `ai_write` / `ai_config_check`）。
2. 直接问它一句：**「帮我找找本机有哪些 .tdxproj 项目」** —— 它应当去调 `project_list`。
3. 想写稿时说：**「打开 D:\xx\yy.tdxproj，给第 3 章续写 800 字，写完追加进去」**。

## 排障

客户端启动 MCP 失败时，多半看不到 stderr（有些客户端直接吞掉）。这时落盘看日志：

```json
{
  "mcpServers": {
    "tdxclaw-writing": {
      "command": "D:\\编辑器\\dist\\mcp\\TdxClaw.Mcp.exe",
      "args": [],
      "env": { "TDX_MCP_LOG": "C:\\Users\\Administrator\\tdxclaw-mcp.log" }
    }
  }
}
```

也可以自己手验（不走客户端）：

```bash
printf '%s\n%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | ./dist/mcp/TdxClaw.Mcp.exe
```

或跑内置自检：`./dist/mcp/TdxClaw.Mcp.exe --selftest`。
