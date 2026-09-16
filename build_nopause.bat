@echo off
cd /d "%~dp0"

set "DOTNET="

if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
    "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" --list-sdks >nul 2>nul
    if not errorlevel 1 set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
)

if not defined DOTNET if exist "C:\Program Files\dotnet\dotnet.exe" (
    "C:\Program Files\dotnet\dotnet.exe" --list-sdks >nul 2>nul
    if not errorlevel 1 set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
)

if not defined DOTNET (
    echo [错误] 未找到带 .NET SDK 的 dotnet.exe，请先安装 .NET 8 SDK。
    exit /b 1
)

"%DOTNET%" build -c Debug %*
exit /b %ERRORLEVEL%
