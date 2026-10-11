#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TdxClaw 写作服务（编辑器.Web）的开机自启动管理。

为什么要有这个脚本：
  仓库里的编辑器.Web 是 ASP.NET Core 工程，平时靠 `dotnet run` 手动拉起来，
  重启电脑后就没了——外部设备（包括走 ddd-lighthouse.ddns.net 的 agent）
  全部断连。这个脚本把它发布成一个独立目录 + 一条登录触发的计划任务，
  开机进桌面后自动在后台跑起来，无需人工干预。

为什么是「计划任务」而不是「Windows 服务」：
  服务以 SYSTEM 身份运行时 %APPDATA% 指向 systemprofile，而账户系统
  (accounts.json)、API 配置 (api_profiles.json) 都放在当前用户的
  %APPDATA%\\TdxClaw 下——换了身份就读不到用户设的密码了。
  登录触发的任务跑在用户自己的会话里，配置路径与手动启动时完全一致。

为什么发布到 D:\\TdxClaw\\web 而不是仓库里：
  1. 全 ASCII 路径，避开 Windows 控制台 / 计划任务的中文编码坑；
  2. 与书库 D:\\TdxClaw\\self\\books 同盘同根，好找；
  3. 自启后不再占用源码目录，仓库随便动不影响运行中的服务。

用法（一般双击 web-service.bat 即可）：
  web-service.bat              显示当前状态
  web-service.bat install      发布 + 注册开机自启 + 放行防火墙 + 立刻启动
  web-service.bat uninstall    取消开机自启并停掉服务
  web-service.bat restart      重启服务（改完代码重新 publish 后用）
  web-service.bat start/stop   仅启动 / 仅停止
  web-service.bat log          打开今天的日志
