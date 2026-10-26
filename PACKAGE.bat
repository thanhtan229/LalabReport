@echo off
setlocal
title Lalab Auto Report - Package Release

echo ===================================================
echo   Lalab Auto Report - Packaging Release Standalone
echo ===================================================
echo [NOTICE] Packaging tao ban build phat hanh (Release), khong dung cho dev loop thuong ngay.
echo.

:: 1. Run Automated Tests
echo [1/3] Dang kiem tra toan bo test suite truoc khi dong goi...
dotnet test --nologo
if errorlevel 1 (
    echo [ERROR] Test suite that bai! Huy dong goi phat hanh de dam bao chat luong.
    exit /b 1
)
echo [OK] Toan bo test case da vuot qua.

:: 2. Publish Single-File Release
echo [2/3] Dang dong goi Single-File Executable (win-x64)...
dotnet publish src\LalabAutoReport.UI\LalabAutoReport.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo
if errorlevel 1 (
    echo [ERROR] Publish that bai!
    exit /b 1
)

:: 3. Verify Artifact
echo [3/3] Kiem tra file thuc thi sau dong goi...
set "EXE_PATH=src\LalabAutoReport.UI\bin\Release\net10.0-windows\win-x64\publish\LalabAutoReport.UI.exe"

if exist "%EXE_PATH%" (
    echo ===================================================
    echo [SUCCESS] Dong goi thanh cong!
    echo File thuc thi : %EXE_PATH%
    powershell -NoProfile -Command "$size = (Get-Item '%EXE_PATH%').Length / 1MB; Write-Host ('Dung luong      : ' + [math]::Round($size, 2) + ' MB') -ForegroundColor Green"
    echo ===================================================
    exit /b 0
) else (
    echo [ERROR] Khong tim thay file thuc thi sau khi publish tai %EXE_PATH%!
    exit /b 1
)
