param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [int]$AccountingDays = 60
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = Join-Path $RepositoryRoot "Source"
$payload = Join-Path $PSScriptRoot "payload"
$payloadSource = Join-Path $payload "Source"
$payloadTools = Join-Path $payload "tools"
$payloadConfig = Join-Path $payload "config"
$tools = Join-Path $RepositoryRoot "tools"
$config = Join-Path $RepositoryRoot "config"
$logs = Join-Path $RepositoryRoot "Logs"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $RepositoryRoot "_backups\v3.7.4-$stamp"

function Stop-Dashboard {
    Write-Host "[1/10] Stopping only the process listening on port 5088..." -ForegroundColor Cyan

    try {
        $listeners = Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue
        foreach ($listener in $listeners) {
            if ($listener.OwningProcess -gt 0) {
                Stop-Process -Id $listener.OwningProcess -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        Write-Warning "Could not inspect port 5088: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 2
}

function Set-JsonProperty {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($property) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Repair-AccountingConfiguration {
    param([string]$Path)

    $json = $null

    if (Test-Path $Path) {
        try {
            $json = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            Copy-Item $Path (Join-Path $backup "appsettings.Accounting.local.invalid.json") -Force
            Write-Warning "Accounting JSON was invalid and was rebuilt."
        }
    }

    if (-not $json) {
        $json = [pscustomobject]@{}
    }

    $accountingProperty = $json.PSObject.Properties["Accounting"]
    if ($accountingProperty) {
        $accounting = $accountingProperty.Value
    }
    else {
        $accounting = [pscustomobject]@{}
        Set-JsonProperty -Object $json -Name "Accounting" -Value $accounting
    }

    $server = "corei5"
    $database = "daftar1405"
    $fiscalYear = 1405

    $serverProperty = $accounting.PSObject.Properties["Server"]
    if ($serverProperty -and $serverProperty.Value) {
        $server = [string]$serverProperty.Value
    }

    $databaseProperty = $accounting.PSObject.Properties["Database"]
    if ($databaseProperty -and $databaseProperty.Value) {
        $database = [string]$databaseProperty.Value
    }

    $yearProperty = $accounting.PSObject.Properties["FiscalYear"]
    if ($yearProperty -and $yearProperty.Value) {
        $fiscalYear = [int]$yearProperty.Value
    }

    Set-JsonProperty -Object $accounting -Name "FiscalYear" -Value $fiscalYear
    Set-JsonProperty -Object $accounting -Name "Server" -Value $server
    Set-JsonProperty -Object $accounting -Name "Database" -Value $database

    $connectionStringsProperty = $json.PSObject.Properties["ConnectionStrings"]
    if ($connectionStringsProperty) {
        $connectionStrings = $connectionStringsProperty.Value
    }
    else {
        $connectionStrings = [pscustomobject]@{}
        Set-JsonProperty -Object $json -Name "ConnectionStrings" -Value $connectionStrings
    }

    $legacySql = "Server=$server;Database=$database;User Id=sa;Password=;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;"
    $legacyAdo = "Provider=SQLOLEDB;Data Source=$server;Initial Catalog=$database;User ID=sa;Password=;"

    Set-JsonProperty -Object $connectionStrings -Name "AccountingLegacy" -Value $legacySql
    Set-JsonProperty -Object $connectionStrings -Name "AccountingLegacyAdo" -Value $legacyAdo

    $json | ConvertTo-Json -Depth 20 | Set-Content -Path $Path -Encoding UTF8

    Write-Host "Accounting connection fixed: $server/$database with SQL login sa." -ForegroundColor Green
}

function Start-Dashboard {
    param([string]$LogPrefix)

    $stdout = Join-Path $logs "$LogPrefix-output.log"
    $stderr = Join-Path $logs "$LogPrefix-error.log"
    Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue

    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("run","--no-build","--no-restore") `
        -WorkingDirectory $source `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru

    $healthy = $false

    for ($i=0; $i -lt 35; $i++) {
        Start-Sleep -Seconds 1

        if ($process.HasExited) {
            break
        }

        try {
            $health = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3
            if ($health.status -eq "healthy") {
                $healthy = $true
                break
            }
        }
        catch {}
    }

    if (-not $healthy) {
        Write-Host "Application output:" -ForegroundColor Yellow
        if (Test-Path $stdout) { Get-Content $stdout -Tail 50 }
        if (Test-Path $stderr) { Get-Content $stderr -Tail 50 }
        throw "Dashboard did not become healthy on port 5088."
    }

    return $process
}

if (-not (Test-Path (Join-Path $source "Program.cs"))) {
    throw "Current project was not found at $source."
}

if (-not (Test-Path (Join-Path $payloadSource "Program.cs"))) {
    throw "v3.7.4 payload is incomplete."
}

Write-Host ""
Write-Host "DigiAhan CDR v3.7.4 - Recovery and Real-Schema Release" -ForegroundColor Cyan
Write-Host "This release was built from the uploaded full project and exported database schema." -ForegroundColor DarkGray
Write-Host ""

Stop-Dashboard

Write-Host "[2/10] Creating a recoverable backup..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backup | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null
New-Item -ItemType Directory -Force -Path $config | Out-Null
New-Item -ItemType Directory -Force -Path $logs | Out-Null

$backupSource = Join-Path $backup "Source"
$backupTools = Join-Path $backup "tools"
$backupConfig = Join-Path $backup "config"
New-Item -ItemType Directory -Force -Path $backupSource,$backupTools,$backupConfig | Out-Null

Copy-Item (Join-Path $source "*") $backupSource -Recurse -Force
if (Test-Path $tools) {
    Copy-Item (Join-Path $tools "*") $backupTools -Recurse -Force
}
if (Test-Path $config) {
    Copy-Item (Join-Path $config "*") $backupConfig -Recurse -Force
}

Write-Host "[3/10] Installing the real-source v3.7.4 files..." -ForegroundColor Cyan
Copy-Item (Join-Path $payloadSource "*") $source -Recurse -Force
Copy-Item (Join-Path $payloadTools "*") $tools -Recurse -Force
Copy-Item (Join-Path $payloadConfig "*") $config -Recurse -Force

$accountingConfig = Join-Path $source "appsettings.Accounting.local.json"
Repair-AccountingConfiguration -Path $accountingConfig

Write-Host "[4/10] Building the dashboard..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "No-restore build failed. Retrying with the local NuGet cache."
        & dotnet build
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}
finally {
    Pop-Location
}

Write-Host "[5/10] Starting dashboard before the accounting import..." -ForegroundColor Cyan
$appProcess = Start-Dashboard -LogPrefix "v3.7.4-app"
Write-Host "Dashboard is healthy. PID=$($appProcess.Id)" -ForegroundColor Green
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow

Write-Host "[6/10] Synchronizing accounting with the actual AccountingSyncRuns schema..." -ForegroundColor Cyan
$syncSucceeded = $false
$bridge = Join-Path $tools "accounting-bridge-v3.7.4.ps1"

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridge `
        -RepositoryRoot $RepositoryRoot `
        -Days $AccountingDays

    $syncSucceeded = ($LASTEXITCODE -eq 0)

    if (-not $syncSucceeded) {
        Write-Warning "Accounting sync failed, but the dashboard remains running."
    }
}
catch {
    Write-Warning "Accounting sync failed, but the dashboard remains running: $($_.Exception.Message)"
}

Write-Host "[7/10] Installing the automatic 15-minute accounting task..." -ForegroundColor Cyan
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $tools "install-accounting-task-v3.7.4.ps1") `
        -RepositoryRoot $RepositoryRoot `
        -EveryMinutes 15 `
        -Days $AccountingDays

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Scheduled task was not installed. The dashboard is still running."
    }
}
catch {
    Write-Warning "Scheduled task was not installed: $($_.Exception.Message)"
}

Write-Host "[8/10] Running end-to-end diagnostics..." -ForegroundColor Cyan
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $tools "test-all-connections-v3.7.4.ps1") `
        -RepositoryRoot $RepositoryRoot `
        -ServerUrl "http://localhost:5088"

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Diagnostics reported one or more failures. Check the report in Logs."
    }
}
catch {
    Write-Warning "Diagnostics could not finish: $($_.Exception.Message)"
}

Write-Host "[9/10] Saving the local release in Git..." -ForegroundColor Cyan
try {
    & git -C $RepositoryRoot add Source tools config RUN-v3.7.4.ps1 RUN-v3.7.4.cmd START-DASHBOARD-NOW-v3.7.4.ps1 START-DASHBOARD-NOW-v3.7.4.cmd
    $status = & git -C $RepositoryRoot status --porcelain

    if (-not [string]::IsNullOrWhiteSpace(($status -join ""))) {
        & git -C $RepositoryRoot commit -m "Release v3.7.4 recovery and real schema sync"
    }

    & git -C $RepositoryRoot push origin main

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Git push failed. The local v3.7.4 installation is still valid."
    }
}
catch {
    Write-Warning "Git save was skipped: $($_.Exception.Message)"
}

Write-Host "[10/10] v3.7.4 is running." -ForegroundColor Green
Write-Host ""
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Health: http://192.168.8.143:5088/health" -ForegroundColor Yellow
Write-Host "Backup: $backup" -ForegroundColor DarkGray
Write-Host "Accounting sync success: $syncSucceeded" -ForegroundColor Cyan
Write-Host ""
Write-Host "Final Issabel test:" -ForegroundColor Cyan
Write-Host "digiahan-test-ring 201 09121395663" -ForegroundColor White
Write-Host ""
Write-Host "The dashboard runs in the background. This window can now be closed." -ForegroundColor Green
