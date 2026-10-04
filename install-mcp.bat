@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  把 TdxClaw 写作助手的 MCP 服务器装进各个 agent 客户端
rem  实际逻辑在 install-mcp.py（JSON 合并 / 备份 / 检测都在这里），
rem  bat 只负责找到能用的 python。exe 路径由脚本自己推导，
rem  所以这里不传任何中文路径 —— 避开 Windows 控制台编码坑。
rem
rem  用法：
rem    双击                    列出本机检测到的客户端
rem    install-mcp.bat all     装到全部检测到的客户端
rem    install-mcp.bat cursor  只装 Cursor
rem ============================================================

set "PY="
where py >nul 2>&1 && set "PY=py -3"
if not defined PY if exist "%USERPROFILE%\.workbuddy\binaries\python\versions\3.13.12\python.exe" set "PY=%USERPROFILE%\.workbuddy\binaries\python\versions\3.13.12\python.exe"
if not defined PY if exist "E:\anaconda\python.exe" set "PY=E:\anaconda\python.exe"
if not defined PY where python >nul 2>&1 && set "PY=python"

if not defined PY (
    echo [错误] 没找到 Python。请先安装 Python 3，或手动复制 docs\mcp-clients\ 下的配置片段。
    pause
    exit /b 1
)

set "ARGS="
if "%~1"=="" goto :run
if /i "%~1"=="all" ( set "ARGS=--install all" & goto :run )
set "ARGS=--install %*"

:run
%PY% "%~dp0install-mcp.py" %ARGS%

echo.
pause
endlocal
