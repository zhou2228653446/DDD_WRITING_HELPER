@echo off
chcp 65001 >nul
setlocal
rem ============================================================
rem  TdxClaw writing service (web editor) startup manager.
rem  All real logic lives in web-service.py; this bat only finds
rem  a usable python interpreter and forwards the arguments.
rem
rem  IMPORTANT: keep this file pure ASCII and CRLF-terminated.
rem  cmd parses .bat byte by byte; a LF-only file containing
rem  multi-byte characters gets sliced into fragments that it then
rem  tries to RUN ("'restart' is not recognized as a command").
rem  web-service.py repairs the line endings automatically, but it
rem  cannot repair non-ASCII text -- edit comments in English.
rem
rem  Usage (double click = status):
rem    web-service.bat               status
rem    web-service.bat install       publish + autostart + firewall + start
rem    web-service.bat uninstall     remove autostart and stop
rem    web-service.bat restart       republish after code change, then restart
rem    web-service.bat start / stop
rem    web-service.bat log           tail today's log
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
