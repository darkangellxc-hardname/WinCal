@echo off
cd /d "%~dp0"

set "DOTNET="

:: 候选 1：用户级安装（dotnet-install 脚本的默认位置，无需管理员权限）
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
    "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" --list-sdks >nul 2>nul
    if not errorlevel 1 set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
)

:: 候选 2：系统级安装
if not defined DOTNET if exist "C:\Program Files\dotnet\dotnet.exe" (
    "C:\Program Files\dotnet\dotnet.exe" --list-sdks >nul 2>nul
    if not errorlevel 1 set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
)

if not defined DOTNET (
    echo.
    echo [错误] 未找到带 .NET SDK 的 dotnet.exe
    echo        仅安装了 Runtime 是不够的，编译需要 SDK。
    echo.
    echo 安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0
    echo 或免提权安装到用户目录：
    echo   powershell -NoProfile -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel 8.0 -InstallDir "%%LOCALAPPDATA%%\Microsoft\dotnet"
    echo.
    pause
    exit /b 1
)

echo 使用 SDK: %DOTNET%
echo.
"%DOTNET%" build %*
pause
