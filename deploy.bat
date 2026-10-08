@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " ^
  "$u = { param($s) [regex]::Unescape($s) }; " ^
  "Write-Host \"`n[1/3] \" -NoNewline -ForegroundColor Cyan; Write-Host (& $u '\u6b63\u5728\u91ca\u653e\u540e\u53f0 MCP \u8fdb\u7a0b\u5360\u7528...'); " ^
  "Stop-Process -Name 'TdxClaw.Mcp' -Force -ErrorAction SilentlyContinue; " ^
  "$mainProj = Get-ChildItem -Directory | Where-Object { $_.Name -notlike '*.*' -and (Test-Path (Join-Path $_.FullName ($_.Name + '.csproj'))) } | Select-Object -First 1; " ^
  "$mcpProj = Get-ChildItem -Directory -Filter '*.Mcp' | Select-Object -First 1; " ^
  "Write-Host \"`n[2/3] \" -NoNewline -ForegroundColor Cyan; Write-Host (& $u '\u6b63\u5728\u53d1\u5e03\u5355\u6587\u4ef6\u4e3b\u7a0b\u5e8f dist\\\u7f16\u8f91\u5668.exe (win-x64)...'); " ^
  "dotnet publish (Join-Path $mainProj.FullName ($mainProj.Name + '.csproj')) -c Release -p:EnableSingleFilePublish=true -o dist; " ^
  "if ($LASTEXITCODE -ne 0) { Write-Host (& $u '\n[\u9519\u8bef] \u4e3b\u7a0b\u5e8f\u53d1\u5e03\u5931\u8d25\uff01\u8bf7\u5148\u5173\u95ed\u6b63\u5728\u8fd0\u884c\u7684\u7f16\u8f91\u5668\u7a97\u53e3\u540e\u91cd\u8bd5\u3002') -ForegroundColor Red; exit 1 }; " ^
  "Get-ChildItem -Path 'dist' -File | Where-Object { $_.Extension -ne '.exe' } | Remove-Item -Force -ErrorAction SilentlyContinue; " ^
  "if (Test-Path 'dist\runtimes') { Remove-Item 'dist\runtimes' -Recurse -Force -ErrorAction SilentlyContinue }; " ^
  "if (Test-Path 'dist\LatoFont') { Remove-Item 'dist\LatoFont' -Recurse -Force -ErrorAction SilentlyContinue }; " ^
  "Write-Host \"`n[3/3] \" -NoNewline -ForegroundColor Cyan; Write-Host (& $u '\u6b63\u5728\u53d1\u5e03 MCP \u670d\u52a1\u5668 dist\\mcp\\TdxClaw.Mcp.exe...'); " ^
  "dotnet publish (Join-Path $mcpProj.FullName ($mcpProj.Name + '.csproj')) -c Release -o dist\mcp; " ^
  "if ($LASTEXITCODE -ne 0) { Write-Host (& $u '[\u8b66\u544a] MCP \u670d\u52a1\u5668\u53d1\u5e03\u5931\u8d25') -ForegroundColor Yellow }; " ^
  "Write-Host \"`n============================================\" -ForegroundColor Green; " ^
  "Write-Host (& $u ' \u53d1\u5e03\u5b8c\u6210\uff1a') -ForegroundColor Green; " ^
  "Write-Host (& $u '   dist\\\u7f16\u8f91\u5668.exe          \u4e3b\u7a0b\u5e8f\uff08\u5355\u6587\u4ef6\uff0c\u62f7\u8d70\u5373\u7528\uff09'); " ^
  "Write-Host (& $u '   dist\\mcp\\TdxClaw.Mcp.exe MCP \u670d\u52a1\u5668\uff08\u63a5\u5165 AI \u5ba2\u6237\u7aef\u7528\uff09'); " ^
  "Write-Host \"============================================`n\" -ForegroundColor Green; " ^
  "if (-not $env:NO_EXPLORER) { Start-Process explorer.exe -ArgumentList (Join-Path $PWD 'dist') }"
if errorlevel 1 (
    if "%NO_PAUSE%"=="" pause
    exit /b 1
)
exit /b 0
