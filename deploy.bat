@echo off
rem ============================================================
rem  一键发布：把「AI 写作助手」打成一个独立 exe
rem
rem  用法：双击运行，或命令行 执行 deploy.bat
rem  产物：dist\AI写作助手.exe（自含 .NET 运行时，目标机器无需安装 .NET）
rem
rem  说明：
rem   - EnableSingleFilePublish 开关只在 publish 时生效，不影响日常 build
rem   - 单文件约 150~200MB（WPF + 运行时 + 原生库全打包），首次启动稍慢属正常
rem   - QuestPDF LicenseType.Community 已在代码里设置，发布无需额外配置
rem ============================================================

setlocal
cd /d "%~dp0"

echo.
echo [1/2] 正在发布单文件 exe（win-x64，自含运行时）...
dotnet publish 编辑器\编辑器.csproj -c Release -p:EnableSingleFilePublish=true -o dist
if errorlevel 1 (
    echo.
    echo 发布失败！请把上面的错误信息发给 AI 分析。
    pause
    exit /b 1
)

echo.
echo [2/3] 清理多余文件（保留单个 exe）...
rem PublishSingleFile 已把托管与原生依赖全部打进 exe，dist 里剩的是
rem pdb / 开发用文件；DebugType=none 时 pdb 都不会生成，这里兜底删一遍。
if exist dist\*.pdb del /q dist\*.pdb

echo.
echo [3/3] 发布 MCP 服务器（给 AI 客户端接入用）...
rem MCP 走 stdio，会被客户端反复启动，所以**不打单文件**——单文件每次启动都要
rem 解包原生库，冷启动会明显变慢。这里是依赖本机的 .NET 10 运行时，启动即来。
dotnet publish 编辑器.Mcp\编辑器.Mcp.csproj -c Release -o dist\mcp
if errorlevel 1 (
    echo.
    echo MCP 服务器发布失败！主程序不受影响，可继续使用。
)

echo.
echo ============================================
echo  发布完成：
echo    dist\AI写作助手.exe     主程序（单文件，拷走即用）
echo    dist\mcp\TdxClaw.Mcp.exe MCP 服务器（接入 AI 客户端用）
echo       配置示例见 docs\mcp-config-example.json
echo  首次启动需解包原生库，会慢几秒，属正常现象。
echo ============================================
echo.
explorer "%~dp0dist"
exit /b 0
