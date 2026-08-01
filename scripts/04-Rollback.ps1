param(
    [Parameter(Mandatory=$true)][string]$BackupPath,
    [string]$InstallPath = "D:\DigiAhan\CDR-Live",
    [string]$ServiceName = ""
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $BackupPath)) { throw "Backup path پیدا نشد." }
if ($ServiceName -and (Get-Service $ServiceName -ErrorAction SilentlyContinue)) { Stop-Service $ServiceName -Force }
Remove-Item "$InstallPath\*" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$BackupPath\*" $InstallPath -Recurse -Force
if ($ServiceName -and (Get-Service $ServiceName -ErrorAction SilentlyContinue)) { Start-Service $ServiceName }
Write-Host "Rollback انجام شد." -ForegroundColor Green
