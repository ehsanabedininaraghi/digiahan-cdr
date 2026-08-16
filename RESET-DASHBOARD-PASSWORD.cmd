@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RESET-DASHBOARD-PASSWORD.ps1"
if errorlevel 1 (
  echo.
  echo Password reset failed.
  pause
  exit /b 1
)
echo.
pause
