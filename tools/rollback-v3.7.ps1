param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
$source = Join-Path $RepositoryRoot "Source"
$backupRoot = Join-Path $RepositoryRoot "_backups"

$backup = Get-ChildItem $backupRoot -Directory -Filter "v3.7.0-*" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1

if (-not $backup) {
    throw "No v3.7.0 backup was found."
}

Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Get-ChildItem $backup.FullName -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($backup.FullName.Length).TrimStart('\')
    $target = Join-Path $source $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target -Force
}

Write-Host "Source files restored from: $($backup.FullName)" -ForegroundColor Green
Set-Location $source
dotnet build --no-restore
if ($LASTEXITCODE -ne 0) { throw "Restored version build failed." }
dotnet run --no-build --no-restore
