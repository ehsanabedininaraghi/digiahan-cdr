param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [int]$AccountingDays = 60
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = Join-Path $RepositoryRoot "Source"
$tools = Join-Path $RepositoryRoot "tools"
$logs = Join-Path $RepositoryRoot "Logs"
$payload = Join-Path $PSScriptRoot "payload"
$payloadTools = Join-Path $payload "tools"
$payloadModels = Join-Path $payload "Source\Models"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $RepositoryRoot "_backups\v3.7.5-$stamp"
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Stop-Dashboard {
    try {
        Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object {
                if ($_.OwningProcess -gt 0) {
                    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
                }
            }
    }
    catch {}

    Start-Sleep -Seconds 2
}

function Start-Dashboard {
    $stdout = Join-Path $logs "v3.7.5-app-output.log"
    $stderr = Join-Path $logs "v3.7.5-app-error.log"
    Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue

    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("run","--no-build","--no-restore") `
        -WorkingDirectory $source `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru

    for ($i=0; $i -lt 35; $i++) {
        Start-Sleep -Seconds 1

        if ($process.HasExited) {
            break
        }

        try {
            $health = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3
            if ($health.status -eq "healthy") {
                return $process
            }
        }
        catch {}
    }

    if (Test-Path $stdout) {
        Write-Host "Application output:" -ForegroundColor Yellow
        Get-Content $stdout -Tail 50
    }

    if (Test-Path $stderr) {
        Write-Host "Application error:" -ForegroundColor Yellow
        Get-Content $stderr -Tail 50
    }

    throw "Dashboard did not become healthy on port 5088."
}

if (-not (Test-Path (Join-Path $source "Program.cs"))) {
    throw "Project not found at $source."
}

foreach ($required in @(
    (Join-Path $payloadTools "accounting-bridge-v3.7.5.ps1"),
    (Join-Path $payloadTools "rebuild-customer-identity-v3.7.5.ps1"),
    (Join-Path $payloadTools "rebuild-customer-identity.ps1"),
    (Join-Path $payloadTools "test-all-connections-v3.7.5.ps1"),
    (Join-Path $payloadTools "install-accounting-task-v3.7.5.ps1"),
    (Join-Path $payloadModels "AccountingModels.cs")
)) {
    if (-not (Test-Path $required)) {
        throw "Hotfix payload is incomplete: $required"
    }
}

Write-Host ""
Write-Host "DigiAhan CDR v3.7.5 Hotfix" -ForegroundColor Cyan
Write-Host "Fixes SqlBulkCopy staging, identity rebuild, and accounting-status diagnostics." -ForegroundColor DarkGray
Write-Host ""

Write-Host "[1/8] Stopping dashboard and the old v3.7.4 accounting task..." -ForegroundColor Cyan
Stop-Dashboard

foreach ($taskName in @(
    "DigiAhan Accounting Bridge v3.7.4",
    "DigiAhan Accounting Bridge v3.7.2"
)) {
    try {
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
            Disable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Warning "Could not remove old task $taskName : $($_.Exception.Message)"
    }
}

Write-Host "[2/8] Backing up the current v3.7.4 files..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backup | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null
New-Item -ItemType Directory -Force -Path $logs | Out-Null

foreach ($path in @(
    (Join-Path $tools "accounting-bridge-v3.7.4.ps1"),
    (Join-Path $tools "rebuild-customer-identity.ps1"),
    (Join-Path $tools "rebuild-customer-identity-v3.7.4.ps1"),
    (Join-Path $tools "test-all-connections-v3.7.4.ps1"),
    (Join-Path $source "Models\AccountingModels.cs"),
    (Join-Path $source "Program.cs"),
    (Join-Path $source "DigiAhan.CDR.Receiver.csproj")
)) {
    if (Test-Path $path) {
        Copy-Item $path (Join-Path $backup ([IO.Path]::GetFileName($path))) -Force
    }
}

