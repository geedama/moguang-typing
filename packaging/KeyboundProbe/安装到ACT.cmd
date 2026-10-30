@echo off
setlocal
title Moguang Typing Installer

if not exist "%~dp0Install-KeyboundProbe.ps1" goto missing_files

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-KeyboundProbe.ps1"
set "INSTALL_EXIT=%ERRORLEVEL%"
echo.
if not "%INSTALL_EXIT%"=="0" echo [ERROR] Installation failed. See the message and log path above.
pause
exit /b %INSTALL_EXIT%

:missing_files
echo.
echo [ERROR] Installer files are incomplete.
echo Extract the entire ZIP first, then run this file from the extracted folder.
echo.
pause
exit /b 1
