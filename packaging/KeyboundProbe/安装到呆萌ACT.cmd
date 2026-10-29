@echo off
chcp 65001 >nul
title 安装魔光打字通
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-KeyboundProbe.ps1"
echo.
pause
