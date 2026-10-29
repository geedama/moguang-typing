@echo off
setlocal
chcp 65001 >nul
title 安装魔光打字通
if not exist "%~dp0Install-KeyboundProbe.ps1" (
    color 0C
    echo.
    echo [安装失败] 没有找到安装脚本。
    echo 请先右键 ZIP 选择“全部解压”，再从解压后的文件夹运行本程序。
    echo.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-KeyboundProbe.ps1"
set "INSTALL_EXIT=%ERRORLEVEL%"
echo.
if not "%INSTALL_EXIT%"=="0" (
    color 0C
    echo [安装未完成] 请查看上方错误说明；需要反馈时请附上错误日志。
    echo.
)
pause
exit /b %INSTALL_EXIT%
