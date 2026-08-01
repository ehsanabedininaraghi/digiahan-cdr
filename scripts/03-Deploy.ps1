param(
    [string]$InstallPath = "D:\DigiAhan\CDR-Live",
    [string]$ServiceName = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$newPublish = "$root\runtime\publish-new"
if (-not (Test-Path $newPublish)) { throw "ابتدا scripts\02-Update-Build.ps1 را اجرا کنید." }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$root\runtime\backups\$stamp"
New-Item -ItemType Directory -Force -Path $backup | Out-Null

if ($ServiceName -and (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    Stop-Service $ServiceName -Force
}

if (Test-Path $InstallPath) {
    Copy-Item "$InstallPath\*" $backup -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null

# Preserve local secrets/config
$config = Join-Path $InstallPath "appsettings.json"
$tempConfig = Join-Path $env:TEMP "digiahan-appsettings-$stamp.json"
if (Test-Path $config) { Copy-Item $config $tempConfig -Force }

Remove-Item "$InstallPath\*" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$newPublish\*" $InstallPath -Recurse -Force
if (Test-Path $tempConfig) { Copy-Item $tempConfig $config -Force }

if ($ServiceName -and (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    Start-Service $ServiceName
}

Write-Host "Deploy انجام شد. Backup: $backup" -ForegroundColor Green
