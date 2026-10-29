@echo off
setlocal
title Moguang Typing Uninstaller

if not exist "%~dp0Uninstall-KeyboundProbe.ps1" goto missing_files

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-KeyboundProbe.ps1"
set "UNINSTALL_EXIT=%ERRORLEVEL%"
echo.
if not "%UNINSTALL_EXIT%"=="0" echo [ERROR] Uninstallation failed. See the message and log path above.
pause
exit /b %UNINSTALL_EXIT%

:missing_files
echo.
echo [ERROR] Uninstaller files are incomplete.
echo Run this file from a fully extracted release package.
echo.
pause
exit /b 1