"""

import argparse
import ctypes
import os
import re
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent          # 仓库根目录
INSTALL_DIR = Path(r"D:\TdxClaw\web")           # 发布目录（全 ASCII）
PORT = 5280
TASK_NAME = "TdxClawWeb"
APP_EXE = "TdxClaw.Web.exe"     # 发布目录里混着另外两个 exe，必须按名字精确定位
FW_RULE_NAME = "TdxClaw Web 5280"
DOMAIN = "ddd-lighthouse.ddns.net"
# 域名靠 DDNS 跟着家宽 IPv6 走。更新脚本在 bili 项目里（那边已经配好了
# ddd-lighthouse.ddns.net 的 No-IP DDNS Key），这里只是开机时顺手推一次：
# 重启后 IPv6 前缀常常已经变了，不更新的话域名会指向旧地址，外部连不上。
# 找不到脚本就跳过，不阻断服务启动。
DDNS_SCRIPT = Path(r"D:\repos\bili\ddns_update.py")

PROJ = ROOT / "编辑器.Web" / "编辑器.Web.csproj"
BAT_SELF = ROOT / "web-service.bat"       # 入口 bat，下面要负责给它修换行

OK = "[OK]"
ERR = "[!!]"
INFO = "[..]"


# ---------------------------------------------------------------- 基础工具

def ensure_bat_eol():
    """web-service.bat 必须是 CRLF，否则用户双击就报一堆「不是内部或外部命令」。

    cmd 是按字节逐个解析 .bat 的，只认 CRLF 作换行。文件一旦变成 LF-only，
    里面的中文多字节字符会被拦腰截断，切开的后半截被当成一条命令去执行，
    于是出现 `'restart' 不是内部或外部命令` 这种莫名其妙的报错。
    编辑器保存、跨工具复制都很容易把换行还原成 LF，所以每次运行顺手校一遍。

    只修换行，不碰内容（内容非 ASCII 得靠写的人自觉——bat 里注释走英文）。
    """
    try:
        raw = BAT_SELF.read_bytes()
    except OSError:
        return
    fixed = raw.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    if fixed == raw:
        return
    try:
        BAT_SELF.write_bytes(fixed)
        say(INFO, f"已把 {BAT_SELF.name} 的换行修回 CRLF（cmd 只认 CRLF）")
    except OSError:
        pass


def run(args, check=False, capture=True, timeout=60, dry=False):
    """跑一条命令。返回 (返回码, 输出文本)。dry=True 只打印不执行。"""
    if dry:
        print(f"       [dry-run] {' '.join(args)}")
        return 0, ""
    try:
        p = subprocess.run(
            args,
            capture_output=capture,
            timeout=timeout,
            text=True,
            encoding="utf-8",
            errors="replace",
            shell=False,
        )
        out = (p.stdout or "") + (p.stderr or "")
        if check and p.returncode != 0:
            raise RuntimeError(out.strip())
        return p.returncode, out.strip()
    except FileNotFoundError:
        return 127, f"找不到命令：{args[0]}"
    except subprocess.TimeoutExpired:
        return 124, f"超时：{' '.join(args)}"
    except OSError as e:
        # 连进程都起不来（权限不足、被安全软件拦掉）也算"这一步没成"，
        # 不能让异常把整个 status/restart 掀翻——查不到任务状态而已，服务还在跑
        return 126, f"无法执行：{e}"


def say(tag, msg):
    print(f"{tag} {msg}", flush=True)


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin():
    """注册计划任务 / 放行防火墙都要管理员令牌。非提升进程干不了，
    就弹一次 UAC 把自己重跑一遍（用户点是即可，不用手动开管理员终端）。"""
    if os.environ.get("TDXCLAW_ELEVATED") == "1":
        return False        # 已经重跑过了，别无限循环
    args = " ".join(sys.argv[1:])
    cmdline = (f'/c set TDXCLAW_ELEVATED=1&& "{sys.executable}" '
               f'"{Path(__file__).resolve()}" {args}')
    rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", "cmd.exe", cmdline, None, 1)
    return rc > 32          # <=32 表示用户取消了 UAC 或启动失败


def local_urls():
    """猜几个本机可访问的地址，打印给用户看。"""
    urls = [f"http://127.0.0.1:{PORT}/", f"http://{DOMAIN}:{PORT}/"]
    try:
        host = socket.gethostname()
        for info in socket.getaddrinfo(host, None):
            ip = info[4][0]
            if ip.startswith("127.") or ip == "::1":
                continue
            if ":" in ip:                       # IPv6，取全局单播地址
                if ip.lower().startswith(("2", "3")):
                    urls.append(f"http://[{ip}]:{PORT}/")
            elif ip.startswith(("192.168.", "10.", "172.")):
                urls.append(f"http://{ip}:{PORT}/")
    except Exception:
        pass
    # 去重、保序
    seen, out = set(), []
    for u in urls:
        if u not in seen:
            seen.add(u)
            out.append(u)
    return out


def alive(timeout=3):
    """服务是否在跑。本地回环探测，非回环会要求登录所以只探回环。
    ⚠ 必须绕过系统代理：代理会把 127.0.0.1 的请求也接走并回一个 502，
    那样探测结果全是错的（曾因此误判"服务已在运行"，服务再也没起来过）。"""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(f"http://127.0.0.1:{PORT}/", timeout=timeout) as r:
            return r.status < 500
    except Exception:
        return False


def my_ipv6():
    """本机对外 IPv6（全局单播地址，排除临时/链路本地）。"""
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET6):
            ip = info[4][0].split("%")[0]
            if ip.lower().startswith(("2", "3")):
                return ip
    except Exception:
        pass
    return ""


def domain_ipv6():
    """域名当前解析到的 IPv6。"""
    try:
        for info in socket.getaddrinfo(DOMAIN, None, socket.AF_INET6):
            return info[4][0].split("%")[0]
    except Exception:
        return ""


def update_ddns():
    """开机时把当前 IPv6 推给 No-IP。失败不影响服务本身。"""
    if not DDNS_SCRIPT.exists():
        say(INFO, f"没找到 DDNS 脚本（{DDNS_SCRIPT}），跳过域名更新。")
        return
    code, out = run([sys.executable, str(DDNS_SCRIPT), "--force"], timeout=90)
    if code == 0:
        say(OK, f"DDNS 已更新：{DOMAIN} -> {my_ipv6()}")
    else:
        say(INFO, f"DDNS 更新没成功（服务本身不受影响）：{out[:200]}")


def pid_on_port():
    """占用 5280 的进程 PID 列表。"""
    code, out = run(["netstat", "-ano", "-p", "TCP"])
    pids = set()
    if code == 0:
        for line in out.splitlines():
            if f":{PORT} " in line and "LISTENING" in line.upper():
                parts = line.split()
                if parts and parts[-1].isdigit():
                    pids.add(parts[-1])
    return pids


# ---------------------------------------------------------------- 发布

def publish():
    say(INFO, f"发布到 {INSTALL_DIR} ...")
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    code, out = run(
        ["dotnet", "publish", str(PROJ), "-c", "Release", "-o", str(INSTALL_DIR)],
        timeout=600,
    )
    if code != 0:
        say(ERR, "发布失败：")
        print(out)
        return False
    exe = find_exe()
    if not exe:
        say(ERR, "发布完成但目录里没找到可执行文件。")
        return False
    say(OK, f"发布完成：{exe.name}")
    return True


def find_exe():
    """发布目录里的主程序。目录里还有 编辑器.exe（桌面版）和 TdxClaw.Mcp.exe，
    不能拿 glob 的第一个凑合——起错程序就是打开一个 WPF 窗口。"""
    fixed = INSTALL_DIR / APP_EXE
    if fixed.exists():
        return fixed
    if not INSTALL_DIR.is_dir():
        return None
    for p in sorted(INSTALL_DIR.glob("*.exe")):
        if "Web" in p.name:
            return p
    return None


# ---------------------------------------------------------------- 启动器脚本

# ⚠ 启动器脚本刻意保持纯 ASCII、英文注释：cmd 与 wscript 都按系统 ANSI（中文机器
# 是 GBK）解析脚本文件，UTF-8 的中文会变成乱码，注释乱码还可能拼出意外字符。
START_BAT = r'''@echo off
rem TdxClaw writing service launcher -- generated by web-service.py, do not edit.
rem Keep this file pure ASCII: cmd parses .bat with the system ANSI codepage.
setlocal
set "DIR=%~dp0"
set "PORT=5280"
if not exist "%DIR%logs" md "%DIR%logs"

rem Log file name: caller passes YYYYMMDD (the vbs/python launchers already
rem computed it); parsing %DATE% here is only a fallback and varies by locale.
set "STAMP=%~1"
if not defined STAMP (
  for /f "tokens=1-3 delims=/-. " %%a in ("%DATE%") do set "STAMP=%%a%%b%%c"
)
if not defined STAMP set "STAMP=run"
set "LOG=%DIR%logs\web-%STAMP%.log"

rem Skip if an instance is already listening (same port would clash).
rem --noproxy is not optional: with a system proxy set, curl asks the proxy for
rem 127.0.0.1 and gets a 502 back -- which still counts as "curl succeeded",
rem so the launcher would skip starting and the service would never come up.
rem -f makes HTTP error codes count as failure too.
curl.exe -s -f --noproxy "*" -m 3 -o NUL http://127.0.0.1:%PORT%/ >NUL 2>&1
if not errorlevel 1 (
  >> "%LOG%" echo %DATE% %TIME% [TdxClaw] already listening on %PORT%, skip.
  exit /b 0
)

rem SYSTEM autostart (before logon): %APPDATA% points at systemprofile, which
rem would make the service read a different set of accounts and API keys.
rem Pin the config dir to the real user profile -- written by install --onstart.
set "TDXCLAW_CONFIG_DIR=__CONFIG_DIR__"

rem TDXCLAW_LAN=1 -> bind 0.0.0.0 and [::] so other devices can reach it.
set TDXCLAW_LAN=1
set ASPNETCORE_ENVIRONMENT=Production

rem The folder also holds the desktop app and TdxClaw.Mcp.exe -- name it exactly.
set "APP=%DIR%TdxClaw.Web.exe"
if not exist "%APP%" (
  echo [TdxClaw] %APP% not found, run publish first.
  exit /b 1
)

>> "%LOG%" echo %DATE% %TIME% [TdxClaw] starting %APP%
"%APP%" >> "%LOG%" 2>&1
'''

START_VBS = r'''Option Explicit
' TdxClaw writing service -- silent launcher, generated by web-service.py.
' Scheduled tasks running start.bat directly flash a console window;
' wrapping it in wscript keeps it fully hidden.
Dim ws, fso, ts, dir, cmd, stamp
Set ws = CreateObject("WScript.Shell")
dir = "__DIR__"
' Pass YYYYMMDD to start.bat so the daily log name does not depend on how
' this particular host resolves %DATE%; start.bat writes the log itself.
stamp = Year(Now) & Right("0" & Month(Now), 2) & Right("0" & Day(Now), 2)
' Leave a trace that autostart really fired. A broken vbs fails silently at
' logon, so without this line there is no way to tell "never triggered" from
' "started fine". Check the daily log after a reboot.
On Error Resume Next
Set fso = CreateObject("Scripting.FileSystemObject")
Set ts = fso.OpenTextFile(dir & "\logs\web-" & stamp & ".log", 8, True)
ts.WriteLine Now & " [TdxClaw] autostart triggered"
ts.Close
On Error GoTo 0
cmd = "cmd /c """ & dir & "\start.bat"" " & stamp
ws.CurrentDirectory = dir
ws.Run cmd, 0, False
'''


def user_config_dir():
    """真实用户的配置目录（%APPDATA%\\TdxClaw）。开机自启跑在 SYSTEM 下时
    要把这个路径显式传给服务，否则它读的是 systemprofile 下那份空配置。"""
    base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
    return str(Path(base) / "TdxClaw")


def write_launchers(config_dir=None):
    """不传参时也把配置目录钉死成真实用户目录：用户会话下它与 %APPDATA% 一致，
    而 SYSTEM 自启时这是唯一能读到同一份账户/API 配置的办法。
    ⚠ 别让 restart 之类后续操作把它写没了——那会让 SYSTEM 那条路静默读空配置。"""
    if config_dir is None:
        config_dir = user_config_dir()
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    bat = START_BAT
    if config_dir:
        bat = bat.replace("__CONFIG_DIR__", config_dir)
    else:
        # 普通（用户会话）路线不需要钉死目录，去掉这两行而不是留个空变量
        bat = "\n".join(l for l in bat.splitlines() if "__CONFIG_DIR__" not in l)
    (INSTALL_DIR / "start.bat").write_text(bat, encoding="ascii")
    (INSTALL_DIR / "start-hidden.vbs").write_text(
        START_VBS.replace("__DIR__", str(INSTALL_DIR)), encoding="ascii"
    )
    (INSTALL_DIR / "logs").mkdir(exist_ok=True)
    say(OK, f"启动器已写入 {INSTALL_DIR}")


# ---------------------------------------------------------------- 计划任务

def task_exists():
    code, out = run(["schtasks", "/query", "/tn", TASK_NAME])
    return code == 0


def task_info(name=None, dry=False):
    """返回 (是否存在, 状态, 上次运行时间)"""
    code, out = run(["schtasks", "/query", "/tn", name or TASK_NAME, "/fo", "LIST", "/v"],
                    dry=dry)
    if code != 0:
        return False, "", ""
    status = re.search(r"^\s*(?:Status|状态)\s*[:：]\s*(.+)$", out, re.M)
    last = re.search(r"^\s*(?:Last Run Time|上次运行时间)\s*[:：]\s*(.+)$", out, re.M)
    return True, (status.group(1).strip() if status else ""), (last.group(1).strip() if last else "")


def install_task():
    vbs = INSTALL_DIR / "start-hidden.vbs"
    if not vbs.exists():
        say(ERR, f"缺少 {vbs}，先写启动器。")
        return False
    run(["schtasks", "/delete", "/tn", TASK_NAME, "/f"])   # 先清旧的，幂等
    tr = f'wscript.exe //B //NOLOGO "{vbs}"'
    code, out = run(
        ["schtasks", "/create", "/tn", TASK_NAME, "/tr", tr,
         "/sc", "ONLOGON", "/delay", "0000:45", "/f"]
    )
    if code != 0:
        say(ERR, "注册计划任务失败：")
        print(out)
        return False
    say(OK, f"计划任务 {TASK_NAME} 已注册（登录后延迟 45 秒启动）")
    return True


STARTUP_VBS_NAME = "TdxClawWeb.vbs"
DDNS_TASK = "TdxClawDDNS"


def startup_folder():
    """当前用户的启动文件夹。往里放东西不需要管理员权限——这是计划任务
    注册不了（没 UAC / 用户取消）时的兜底方案。"""
    base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
    return Path(base) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup"


def install_startup_folder():
    sf = startup_folder()
    try:
        sf.mkdir(parents=True, exist_ok=True)
        vbs = START_VBS.replace("__DIR__", str(INSTALL_DIR))
        (sf / STARTUP_VBS_NAME).write_text(vbs, encoding="ascii")
    except Exception as e:
        say(ERR, f"写入启动文件夹失败：{e}")
        return False
    say(OK, f"已放进启动文件夹：{sf / STARTUP_VBS_NAME}")
    say(INFO, "登录进桌面后自动启动（不需要管理员权限）。")
    return True


def uninstall_startup_folder():
    f = startup_folder() / STARTUP_VBS_NAME
    if f.exists():
        try:
            f.unlink()
            say(OK, f"已删除 {f}")
        except Exception as e:
            say(ERR, f"删除失败：{e}")
            return False
    return True


def install_task_onstart(dry=False):
    """开机即跑（登录之前）：SYSTEM 账户、最高权限。跟 bili 的 BiliMonitor 一个路子，
    好处是不用写账户密码、开机就有；代价是 %APPDATA% 变成 systemprofile，
    所以启动脚本里必须钉死 TDXCLAW_CONFIG_DIR（见 user_config_dir）。"""
    vbs = INSTALL_DIR / "start-hidden.vbs"
    if not vbs.exists():
        say(ERR, f"缺少 {vbs}，先写启动器。")
        return False
    # 先清同名旧任务，保证幂等（ONLOGON 装过再装 ONSTART 也不会留两个）
    run(["schtasks", "/delete", "/tn", TASK_NAME, "/f"], dry=dry)
    tr = f'wscript.exe //B //NOLOGO "{vbs}"'
    code, out = run(
        ["schtasks", "/create", "/tn", TASK_NAME, "/tr", tr,
         "/sc", "onstart", "/ru", "SYSTEM", "/rl", "HIGHEST", "/f"],
        dry=dry,
    )
    if code != 0:
        say(ERR, "注册开机任务失败：")
        print(out)
        return False
    say(OK, f"计划任务 {TASK_NAME} 已注册（开机即启动，SYSTEM，无需登录）")
    return True


def install_ddns_task(dry=False):
    """家宽 IPv6 前缀随时可能变，域名不跟着更新外部就断连。每 30 分钟查一次。"""
    if not DDNS_SCRIPT.exists():
        say(INFO, f"没找到 DDNS 脚本（{DDNS_SCRIPT}），跳过定时任务。")
        return False
    tr = f'"{sys.executable}" "{DDNS_SCRIPT}"'
    code, out = run(
        ["schtasks", "/create", "/tn", DDNS_TASK, "/tr", tr,
         "/sc", "minute", "/mo", "30", "/ru", "SYSTEM", "/rl", "HIGHEST", "/f"],
        dry=dry,
    )
    if code != 0:
        say(INFO, f"DDNS 定时任务没建成：{out[:200]}")
        return False
    say(OK, f"计划任务 {DDNS_TASK} 已注册（每 30 分钟检查 IPv6 是否变化）")
    return True


def run_task(name, dry=False):
    code, out = run(["schtasks", "/run", "/tn", name], dry=dry)
    if code == 0:
        say(OK, f"已触发任务 {name}")
    else:
        say(INFO, f"触发 {name} 失败：{out[:200]}")
    return code == 0


def uninstall_ddns_task():
    code, _ = run(["schtasks", "/delete", "/tn", DDNS_TASK, "/f"])
    if code == 0:
        say(OK, f"计划任务 {DDNS_TASK} 已删除")
    return code == 0


def uninstall_task():
    code, out = run(["schtasks", "/delete", "/tn", TASK_NAME, "/f"])
    if code == 0:
        say(OK, f"计划任务 {TASK_NAME} 已删除")
    else:
        say(INFO, f"没有名为 {TASK_NAME} 的计划任务（或删除失败）：{out}")
    return code == 0


# ---------------------------------------------------------------- 防火墙

def firewall_open(quiet=False, dry=False):
    """放行 5280 入站。需要管理员；没权限就给出命令让用户自己跑。"""
    code, out = run(["netsh", "advfirewall", "firewall", "show", "rule",
                     f"name={FW_RULE_NAME}"], dry=dry)
    if code == 0 and FW_RULE_NAME in out:
        say(OK, f"防火墙规则已存在：{FW_RULE_NAME}")
        return True

    code, out = run(["netsh", "advfirewall", "firewall", "add", "rule",
                     f"name={FW_RULE_NAME}", "dir=in", "action=allow",
                     "protocol=TCP", f"localport={PORT}"], dry=dry)
    if code == 0:
        say(OK, f"防火墙已放行 TCP {PORT}")
        return True

    if not quiet:
        say(INFO, "放行防火墙需要管理员权限，请用管理员身份跑一次：")
        print(f'       netsh advfirewall firewall add rule name="{FW_RULE_NAME}" '
              f"dir=in action=allow protocol=TCP localport={PORT}")
        print("       （之前如果已经放行过 5280，这一步可以跳过）")
    return False


# ---------------------------------------------------------------- 起停

def start_service(wait=12):
    bat = INSTALL_DIR / "start.bat"
    if not bat.exists():
        say(ERR, "启动器不存在，先 install。")
        return False
    # 走同一份 start.bat（而不是另起一套启动逻辑），手动启动和计划任务行为一致；
    # 环境变量、端口检查、日志落地都在 bat 里，改一处两边同时生效。
    stamp = time.strftime("%Y%m%d")
    subprocess.Popen(
        ["cmd", "/c", str(bat), stamp],
        cwd=str(INSTALL_DIR),
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    for _ in range(wait):
        time.sleep(1)
        if alive(timeout=2):
            say(OK, f"服务已启动，端口 {PORT}")
            return True
    say(INFO, "还没探测到服务响应，可能还在启动，看日志：")
    print(f"       {INSTALL_DIR / 'logs'}")
    return False


def stop_service():
    pids = pid_on_port()
    if not pids:
        say(INFO, f"端口 {PORT} 上没有监听，无需停止。")
        return True
    for pid in pids:
        code, out = run(["taskkill", "/PID", pid, "/F"])
        say(OK if code == 0 else ERR, f"结束进程 {pid}：{out or 'done'}")
    time.sleep(1)
    return not pid_on_port()


# ---------------------------------------------------------------- 命令

def cmd_status(_a):
    print()
    print("==== TdxClaw 写作服务状态 ====")
    exists, status, last = task_info()
    if exists:
        say(OK, f"开机自启：已注册（{TASK_NAME}）")
        if status:
            print(f"       状态：{status}")
        if last:
            print(f"       上次运行：{last}")
    else:
        say(INFO, "开机自启（计划任务）：未注册（跑 install 开启）")

    ddns_ok, _, ddns_last = task_info(DDNS_TASK)
    if ddns_ok:
        say(OK, f"DDNS 定时检查：已注册（{DDNS_TASK}"
                + (f"，上次 {ddns_last}）" if ddns_last else "）"))
    else:
        say(INFO, f"DDNS 定时检查：未注册（install --onstart 会一并建）")

    sf = startup_folder() / STARTUP_VBS_NAME
    if sf.exists():
        say(OK, f"开机自启（启动文件夹）：已放 {sf}")
    else:
        say(INFO, "开机自启（启动文件夹）：未启用")

    exe = find_exe()
    print(f"       发布目录：{INSTALL_DIR}"
          + (f"（{exe.name}）" if exe else "（未发布）"))

    if alive():
        say(OK, f"服务：运行中 http://127.0.0.1:{PORT}/")
    else:
        say(INFO, "服务：未运行")

    # 域名解析与本机地址是否一致——不一致说明 DDNS 没跟上，外部会连到旧地址
    mine, theirs = my_ipv6(), domain_ipv6()
    if theirs:
        if mine and theirs.lower() == mine.lower():
            say(OK, f"域名 {DOMAIN} -> {theirs}（与本机一致）")
        else:
            say(INFO, f"域名 {DOMAIN} -> {theirs}，本机 {mine or '未知'}（不一致，"
                      "跑一次 install 或手动更新 DDNS）")
    elif mine:
        say(INFO, f"域名 {DOMAIN} 暂时解析不到（本机 {mine}）")

    print("       访问地址：")
    for u in local_urls():
        print(f"         {u}")

    logs = sorted((INSTALL_DIR / "logs").glob("web-*.log"), reverse=True) if (INSTALL_DIR / "logs").is_dir() else []
    if logs:
        print(f"       最新日志：{logs[0]}")
    print()


def stop_before_publish():
    """发布会覆盖 dll，而运行中的进程一直锁着这些文件——不先停掉，
    publish 必然卡在「文件被另一个进程使用」上重试十次然后失败。"""
    if alive():
        say(INFO, "先停掉正在运行的服务：发布要覆盖 dll，运行中的进程锁着它们。")
        stop_service()


def cmd_install(a):
    print()
    if not a.no_publish:
        stop_before_publish()
        if not publish():
            return 1
    exe = find_exe()
    if not exe:
        say(ERR, "发布目录里没有可执行文件，install 中止。")
        return 1

    if a.onstart:
        # 开机即跑（登录之前）：SYSTEM 账户，必须要管理员令牌
        if not is_admin():
            if a.dry_run:
                say(INFO, "（dry-run）真实执行要管理员权限，这里只打印命令。")
            elif relaunch_as_admin():
                say(INFO, "已弹窗请求管理员权限，请在打开的窗口里确认。")
                return 2
            else:
                say(INFO, "注册开机任务需要管理员权限。请用管理员终端跑：")
                print("       web-service.bat install --onstart")
                return 1
        cfg = user_config_dir()
        say(INFO, f"SYSTEM 下 %APPDATA% 不是你的目录，配置目录钉死为 {cfg}")
        write_launchers(config_dir=cfg)
        if not install_task_onstart(a.dry_run):
            return 1
        install_ddns_task(a.dry_run)
        firewall_open(dry=a.dry_run)
        if not a.dry_run:
            run_task(TASK_NAME)     # 立刻按任务的方式跑一次，确认 SYSTEM 下也起得来
            cmd_status(a)
        return 0

    write_launchers()

    if a.startup_folder:
        # 免管理员路线：只往当前用户的启动文件夹放一个静默启动脚本
        if not install_startup_folder():
            return 1
    elif not install_task():
        # 没权限就弹 UAC 重跑一次，用户在弹窗里点“是”即可完成剩下的步骤
        if not is_admin() and relaunch_as_admin():
            say(INFO, "已弹窗请求管理员权限，请在打开的窗口里确认。")
            return 2
        say(INFO, "注册计划任务需要管理员权限，改用免管理员的启动文件夹方案。")
        if not install_startup_folder():
            say(INFO, "也可以自己用管理员终端跑：web-service.bat install")
            return 1
    firewall_open()
    if alive():                 # 已经有一个实例在跑（多半是手动 dotnet run 的）
        say(INFO, "端口上已有旧实例，先停掉再起发布版。")
        stop_service()
    start_service()
    update_ddns()               # 重启后 IPv6 多半变了，顺手把域名指过来
    cmd_status(a)
    return 0


def cmd_uninstall(_a):
    print()
    uninstall_startup_folder()
    uninstall_ddns_task()
    if task_exists() and not uninstall_task():
        if not is_admin() and relaunch_as_admin():
            say(INFO, "已弹窗请求管理员权限，请在打开的窗口里确认。")
            return 2
        say(INFO, "删除计划任务需要管理员权限。请用管理员身份打开终端后跑：")
        print("       web-service.bat uninstall")
        return 1
    stop_service()
    print()
    return 0


def cmd_restart(a):
    print()
    stop_service()
    if not a.no_publish:
        if not publish():
            return 1
    start_service()
    cmd_status(a)
    return 0


def cmd_start(_a):
    print()
    start_service()
    print()
    return 0


def cmd_stop(_a):
    print()
    stop_service()
    print()
    return 0


def cmd_log(_a):
    logs_dir = INSTALL_DIR / "logs"
    if not logs_dir.is_dir():
        say(ERR, "还没有日志目录。")
        return 1
    logs = sorted(logs_dir.glob("web-*.log"), reverse=True)
    if not logs:
        say(ERR, "还没有日志文件。")
        return 1
    say(INFO, f"{logs[0]} 最后 40 行：")
    print()
    print("\n".join(logs[0].read_text(encoding="utf-8", errors="replace").splitlines()[-40:]))
    return 0


def main():
    ensure_bat_eol()
    ap = argparse.ArgumentParser(description="TdxClaw 写作服务开机自启动管理")
    ap.add_argument("command", nargs="?", default="status",
                    choices=["install", "uninstall", "restart", "start", "stop", "status", "log", "publish"])
    ap.add_argument("--no-publish", action="store_true",
                    help="install/restart 时跳过重新发布（只重启现有产物）")
    ap.add_argument("--startup-folder", action="store_true",
                    help="install 时只写入用户启动文件夹（免管理员，不注册计划任务）")
    ap.add_argument("--onstart", action="store_true",
                    help="install 时注册开机即跑的 SYSTEM 任务（需管理员，登录之前就启动）")
    ap.add_argument("--dry-run", action="store_true",
                    help="只打印将执行的系统命令，不真的执行")
    args = ap.parse_args()

    table = {
        "install": cmd_install, "uninstall": cmd_uninstall, "restart": cmd_restart,
        "start": cmd_start, "stop": cmd_stop, "status": cmd_status, "log": cmd_log,
    }
    if args.command == "publish":
        return 0 if publish() else 1
    return table[args.command](args) or 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
