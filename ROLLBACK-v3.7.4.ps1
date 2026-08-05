param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"

$backupRoot = Join-Path $RepositoryRoot "_backups"
$backup = Get-ChildItem $backupRoot -Directory -Filter "v3.7.4-*" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $backup) {
    throw "No v3.7.4 backup was found."
}

try {
    Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_.OwningProcess -gt 0) {
                Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
            }
        }
}
catch {}

$sourceBackup = Join-Path $backup.FullName "Source"
$toolsBackup = Join-Path $backup.FullName "tools"
$configBackup = Join-Path $backup.FullName "config"

if (-not (Test-Path $sourceBackup)) {
    throw "Source backup is missing: $sourceBackup"
}

Copy-Item (Join-Path $sourceBackup "*") (Join-Path $RepositoryRoot "Source") -Recurse -Force

if (Test-Path $toolsBackup) {
    Copy-Item (Join-Path $toolsBackup "*") (Join-Path $RepositoryRoot "tools") -Recurse -Force
}

if (Test-Path $configBackup) {
    Copy-Item (Join-Path $configBackup "*") (Join-Path $RepositoryRoot "config") -Recurse -Force
}

Write-Host "Rollback restored: $($backup.FullName)" -ForegroundColor Green

& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $RepositoryRoot "START-DASHBOARD-NOW-v3.7.4.ps1") `
    -RepositoryRoot $RepositoryRoot