Write-Host "[3/8] Installing the fixed v3.7.5 files..." -ForegroundColor Cyan
Copy-Item (Join-Path $payloadTools "*") $tools -Recurse -Force
Copy-Item (Join-Path $payloadModels "AccountingModels.cs") `
    (Join-Path $source "Models\AccountingModels.cs") -Force

$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)
$program = [regex]::Replace(
    $program,
    'const string AppVersion = "[^"]+";',
    'const string AppVersion = "3.7.5";'
)
$program = [regex]::Replace(
    $program,
    'const string BuildDate = "[^"]+";',
    'const string BuildDate = "2026-08-03";'
)
[IO.File]::WriteAllText($programPath,$program,$utf8)

$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.7.5</Version>')
$project = [regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.7.5.0</AssemblyVersion>')
$project = [regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.7.5.0</FileVersion>')
[IO.File]::WriteAllText($projectPath,$project,$utf8)

Write-Host "[4/8] Building v3.7.5..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "No-restore build failed. Retrying normal build."
        & dotnet build
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}
finally {
    Pop-Location
}

$syncSucceeded = $false
$appProcess = $null

try {
    Write-Host "[5/8] Synchronizing accounting and rebuilding identities..." -ForegroundColor Cyan
    $bridge = Join-Path $tools "accounting-bridge-v3.7.5.ps1"

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridge `
        -RepositoryRoot $RepositoryRoot `
        -Days $AccountingDays

    $syncSucceeded = ($LASTEXITCODE -eq 0)

    if (-not $syncSucceeded) {
        throw "Accounting synchronization returned an error."
    }
}
catch {
    Write-Warning "Accounting sync failed. The dashboard will still be restarted: $($_.Exception.Message)"
}
finally {
    Write-Host "[6/8] Starting dashboard..." -ForegroundColor Cyan
    $appProcess = Start-Dashboard
    Write-Host "Dashboard is healthy. PID=$($appProcess.Id)" -ForegroundColor Green
}

Write-Host "[7/8] Installing the corrected 15-minute accounting task..." -ForegroundColor Cyan
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $tools "install-accounting-task-v3.7.5.ps1") `
        -RepositoryRoot $RepositoryRoot `
        -EveryMinutes 15 `
        -Days $AccountingDays

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "The v3.7.5 scheduled task was not installed."
    }
}
catch {
    Write-Warning "Scheduled task installation failed: $($_.Exception.Message)"
}

Write-Host "[8/8] Running end-to-end diagnostics..." -ForegroundColor Cyan
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $tools "test-all-connections-v3.7.5.ps1") `
        -RepositoryRoot $RepositoryRoot `
        -ServerUrl "http://localhost:5088"

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Diagnostics reported failures. The dashboard remains running."
    }
}
catch {
    Write-Warning "Diagnostics failed: $($_.Exception.Message)"
}

try {
    & git -C $RepositoryRoot add `
        Source/Models/AccountingModels.cs `
        Source/Program.cs `
        Source/DigiAhan.CDR.Receiver.csproj `
        tools/accounting-bridge-v3.7.5.ps1 `
        tools/rebuild-customer-identity.ps1 `
        tools/rebuild-customer-identity-v3.7.5.ps1 `
        tools/test-all-connections-v3.7.5.ps1 `
        tools/install-accounting-task-v3.7.5.ps1 `
        RUN-v3.7.5.ps1 `
        RUN-v3.7.5.cmd `
        START-DASHBOARD-NOW-v3.7.5.ps1 `
        START-DASHBOARD-NOW-v3.7.5.cmd `
        ROLLBACK-v3.7.5.ps1 `
        ROLLBACK-v3.7.5.cmd

    $status = & git -C $RepositoryRoot status --porcelain
    if (-not [string]::IsNullOrWhiteSpace(($status -join ""))) {
        & git -C $RepositoryRoot commit -m "Hotfix v3.7.5 - stable accounting staging and identity sync"
    }

    & git -C $RepositoryRoot push origin main
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Git push failed; the local installation remains valid."
    }
}
catch {
    Write-Warning "Git save skipped: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "v3.7.5 completed." -ForegroundColor Green
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Health: http://192.168.8.143:5088/health" -ForegroundColor Yellow
Write-Host "Accounting sync success: $syncSucceeded" -ForegroundColor Cyan
Write-Host "Backup: $backup" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Real Issabel test:" -ForegroundColor Cyan
Write-Host "digiahan-test-ring 201 09121395663" -ForegroundColor White
