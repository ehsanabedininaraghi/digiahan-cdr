@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RUN-v4.3.8.ps1" -RepositoryRoot "D:\DigiAhan\CDR4.0"
if errorlevel 1 (
  echo.
  echo Installation failed. Check Logs\Runs in the repository.
  pause
  exit /b 1
)
echo.
echo v4.3.8 installed successfully.
pause
