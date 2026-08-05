param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = Join-Path $RepositoryRoot "Source"
$configPath = Join-Path $source "appsettings.Accounting.local.json"
$tools = Join-Path $RepositoryRoot "tools"
$packageTools = Join-Path $RepositoryRoot "mvp-v3.7.2-tools"
$mappingSource = Join-Path $RepositoryRoot "mvp-v3.7.2-config\manual-customer-mappings.csv"
$mappingTarget = Join-Path $RepositoryRoot "config\manual-customer-mappings.csv"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $RepositoryRoot "_backups\MVP-v3.7.2-$stamp"
$logRoot = Join-Path $RepositoryRoot "Logs"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path (Join-Path $source "Program.cs"))) {
    throw "Source\Program.cs was not found. Extract this package inside D:\DigiAhan\CDR3.1.0git."
}

if (-not (Test-Path $configPath)) {
    throw "Source\appsettings.Accounting.local.json was not found."
}

foreach ($file in @(
    "accounting-bridge-v3.7.2.ps1",
    "rebuild-customer-identity.ps1",
    "test-all-connections-v3.7.2.ps1",
    "install-accounting-task-v3.7.2.ps1"
)) {
    if (-not (Test-Path (Join-Path $packageTools $file))) {
        throw "MVP package file is missing: $file"
    }
}

Write-Host ""
Write-Host "DigiAhan MVP Connectivity v3.7.2" -ForegroundColor Cyan
Write-Host "Accounting + Didar + Issabel/VoIP + DigiAhan_CDR" -ForegroundColor DarkGray
Write-Host ""

Write-Host "[1/9] Stopping previous dashboard..." -ForegroundColor Cyan
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "[2/9] Backing up configuration and scripts..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backup | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $mappingTarget) | Out-Null
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

Copy-Item $configPath (Join-Path $backup "appsettings.Accounting.local.json") -Force

foreach ($file in @(
    "accounting-bridge-v3.7.2.ps1",
    "rebuild-customer-identity.ps1",
    "test-all-connections-v3.7.2.ps1",
    "install-accounting-task-v3.7.2.ps1"
)) {
    $current = Join-Path $tools $file
    if (Test-Path $current) {
        Copy-Item $current (Join-Path $backup $file) -Force
    }

    Copy-Item (Join-Path $packageTools $file) $current -Force
}

if (Test-Path $mappingTarget) {
    Copy-Item $mappingTarget (Join-Path $backup "manual-customer-mappings.csv") -Force
}
Copy-Item $mappingSource $mappingTarget -Force

Write-Host "[3/9] Writing a direct SQL2000 ADODB connection..." -ForegroundColor Cyan
$json = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json

$accountingProperty = $json.PSObject.Properties["Accounting"]
if (-not $accountingProperty) {
    $accounting = [pscustomobject]@{
        FiscalYear = 1405
        Server = "corei5"
        Database = "daftar1405"
    }
    $json | Add-Member -NotePropertyName "Accounting" -NotePropertyValue $accounting
}
else {
    $accounting = $accountingProperty.Value
}

$serverProperty = $accounting.PSObject.Properties["Server"]
$databaseProperty = $accounting.PSObject.Properties["Database"]
$yearProperty = $accounting.PSObject.Properties["FiscalYear"]

$server = if ($serverProperty -and $serverProperty.Value) { [string]$serverProperty.Value } else { "corei5" }
$database = if ($databaseProperty -and $databaseProperty.Value) { [string]$databaseProperty.Value } else { "daftar1405" }
$fiscalYear = if ($yearProperty -and $yearProperty.Value) { [int]$yearProperty.Value } else { 1405 }

$connectionStringsProperty = $json.PSObject.Properties["ConnectionStrings"]
if (-not $connectionStringsProperty) {
    $connectionStrings = [pscustomobject]@{}
    $json | Add-Member -NotePropertyName "ConnectionStrings" -NotePropertyValue $connectionStrings
}
else {
    $connectionStrings = $connectionStringsProperty.Value
}

$legacySql = "Server=$server;Database=$database;User Id=sa;Password=;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;"
$legacyAdo = "Provider=SQLOLEDB;Data Source=$server;Initial Catalog=$database;User ID=sa;Password=;"

$legacyProperty = $connectionStrings.PSObject.Properties["AccountingLegacy"]
if ($legacyProperty) {
    $connectionStrings.AccountingLegacy = $legacySql
}
else {
    $connectionStrings | Add-Member -NotePropertyName "AccountingLegacy" -NotePropertyValue $legacySql
}

