@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CREATE-LOG-ZIP-v4.0.1.ps1"
pause
