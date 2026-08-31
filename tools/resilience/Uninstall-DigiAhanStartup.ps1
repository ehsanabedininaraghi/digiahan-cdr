$ErrorActionPreference = "Stop"
$launcherPath = Join-Path ([Environment]::GetFolderPath("Startup")) "DigiAhan Dashboard Watchdog.cmd"
if (Test-Path -LiteralPath $launcherPath) {
    Remove-Item -LiteralPath $launcherPath -Force
    Write-Host "Startup launcher removed. The running dashboard was not stopped." -ForegroundColor Green
}
else {
    Write-Host "Startup launcher is not installed." -ForegroundColor Yellow
}