$adoProperty = $connectionStrings.PSObject.Properties["AccountingLegacyAdo"]
if ($adoProperty) {
    $connectionStrings.AccountingLegacyAdo = $legacyAdo
}
else {
    $connectionStrings | Add-Member -NotePropertyName "AccountingLegacyAdo" -NotePropertyValue $legacyAdo
}

$json | ConvertTo-Json -Depth 20 | Set-Content -Path $configPath -Encoding UTF8

Write-Host "Direct ADO login configured: Server=$server Database=$database Login=sa" -ForegroundColor Green

Write-Host "[4/9] Building current application as v3.7.2..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)
$program = [regex]::Replace(
    $program,
    'const string AppVersion = "[^"]+";',
    'const string AppVersion = "3.7.2";'
)
$program = [regex]::Replace(
    $program,
    'const string BuildDate = "[^"]+";',
    'const string BuildDate = "2026-08-03";'
)
[IO.File]::WriteAllText($programPath,$program,$utf8)

$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.7.2</Version>')
$project = [regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.7.2.0</AssemblyVersion>')
$project = [regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.7.2.0</FileVersion>')
[IO.File]::WriteAllText($projectPath,$project,$utf8)

Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}
finally {
    Pop-Location
}

Write-Host "[5/9] Synchronizing accounting through direct ADODB..." -ForegroundColor Cyan
$bridge = Join-Path $tools "accounting-bridge-v3.7.2.ps1"

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridge `
    -RepositoryRoot $RepositoryRoot `
    -Days 60

if ($LASTEXITCODE -ne 0) {
    throw "Accounting or customer identity synchronization failed."
}

Write-Host "[6/9] Installing automatic 15-minute accounting sync..." -ForegroundColor Cyan
$taskInstaller = Join-Path $tools "install-accounting-task-v3.7.2.ps1"

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskInstaller `
        -RepositoryRoot $RepositoryRoot `
        -EveryMinutes 15 `
        -Days 60

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Scheduled task was not installed. Manual sync still works."
    }
}
catch {
    Write-Warning "Scheduled task was not installed: $($_.Exception.Message)"
}

Write-Host "[7/9] Starting the receiver in background..." -ForegroundColor Cyan
$appOut = Join-Path $logRoot "MVP-v3.7.2-app-output.log"
$appError = Join-Path $logRoot "MVP-v3.7.2-app-error.log"

Remove-Item $appOut,$appError -Force -ErrorAction SilentlyContinue

$appProcess = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList @("run","--no-build","--no-restore") `
    -WorkingDirectory $source `
    -RedirectStandardOutput $appOut `
    -RedirectStandardError $appError `
    -WindowStyle Hidden `
    -PassThru

$healthy = $false
for ($i=0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1

    try {
        $health = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3
        if ($health.status -eq "healthy") {
            $healthy = $true
            break
        }
    }
    catch {}

    if ($appProcess.HasExited) {
        break
    }
}

if (-not $healthy) {
    Write-Host "Application output:" -ForegroundColor Yellow
    if (Test-Path $appOut) { Get-Content $appOut -Tail 40 }
    if (Test-Path $appError) { Get-Content $appError -Tail 40 }
    throw "Receiver did not become healthy on port 5088."
}

Write-Host "Receiver is healthy. PID=$($appProcess.Id)" -ForegroundColor Green

Write-Host "[8/9] Testing accounting, Didar, CDR and VoIP identity flow..." -ForegroundColor Cyan
$tester = Join-Path $tools "test-all-connections-v3.7.2.ps1"

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tester `
    -RepositoryRoot $RepositoryRoot `
    -ServerUrl "http://localhost:5088"

$testExit = $LASTEXITCODE
if ($testExit -ne 0) {
    Write-Warning "The diagnostic script returned an error. The application remains running for inspection."
}

Write-Host "[9/9] MVP is running..." -ForegroundColor Green
Write-Host ""
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Application PID: $($appProcess.Id)" -ForegroundColor DarkGray
Write-Host "Backup: $backup" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Final real Issabel test:" -ForegroundColor Cyan
Write-Host "digiahan-test-ring 201 09121395663" -ForegroundColor White
Write-Host ""
Write-Host "The app is running in background; this PowerShell window may now be closed." -ForegroundColor Green
