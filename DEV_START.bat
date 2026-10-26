@echo off
setlocal
title Lalab Auto Report - Start Dev

echo ===================================================
echo   Lalab Auto Report - Khoi dong Development
echo ===================================================

:: Kiem tra neu dang chay
powershell -NoProfile -Command ^
    "$proc = Get-Process -Name 'LalabAutoReport.UI' -ErrorAction SilentlyContinue; " ^
    "if (-not $proc) { " ^
    "    $dotnetProc = Get-CimInstance Win32_Process -Filter \"CommandLine LIKE '%%LalabAutoReport.UI%%' AND Name LIKE 'dotnet%%'\" -ErrorAction SilentlyContinue; " ^
    "    if ($dotnetProc) { exit 10 } " ^
    "} else { exit 10 }; exit 0"

if errorlevel 10 (
    echo [INFO] Ung dung dang chay. Dang dung tien trinh cu truoc khi mo lai...
    powershell -NoProfile -Command "Get-Process -Name 'LalabAutoReport.UI' -ErrorAction SilentlyContinue | Stop-Process -Force; Get-CimInstance Win32_Process -Filter \"CommandLine LIKE '%%LalabAutoReport.UI%%' AND Name LIKE 'dotnet%%'\" -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    ping -n 2 127.0.0.1 >nul
)

:: 1. Build Debug
echo [1/3] Dang build Debug...
dotnet build src\LalabAutoReport.UI\LalabAutoReport.UI.csproj -c Debug --nologo -v q
if errorlevel 1 (
    echo [ERROR] Build that bai!
    exit /b 1
)

:: 2. Start Application
echo [2/3] Dang khoi dong ung dung...
set "EXE_PATH=%~dp0src\LalabAutoReport.UI\bin\Debug\net10.0-windows\LalabAutoReport.UI.exe"
if exist "%EXE_PATH%" (
    start "" "%EXE_PATH%"
) else (
    start "" dotnet run --project "%~dp0src\LalabAutoReport.UI" -c Debug --no-build
)

:: 3. Health Check
echo [3/3] Kiem tra trang thai ung dung (Health Check)...
ping -n 3 127.0.0.1 >nul

powershell -NoProfile -Command ^
    "$proc = Get-Process -Name 'LalabAutoReport.UI' -ErrorAction SilentlyContinue; " ^
    "if (-not $proc) { " ^
    "    $dotnetProc = Get-CimInstance Win32_Process -Filter \"CommandLine LIKE '%%LalabAutoReport.UI%%' AND Name LIKE 'dotnet%%'\" -ErrorAction SilentlyContinue; " ^
    "    if ($dotnetProc) { $procId = $dotnetProc.ProcessId } " ^
    "} else { $procId = $proc.Id }; " ^
    "if ($procId) { " ^
    "    Write-Host '[SUCCESS] Lalab Auto Report dang chay thanh cong (PID: ' $procId ')!' -ForegroundColor Green; " ^
    "    Write-Host '          Ung dung san sang de kiem thu.' -ForegroundColor Green; " ^
    "    exit 0; " ^
    "} else { " ^
    "    Write-Host '[FAIL] Ung dung chua khoi dong thanh cong.' -ForegroundColor Red; " ^
    "    Write-Host '       Kiem tra log tai: %LocalAppData%\LalabAutoReport\logs' -ForegroundColor Red; " ^
    "    exit 1; " ^
    "}"

if errorlevel 1 (
    echo [WARNING] Health check that bai. Kiem tra log tai %%LocalAppData%%\LalabAutoReport\logs
    exit /b 1
)

echo ===================================================
echo   Ung dung da san sang!
echo ===================================================
