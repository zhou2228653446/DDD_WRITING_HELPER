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
- Codex 的 TOML 也做**结构化合并**：按 [table] 段落切分文本，只替换
  [mcp_servers.tdxclaw-writing] 这一段（含 .env 等子表），其余内容连同注释
  逐字节保留。卸载时真能删掉这一段（不依赖 codex CLI 是否安装）。
- YAML（Continue）没有可靠的 stdlib 写入器，仍是**追加片段**——装好就够用，
  卸载需要你手动删那一段。
- 写前一定备份成 <原文件>.bak-<时间戳>。
- exe 路径由本脚本位置推导（仓库根/dist/mcp/TdxClaw.Mcp.exe），
  不在命令行里传中文路径，绕开 Windows 控制台编码坑。
"""

import argparse
import json
import os
import re
import shutil
import sys
import time
from pathlib import Path

# Windows PowerShell 默认控制台编码常为 GBK (cp936)，直接 print emoji（✅/❌/⏭）会抛 UnicodeEncodeError 中断循环
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

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
         kind="toml", key="", cli="codex",
         note="OpenAI Codex CLI 用 ~/.codex/config.toml（TOML）。★ 写的是结构化合并，"
              "不会动你已有的其它 [mcp_servers.*] 段；装完用 `codex mcp list` 验证"),
    dict(id="continue", name="Continue",
         path=lambda: Path.home() / ".continue" / "config.yaml",
         kind="yaml", key="", note="Continue 用 YAML"),
]


def exe_rel() -> str:
    return str(EXE)


def toml_block() -> str:
    """Codex 用的 [mcp_servers.<NAME>] 段。

    两个刻意的选择：
    · 备份/路径里出现反斜杠必须转义成 \\\\ —— TOML 基本字符串里 `\\` 是转义符，
      直接写 D:\\编辑器\\... 会被解析成 D:<退格>辑器...
    · startup_timeout_sec = 30 —— Codex 默认只给 MCP 服务器 10 秒完成握手。
      本服务器是自包含 .NET exe，首次冷启动（杀软扫盘、JIT）经常超过 10 秒，
      不抬高的话用户只会看到「handshaking with MCP server failed」。
    """
    cmd = exe_rel().replace("\\", "\\\\")
    return (
        f"[mcp_servers.{NAME}]\n"
        f"command = \"{cmd}\"\n"
        f"args = []\n"
        f"# 自包含 exe 冷启动常超过 Codex 默认的 10 秒，抬高到 30 秒避免握手超时\n"
        f"startup_timeout_sec = 30\n"
    )


def yaml_snippet() -> str:
    """给 YAML 客户端（Continue）追加的片段。

    ⚠ 这里**不能**把反斜杠转义成 \\\\ —— YAML 的普通标量（不加引号）不做转义，
    写 D:\\\\编辑器\\\\... 会被当成真的双反斜杠路径，服务器就找不到了。
    （JSON 那边必须转义，是 json.dumps 自动处理的，两者规则不同。）
    """
    return f"\nmcpServers:\n  - name: {NAME}\n    command: {exe_rel()}\n    args: []\n"


def entry() -> dict:
    return {"command": exe_rel(), "args": [], "env": {}}


# ── TOML 结构化读写 ────────────────────────────────────────────────
# tomllib 只能读不能写，而为了「只动我们这一段、其余字节原样保留」，
# 也不该整份重写（会丢掉用户的注释和排版）。所以按段落做文本级手术。

_TOML_HEADER = re.compile(r"^\s*\[\[?([^\]\[]+)\]\]?\s*(?:#.*)?$")


def _toml_table(header: str) -> str:
    """按 TOML 段落规则切分文本，返回 [(段头, 该段全部行)]。

    段头为 None 表示文件开头、还没有任何 [table] 之前的裸键区。
    """
    blocks: list[tuple[str | None, list[str]]] = []
    cur_header: str | None = None
    cur: list[str] = []
    for line in header.splitlines(keepends=True):
        m = _TOML_HEADER.match(line)
        if m:
            blocks.append((cur_header, cur))
            cur_header, cur = m.group(1).strip(), [line]
        else:
            cur.append(line)
    blocks.append((cur_header, cur))
    return blocks


def _is_our_table(name: str | None) -> bool:
    if not name:
        return False
    # 精确命中 mcp_servers.tdxclaw-writing，以及它的子表 .env / .env_vars
    return name == f"mcp_servers.{NAME}" or name.startswith(f"mcp_servers.{NAME}.")


def toml_get(text: str) -> str | None:
    """取出我们那一段的文本（含子表）；没有则 None。"""
    hit = [lines for h, lines in _toml_table(text) if _is_our_table(h)]
    return "".join("".join(lines) for lines in hit) or None


def toml_set(text: str, block: str) -> str:
    """写入/替换我们那一段，其余内容原样保留。"""
    if toml_get(text) is None:
        sep = "" if (not text or text.endswith("\n")) else "\n"
        if text and not text.endswith("\n\n"):
            sep += "\n"
        return f"{text}{sep}{block}"
    out: list[str] = []
    for h, lines in _toml_table(text):
        if not _is_our_table(h):
            out.extend(lines)
    body = "".join(out).rstrip("\n")
    return f"{body}\n\n{block}" if body else block


def toml_remove(text: str) -> tuple[str, bool]:
    """删掉我们那一段（含子表）。返回 (新文本, 是否真的删了)。"""
    if toml_get(text) is None:
        return text, False
    out: list[str] = []
    for h, lines in _toml_table(text):
        if not _is_our_table(h):
            out.extend(lines)
    return "".join(out).rstrip("\n") + ("\n" if out else ""), True


def _cli_installed(client) -> str | None:
    """客户端自带命令行工具时，能装 CLI 就说明客户端装了（哪怕配置目录还没建）。"""
    exe = client.get("cli")
    return shutil.which(exe) if exe else None


def status(client):
    p = client["path"]()
    if p.exists():
        return "已存在配置（可合并）", p
    if _cli_installed(client):
        return "检测到命令行工具（会新建配置）", p
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
    missing = [c for c in CLIENTS if c["id"] not in found]
    if missing:
        print("没检测到（装了对应客户端后重跑，或手动照 docs/mcp-clients/ 的片段填）：")
        for c in missing:
            print(f"  · {c['name']} → {c['path']()}")
        print()
    print("安装：python install-mcp.py --install all")
    print("只装 Codex：python install-mcp.py --install codex")
    return found


def snippet_for(client) -> str:
    """该客户端要写进去的配置片段（纯文本，供界面展示/复制）。"""
    if client["kind"] == "json":
        return json.dumps({client["key"]: {NAME: entry()}},
                          ensure_ascii=False, indent=2)
    if client["kind"] == "toml":
        return toml_block()
    return yaml_snippet().lstrip("\n")


def emit_json():
    """给界面/脚本用的机器可读输出（网页版「接入 Agent」对话框就调这个）。

    客户端清单只在这里维护一份 —— 网页端不再抄一遍，避免两边漂移。
    """
    clients = []
    for c in CLIENTS:
        st, p = status(c)
        clients.append({
            "id": c["id"],
            "name": c["name"],
            "kind": c["kind"],
            "configPath": str(p),
            "detected": st != "未检测到",
            "status": st,
            "note": c["note"],
            "snippet": snippet_for(c),
            "installCommand": f"python install-mcp.py --install {c['id']}",
        })
    print(json.dumps({
        "serverName": NAME,
        "exe": str(EXE),
        "exeExists": EXE.exists(),
        "clients": clients,
    }, ensure_ascii=False, indent=2))
    return 0


def backup(p: Path):
    if not p.exists():
        return None
    bak = p.with_suffix(p.suffix + f".bak-{time.strftime('%Y%m%d-%H%M%S')}")
    shutil.copy2(p, bak)
    return bak


def probe_server(timeout: int = 25):
    """拉起服务器跑一遍 MCP 握手（initialize + tools/list），确认它真能被客户端启动。

    这一步值得做：exe 路径写对了不代表客户端能连上——缺 .NET 运行时、
    被杀软拦住、或握手超时（Codex 默认只给 10 秒）都会表现为客户端里
    「服务器没出现」，而客户端通常把 stderr 吞掉，用户完全看不到原因。
    返回 (是否成功, 说明文字)。
    """
    import subprocess
    payload = (
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":'
        '{"protocolVersion":"2024-11-05","capabilities":{},'
        '"clientInfo":{"name":"install-mcp","version":"1"}}}\n'
        '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}\n'
    )
    try:
        proc = subprocess.run(
            [str(EXE)], input=payload, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return False, f"{timeout} 秒内没完成握手（可加大客户端里的启动超时）"
    except Exception as ex:
        return False, f"启动失败：{ex}"

    tools = 0
    for line in (proc.stdout or "").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            obj = json.loads(line)
        except Exception:
            continue
        result = obj.get("result") or {}
        if isinstance(result.get("tools"), list):
            tools = len(result["tools"])
    if tools:
        return True, f"握手成功，暴露 {tools} 个工具"
    err = (proc.stderr or "").strip().splitlines()
    return False, "握手没有返回工具列表" + (f"；stderr: {err[-1]}" if err else "")


def probe_and_report() -> bool:
    print("── 验证服务器能否被客户端启动 ──")
    ok, msg = probe_server()
    print(("  ✅ " if ok else "  ❌ ") + msg)
    if not ok:
        print("     排障：把 TDX_MCP_LOG 环境变量指到一个可写文件，客户端里的报错会落盘。")
    return ok


def install_json(client, force: bool = False) -> bool:
    p = client["path"]()
    if not _ensure_parent(client, p, force):
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


def _ensure_parent(client, p: Path, force: bool = False) -> bool:
    if p.parent.exists():
        return True
    # force：用户用 --install <id> 明确点名了这一家。这时哪怕客户端还没装
    # （比如 Codex 只装了 CLI、或压根还没装），也把配置目录建出来，
    # 免得用户发现「我明明指名要装 codex，它却说未检测到就跳过了」。
    # --install all 不带 force：那是"能给谁装就给谁装"，不该凭空造目录。
    if force or _cli_installed(client):
        p.parent.mkdir(parents=True, exist_ok=True)
        return True
    print(f"  跳过 {client['id']}：目录不存在（{p.parent}）——"
          f"想强制装请写 --install {client['id']}")
    return False


def install_toml(client, force: bool = False) -> bool:
    """TOML 客户端（Codex）：结构化合并，只动 [mcp_servers.tdxclaw-writing] 这一段。"""
    p = client["path"]()
    if not _ensure_parent(client, p, force):
        return False
    try:
        body = p.read_text(encoding="utf-8") if p.exists() else ""
    except UnicodeDecodeError as ex:
        print(f"  ❌ {client['id']}：现有配置不是 UTF-8 文本（{ex}），已放弃，未改动")
        return False

    block = toml_block()
    if toml_get(body) == block:
        print(f"  ⏭  {client['id']}：已经是最新内容，未改动")
        return False
    existed = toml_get(body) is not None

    bak = backup(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(toml_set(body, block), encoding="utf-8")
    verb = "更新" if existed else "新增"
    print(f"  ✅ {client['id']}：{verb} [mcp_servers.{NAME}]"
          + (f"（备份 {bak.name}）" if bak else ""))
    print(f"     验证：codex mcp list")
    return True


def install_yaml(client, force: bool = False) -> bool:
    """YAML 客户端（Continue）：没有可靠的 stdlib YAML 写入器，仍是片段追加。"""
    p = client["path"]()
    if not _ensure_parent(client, p, force):
        return False
    body = p.read_text(encoding="utf-8") if p.exists() else ""
    if NAME in body:
        print(f"  ⏭  {client['id']}：配置里已经有 {NAME}，未改动")
        return False
    bak = backup(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open("a", encoding="utf-8") as f:
        f.write(yaml_snippet())
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
    elif client["kind"] == "toml":
        body = p.read_text(encoding="utf-8")
        new, removed = toml_remove(body)
        if not removed:
            print(f"  ⏭  {client['id']}：配置里没有 {NAME}，未改动")
            return
        backup(p)
        p.write_text(new, encoding="utf-8")
        print(f"  ✅ {client['id']}：已移除 [mcp_servers.{NAME}]")
    else:
        print(f"  ⚠ {client['id']}：{client['kind'].upper()} 配置需手动删除 {NAME} 那一段（{p}）")


def main():
    ap = argparse.ArgumentParser(description="安装 TdxClaw MCP 服务器到各 agent 客户端")
    ap.add_argument("--install", nargs="*", metavar="CLIENT", help="安装到指定客户端；all = 全部检测到的")
    ap.add_argument("--uninstall", nargs="*", metavar="CLIENT", help="移除")
    ap.add_argument("--verify", action="store_true",
                    help="只做一次服务器握手自检（initialize + tools/list），不改任何配置")
    ap.add_argument("--json", action="store_true",
                    help="以 JSON 输出客户端清单/检测状态/配置片段（供界面或脚本消费），不改任何配置")
    args = ap.parse_args()

    if args.json:
        return emit_json()

    if not EXE.exists():
        print("❌ 找不到 MCP 服务器 exe：", EXE)
        print("   先双击 deploy.bat 发布一次。")
        return 1

    if args.verify:
        return 0 if probe_and_report() else 1

    if args.install is None and args.uninstall is None:
        do_list()
        return 0

    targets = []
    if args.install is not None:
        want = args.install or []
        all_mode = "all" in want or len(want) == 0
        targets = [(c, "install", not all_mode) for c in CLIENTS
                   if all_mode or c["id"] in want]
    elif args.uninstall is not None:
        want = args.uninstall or []
        all_mode = "all" in want or len(want) == 0
        targets = [(c, "uninstall", False) for c in CLIENTS
                   if all_mode or c["id"] in want]

    known = {c["id"] for c in CLIENTS}
    unknown = [w for w in (args.install or args.uninstall or [])
               if w not in known and w != "all"]
    if unknown:
        print(f"⚠ 不认识这些客户端名：{' '.join(unknown)}")
        print(f"   可用：{' '.join(sorted(known))} all\n")

    if not targets:
        print("没有匹配到任何客户端。")
        return 1

    for c, mode, force in targets:
        if mode == "install":
            if c["kind"] == "json":
                install_json(c, force)
            elif c["kind"] == "toml":
                install_toml(c, force)
            else:
                install_yaml(c, force)
        else:
            uninstall(c)
    if any(mode == "install" for _, mode, _ in targets):
        print()
        probe_and_report()
    print("\n完成。客户端需要重启才会加载新的 MCP 服务器。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
