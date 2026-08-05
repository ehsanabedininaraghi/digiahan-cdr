@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0START-DASHBOARD-NOW-v3.7.7.ps1"
pause
