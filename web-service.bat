@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  TdxClaw 写作服务（编辑器.Web）的开机自启动管理
rem  实际逻辑在 web-service.py，这个 bat 只负责找到能用的 python。
rem
rem  用法（双击 = 看状态）：
rem    web-service.bat              显示当前状态
rem    web-service.bat install      发布 + 开机自启 + 防火墙 + 立刻启动
rem    web-service.bat uninstall    取消开机自启并停掉
rem    web-service.bat restart      改完代码后重新发布并重启
rem    web-service.bat start / stop
rem    web-service.bat log          看日志
rem ============================================================

set "PY="
where py >nul 2>&1 && set "PY=py -3"
if not defined PY if exist "%USERPROFILE%\.workbuddy\binaries\python\versions\3.13.12\python.exe" set "PY=%USERPROFILE%\.workbuddy\binaries\python\versions\3.13.12\python.exe"
if not defined PY if exist "E:\anaconda\python.exe" set "PY=E:\anaconda\python.exe"
if not defined PY where python >nul 2>&1 && set "PY=python"

if not defined PY (
    echo [ERROR] Python not found. Install Python 3 first.
    pause
    exit /b 1
)

%PY% "%~dp0web-service.py" %*

echo.
pause
endlocal
