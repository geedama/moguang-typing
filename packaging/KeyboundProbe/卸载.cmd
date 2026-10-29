@echo off
chcp 65001 >nul
title 卸载魔光打字通
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-KeyboundProbe.ps1"
echo.
pause
