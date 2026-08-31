@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RUN-v4.4.0.ps1" -RepositoryRoot "D:\DigiAhan\CDR4.0"
if errorlevel 1 (
  echo.
  echo Installation failed. The current system was backed up. Check Logs\Runs in the repository.
  pause
  exit /b 1
)
echo.
echo v4.4.0 installed successfully. Journey v3 remains disabled until pilot configuration is approved.
pause
