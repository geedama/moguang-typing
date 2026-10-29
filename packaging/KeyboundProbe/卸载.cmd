@echo off
setlocal
chcp 65001 >nul
title 卸载魔光打字通
if not exist "%~dp0Uninstall-KeyboundProbe.ps1" (
    color 0C
    echo.
    echo [卸载失败] 没有找到卸载脚本，请从完整解压后的发布包运行。
    echo.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-KeyboundProbe.ps1"
set "UNINSTALL_EXIT=%ERRORLEVEL%"
echo.
if not "%UNINSTALL_EXIT%"=="0" (
    color 0C
    echo [卸载未完成] 请查看上方错误说明。
    echo.
)
pause
exit /b %UNINSTALL_EXIT%
