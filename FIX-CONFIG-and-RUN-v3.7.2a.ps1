param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"

$configPath = Join-Path $RepositoryRoot "Source\appsettings.Accounting.local.json"
$runnerPath = Join-Path $RepositoryRoot "RUN-MVP-v3.7.2.ps1"
$backupRoot = Join-Path $RepositoryRoot "_backups\v3.7.2a-configfix"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

if (-not (Test-Path $configPath)) {
    throw "Accounting config file not found: $configPath"
}

if (-not (Test-Path $runnerPath)) {
    throw "MVP runner not found: $runnerPath"
}

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
Copy-Item $configPath (Join-Path $backupRoot "appsettings.Accounting.local.$stamp.json") -Force

$validJson = @'
{
  "Accounting": {
    "FiscalYear": 1405,
    "Server": "corei5",
    "Database": "daftar1405"
  },
  "ConnectionStrings": {
    "AccountingLegacy": "Server=corei5;Database=daftar1405;User Id=sa;Password=;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;",
    "AccountingLegacyAdo": "Provider=SQLOLEDB;Data Source=corei5;Initial Catalog=daftar1405;User ID=sa;Password=;"
  }
}
'@

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, $validJson, $utf8NoBom)

# Validate before continuing.
try {
    $null = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "The repaired JSON is still invalid: $($_.Exception.Message)"
}

Write-Host "Accounting configuration repaired successfully." -ForegroundColor Green
Write-Host "Backup: $backupRoot" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Continuing MVP v3.7.2..." -ForegroundColor Cyan

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runnerPath -RepositoryRoot $RepositoryRoot
exit $LASTEXITCODE
