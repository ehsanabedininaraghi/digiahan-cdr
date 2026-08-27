@echo off
setlocal
set "REPO_ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%tools\resilience\DigiAhan-Watchdog.ps1" -ConfigPath "%REPO_ROOT%tools\resilience\resilience.config.json" -RepositoryRoot "%REPO_ROOT%" -Once
if errorlevel 1 pause
