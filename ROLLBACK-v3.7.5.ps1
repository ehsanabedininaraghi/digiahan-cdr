param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
$backup = Get-ChildItem (Join-Path $RepositoryRoot "_backups") `
    -Directory -Filter "v3.7.5-*" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $backup) {
    throw "No v3.7.5 backup was found."
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
    "AccountingModels.cs" = "Source\Models\AccountingModels.cs"
    "Program.cs" = "Source\Program.cs"
    "DigiAhan.CDR.Receiver.csproj" = "Source\DigiAhan.CDR.Receiver.csproj"
    "accounting-bridge-v3.7.4.ps1" = "tools\accounting-bridge-v3.7.4.ps1"
    "rebuild-customer-identity.ps1" = "tools\rebuild-customer-identity.ps1"
    "rebuild-customer-identity-v3.7.4.ps1" = "tools\rebuild-customer-identity-v3.7.4.ps1"
    "test-all-connections-v3.7.4.ps1" = "tools\test-all-connections-v3.7.4.ps1"
}

foreach ($name in $restoreMap.Keys) {
    $sourceFile = Join-Path $backup.FullName $name
    if (Test-Path $sourceFile) {
        Copy-Item $sourceFile (Join-Path $RepositoryRoot $restoreMap[$name]) -Force
    }
}

Write-Host "Restored backup: $($backup.FullName)" -ForegroundColor Green
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $RepositoryRoot "START-DASHBOARD-NOW-v3.7.5.ps1") `
    -RepositoryRoot $RepositoryRoot
