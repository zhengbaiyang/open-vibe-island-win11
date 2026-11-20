@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"

echo.
echo   Open Island 0.1.4  -  安装助手
echo   ==================================================
echo   本助手会：
echo     1. 解除当前文件夹内文件的“网络下载锁定”（Unblock-File）
echo     2. 准备一个可写的临时目录（解决安装时“错误 5: 拒绝访问”）
echo     3. 尝试修复 Windows 用户 Temp 目录权限
echo     4. 启动 Open Island 安装程序
echo.
echo   如果本窗口本身被 Windows 拦截，请先对下载的压缩包
echo   右键 - 属性 - 勾选“解除锁定” 后再解压运行。
echo.
pause

powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath (Get-Location) -File | Unblock-File; Write-Host '[OK] 已解除当前文件夹内所有文件的下载锁定' -ForegroundColor Green"

rem ---------------------------------------------------------------------
rem Some PCs have a broken / read-only %%LOCALAPPDATA%%\Temp folder. Inno Setup
rem extracts itself there first, so the installer dies with
rem "无法创建目录 ...is-xxxx.tmp  错误 5: 拒绝访问". Redirect TEMP/TMP to a
rem writable folder for this installation only.
rem ---------------------------------------------------------------------
set "OITEMP="
for %%D in (
    "%LOCALAPPDATA%\OpenIsland\install-temp"
    "%USERPROFILE%\OpenIsland-install-temp"
    "%PUBLIC%\OpenIsland-install-temp"
    "%SystemDrive%\OpenIsland-install-temp"
) do (
    if not defined OITEMP (
        mkdir "%%~D" >nul 2>&1
        if exist "%%~D" set "OITEMP=%%~D"
    )
)

if not defined OITEMP (
    echo.
    echo   [错误] 找不到可写的临时目录。
    echo   请手动选择一个可写目录（例如 D:\Temp），然后在命令提示符中执行：
    echo       set TEMP=D:\Temp
    echo       set TMP=D:\Temp
    echo       OpenIsland-Setup-0.1.4-win-x64.exe
    echo   或直接使用便携版 ZIP（无需安装器临时目录）。
    pause
    exit /b 1
)

echo.
echo   安装临时目录：%OITEMP%

rem Best-effort repair of the normal user Temp folder so future installers work too.
if not exist "%LOCALAPPDATA%\Temp" mkdir "%LOCALAPPDATA%\Temp" >nul 2>&1
icacls "%LOCALAPPDATA%\Temp" /grant "%USERNAME%":(OI)(CI)F >nul 2>&1
if errorlevel 1 (
    echo   [提示] 无法自动修复 %%LOCALAPPDATA%%\Temp 权限（不影响本次安装，
    echo          本次安装已改用上面的可写临时目录）。
)

rem Launch the installer with the redirected temp directory.
set "TEMP=%OITEMP%"
set "TMP=%OITEMP%"

echo.
echo   正在启动安装程序...
for %%F in (OpenIsland-Setup-*.exe) do (
    start "" "%%~fF"
    goto :started
)
echo   [错误] 当前文件夹内没有找到 OpenIsland-Setup-*.exe
pause
exit /b 1

:started
exit /b 0
