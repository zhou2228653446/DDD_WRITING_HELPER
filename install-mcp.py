#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把 TdxClaw 写作助手的 MCP 服务器装进各个 agent 客户端。

用法（在仓库根目录）：
    python install-mcp.py                 列出本机检测到哪些客户端、各自的配置在哪
    python install-mcp.py --install all   给所有检测到的客户端写入（先自动备份）
    python install-mcp.py --install cursor claude
    python install-mcp.py --uninstall all 移除

设计取舍：
- JSON 类配置做**合并**而不是覆盖：你原本挂着的其它 MCP 服务器会保留，
  只新增/替换名为 tdxclaw-writing 的那一条。
- 写前一定备份成 <原文件>.bak-<时间戳>。
- TOML / YAML（Codex、Continue）不做结构化解析，而是**追加片段**，
  文件里已经有 tdxclaw-writing 就跳过，避免动到别人的配置。
- exe 路径由本脚本位置推导（仓库根/dist/mcp/TdxClaw.Mcp.exe），
  不在命令行里传中文路径，绕开 Windows 控制台编码坑。
"""

import argparse
import json
import os
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
EXE = ROOT / "dist" / "mcp" / "TdxClaw.Mcp.exe"
NAME = "tdxclaw-writing"

# kind: json（结构化合并）/ toml / yaml（片段追加）
# key : JSON 里服务器表所在的键 —— VS Code 用 "servers"，其余用 "mcpServers"
CLIENTS = [
    dict(id="antigravity", name="Google Antigravity",
         path=lambda: Path.home() / ".gemini" / "config" / "mcp_config.json",
         kind="json", key="mcpServers",
         note="IDE：Settings ▸ Customizations ▸ Open MCP Config"),
    dict(id="claude", name="Claude Desktop",
         path=lambda: Path(os.environ.get("APPDATA", "")) / "Claude" / "claude_desktop_config.json",
         kind="json", key="mcpServers",
         note="改完要完全退出 Claude 再启动"),
    dict(id="cursor", name="Cursor",
         path=lambda: Path.home() / ".cursor" / "mcp.json",
         kind="json", key="mcpServers",
         note="全局配置；也可放项目 .cursor/mcp.json"),
    dict(id="workbuddy", name="WorkBuddy",
         path=lambda: Path.home() / ".workbuddy" / "mcp.json",
         kind="json", key="mcpServers",
         note="装完在连接器管理页点「信任」新服务器"),
    dict(id="cline", name="Cline（VS Code 扩展）",
         path=lambda: Path(os.environ.get("APPDATA", "")) / "Code" / "User" / "globalStorage"
                / "saoudrizwan.claude-dev" / "settings" / "cline_mcp_settings.json",
         kind="json", key="mcpServers", note=""),
    dict(id="vscode", name="VS Code / Copilot（工作区）",
         path=lambda: Path.cwd() / ".vscode" / "mcp.json",
         kind="json", key="servers",
         note="★ 这一家用 servers（不是 mcpServers）；装到当前目录的 .vscode"),
    dict(id="codex", name="Codex CLI",
         path=lambda: Path.home() / ".codex" / "config.toml",
         kind="toml", key="", note="OpenAI Codex CLI 用 TOML"),
    dict(id="continue", name="Continue",
         path=lambda: Path.home() / ".continue" / "config.yaml",
         kind="yaml", key="", note="Continue 用 YAML"),
]


def exe_rel() -> str:
    return str(EXE)


def snippet(client) -> str:
    """给 TOML / YAML 客户端追加的片段。"""
    cmd = exe_rel().replace("\\", "\\\\")
    if client["kind"] == "toml":
        return f"\n[mcp_servers.{NAME}]\ncommand = \"{cmd}\"\nargs = []\n"
    return f"\nmcpServers:\n  - name: {NAME}\n    command: {cmd}\n    args: []\n"


def entry() -> dict:
    return {"command": exe_rel(), "args": [], "env": {}}


def status(client):
    p = client["path"]()
    if p.exists():
        return "已存在配置（可合并）", p
    if p.parent.exists():
        return "客户端目录存在（会新建配置）", p
    return "未检测到", p


def do_list():
    print(f"MCP 服务器：{EXE}")
    print(("  ✅ 已发布" if EXE.exists() else "  ❌ 还没发布 —— 先双击 deploy.bat") + "\n")
    found = []
    for c in CLIENTS:
        st, p = status(c)
        if st == "未检测到":
            continue
        found.append(c["id"])
        print(f"[{c['id']}] {c['name']}")
        print(f"    状态：{st}")
        print(f"    配置：{p}")
        if c["note"]:
            print(f"    备注：{c['note']}")
        print()
    if not found:
        print("没有检测到任何已知的 agent 客户端。")
        print("也可以手动把 docs/mcp-clients/ 下对应的片段复制进客户端的配置。")
    return found


def backup(p: Path):
    if not p.exists():
        return None
    bak = p.with_suffix(p.suffix + f".bak-{time.strftime('%Y%m%d-%H%M%S')}")
    shutil.copy2(p, bak)
    return bak


def install_json(client) -> bool:
    p = client["path"]()
    if not p.parent.exists():
        print(f"  跳过 {client['id']}：目录不存在（{p.parent}）")
        return False
    data = {}
    if p.exists():
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except Exception as ex:
            print(f"  ❌ {client['id']}：现有配置不是合法 JSON（{ex}），已放弃，未改动")
            return False
    key = client["key"]
    table = data.get(key)
    if not isinstance(table, dict):
        table = {}
    existed = NAME in table
    table[NAME] = entry()
    data[key] = table

    bak = backup(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    verb = "更新" if existed else "新增"
    print(f"  ✅ {client['id']}：{verb} {NAME}" + (f"（备份 {bak.name}）" if bak else ""))
    return True


def install_text(client) -> bool:
    p = client["path"]()
    if not p.parent.exists():
        print(f"  跳过 {client['id']}：目录不存在（{p.parent}）")
        return False
    body = p.read_text(encoding="utf-8") if p.exists() else ""
    if NAME in body:
        print(f"  ⏭  {client['id']}：配置里已经有 {NAME}，未改动")
        return False
    bak = backup(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open("a", encoding="utf-8") as f:
        f.write(snippet(client))
    print(f"  ✅ {client['id']}：追加片段" + (f"（备份 {bak.name}）" if bak else ""))
    return True


def uninstall(client):
    p = client["path"]()
    if not p.exists():
        return
    if client["kind"] == "json":
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except Exception:
            print(f"  ❌ {client['id']}：配置不是合法 JSON，未改动")
            return
        table = data.get(client["key"])
        if isinstance(table, dict) and NAME in table:
            backup(p)
            del table[NAME]
            p.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
            print(f"  ✅ {client['id']}：已移除 {NAME}")
    else:
        print(f"  ⚠ {client['id']}：{client['kind'].upper()} 配置需手动删除 {NAME} 那一段（{p}）")


def main():
    ap = argparse.ArgumentParser(description="安装 TdxClaw MCP 服务器到各 agent 客户端")
    ap.add_argument("--install", nargs="*", metavar="CLIENT", help="安装到指定客户端；all = 全部检测到的")
    ap.add_argument("--uninstall", nargs="*", metavar="CLIENT", help="移除")
    args = ap.parse_args()

    if not EXE.exists():
        print("❌ 找不到 MCP 服务器 exe：", EXE)
        print("   先双击 deploy.bat 发布一次。")
        return 1

    if args.install is None and args.uninstall is None:
        do_list()
        print("安装：python install-mcp.py --install all")
        return 0

    targets = []
    if args.install is not None:
        want = args.install or []
        all_mode = "all" in want or len(want) == 0
        targets = [(c, "install") for c in CLIENTS
                   if all_mode or c["id"] in want]
    elif args.uninstall is not None:
        want = args.uninstall or []
        all_mode = "all" in want or len(want) == 0
        targets = [(c, "uninstall") for c in CLIENTS
                   if all_mode or c["id"] in want]

    for c, mode in targets:
        if mode == "install":
            install_json(c) if c["kind"] == "json" else install_text(c)
        else:
            uninstall(c)
    print("\n完成。客户端需要重启才会加载新的 MCP 服务器。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
