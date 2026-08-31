@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ROLLBACK-v4.4.0.ps1" -RepositoryRoot "D:\DigiAhan\CDR4.0"
if errorlevel 1 (
  echo.
  echo Rollback failed. Check Logs\Runs\rollback-v4.4.0-*.
  pause
  exit /b 1
)
echo.
echo Rollback completed successfully.
pause
