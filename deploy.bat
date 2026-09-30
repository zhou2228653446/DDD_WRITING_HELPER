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
echo [2/2] 清理多余文件（保留单个 exe）...
rem PublishSingleFile 已把托管与原生依赖全部打进 exe，dist 里剩的是
rem pdb / 开发用文件；DebugType=none 时 pdb 都不会生成，这里兜底删一遍。
if exist dist\*.pdb del /q dist\*.pdb

echo.
echo ============================================
echo  发布完成：dist\AI写作助手.exe
echo  首次启动需解包原生库，会慢几秒，属正常现象。
echo ============================================
echo.
explorer "%~dp0dist"
exit /b 0
