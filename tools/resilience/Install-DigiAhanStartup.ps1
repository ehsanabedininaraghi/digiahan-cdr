param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$toolsRoot = Join-Path $RepositoryRoot "tools\resilience"
$watchdog = Join-Path $toolsRoot "DigiAhan-Watchdog.ps1"
$configPath = Join-Path $toolsRoot "resilience.config.json"
if (-not (Test-Path -LiteralPath $watchdog)) { throw "Watchdog was not found: $watchdog" }

$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$config.RepositoryRoot = $RepositoryRoot
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8

if (-not $SkipBuild) {
    & (Join-Path $toolsRoot "Build-ResilientDashboard.ps1") -RepositoryRoot $RepositoryRoot
}

$startupDirectory = [Environment]::GetFolderPath("Startup")
$launcherPath = Join-Path $startupDirectory "DigiAhan Dashboard Watchdog.cmd"
$launcher = @"
@echo off
start "" /min powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$watchdog" -ConfigPath "$configPath"
"@
Set-Content -LiteralPath $launcherPath -Value $launcher -Encoding Ascii

$arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}"' -f $watchdog, $configPath
Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WindowStyle Hidden

Write-Host "Startup protection installed for the current Windows user." -ForegroundColor Green
Write-Host "Launcher: $launcherPath" -ForegroundColor DarkGray
Write-Host "Dashboard: $($config.DashboardUrl)" -ForegroundColor Cyan
