param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"

$backup = Get-ChildItem (Join-Path $RepositoryRoot "_backups") `
    -Directory -Filter "v3.7.6-*" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $backup) {
    throw "No v3.7.6 backup was found."
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

$restoreMap = @{
    "Program.cs" = "Source\Program.cs"
    "AgentPanelRepository.cs" = "Source\Services\AgentPanelRepository.cs"
    "DigiAhan.CDR.Receiver.csproj" = "Source\DigiAhan.CDR.Receiver.csproj"
}

foreach ($name in $restoreMap.Keys) {
    $backupFile = Join-Path $backup.FullName $name
    if (Test-Path $backupFile) {
        Copy-Item $backupFile (Join-Path $RepositoryRoot $restoreMap[$name]) -Force
    }
}

Write-Host "Rollback restored: $($backup.FullName)" -ForegroundColor Green

& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $RepositoryRoot "START-DASHBOARD-NOW-v3.7.6.ps1") `
    -RepositoryRoot $RepositoryRoot
