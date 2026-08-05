@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FIX-CONFIG-and-RUN-v3.7.2a.ps1"
pause
