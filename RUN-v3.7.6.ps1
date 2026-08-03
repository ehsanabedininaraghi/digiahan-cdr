param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = Join-Path $RepositoryRoot "Source"
$services = Join-Path $source "Services"
$tools = Join-Path $RepositoryRoot "tools"
$logs = Join-Path $RepositoryRoot "Logs"
$payload = Join-Path $PSScriptRoot "payload"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $RepositoryRoot "_backups\v3.7.6-$stamp"
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
    $stdout = Join-Path $logs "v3.7.6-app-output.log"
    $stderr = Join-Path $logs "v3.7.6-app-error.log"
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
        Get-Content $stdout -Tail 60
    }

    if (Test-Path $stderr) {
        Write-Host "Application error:" -ForegroundColor Yellow
        Get-Content $stderr -Tail 60
    }

    throw "Dashboard did not become healthy on port 5088."
}

if (-not (Test-Path (Join-Path $source "Program.cs"))) {
    throw "Project not found at $source."
}

$required = @(
    (Join-Path $payload "Source\Program.cs"),
    (Join-Path $payload "Source\Services\AgentPanelRepository.cs"),
    (Join-Path $payload "tools\test-all-connections-v3.7.6.ps1")
)

foreach ($item in $required) {
    if (-not (Test-Path $item)) {
        throw "v3.7.6 payload is incomplete: $item"
    }
}

Write-Host ""
Write-Host "DigiAhan CDR v3.7.6 - VoIP Final Fix" -ForegroundColor Cyan
Write-Host "This patch does not re-import accounting. It fixes the single remaining VoIP 500 error." -ForegroundColor DarkGray
Write-Host ""

Write-Host "[1/6] Stopping dashboard..." -ForegroundColor Cyan
Stop-Dashboard

Write-Host "[2/6] Backing up v3.7.5 files..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backup | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null
New-Item -ItemType Directory -Force -Path $logs | Out-Null

foreach ($path in @(
    (Join-Path $source "Program.cs"),
    (Join-Path $services "AgentPanelRepository.cs"),
    (Join-Path $source "DigiAhan.CDR.Receiver.csproj")
)) {
    if (Test-Path $path) {
        Copy-Item $path (Join-Path $backup ([IO.Path]::GetFileName($path))) -Force
    }
}

Write-Host "[3/6] Installing concurrency-safe Agent Panel and VoIP endpoint..." -ForegroundColor Cyan
Copy-Item (Join-Path $payload "Source\Program.cs") `
    (Join-Path $source "Program.cs") -Force
Copy-Item (Join-Path $payload "Source\Services\AgentPanelRepository.cs") `
    (Join-Path $services "AgentPanelRepository.cs") -Force
Copy-Item (Join-Path $payload "tools\test-all-connections-v3.7.6.ps1") `
    (Join-Path $tools "test-all-connections-v3.7.6.ps1") -Force

$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.7.6</Version>')
$project = [regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.7.6.0</AssemblyVersion>')
$project = [regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.7.6.0</FileVersion>')
[IO.File]::WriteAllText($projectPath,$project,$utf8)

Write-Host "[4/6] Building v3.7.6..." -ForegroundColor Cyan
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

Write-Host "[5/6] Starting dashboard..." -ForegroundColor Cyan
$appProcess = Start-Dashboard
Write-Host "Dashboard is healthy. PID=$($appProcess.Id)" -ForegroundColor Green

Write-Host "[6/6] Testing the full chain..." -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $tools "test-all-connections-v3.7.6.ps1") `
    -RepositoryRoot $RepositoryRoot `
    -ServerUrl "http://localhost:5088"

$testExitCode = $LASTEXITCODE

try {
    & git -C $RepositoryRoot add `
        Source/Program.cs `
        Source/Services/AgentPanelRepository.cs `
        Source/DigiAhan.CDR.Receiver.csproj `
        tools/test-all-connections-v3.7.6.ps1 `
        RUN-v3.7.6.ps1 `
        RUN-v3.7.6.cmd

    $status = & git -C $RepositoryRoot status --porcelain `
        Source/Program.cs `
        Source/Services/AgentPanelRepository.cs `
        Source/DigiAhan.CDR.Receiver.csproj `
        tools/test-all-connections-v3.7.6.ps1 `
        RUN-v3.7.6.ps1 `
        RUN-v3.7.6.cmd

    if (-not [string]::IsNullOrWhiteSpace(($status -join ""))) {
        & git -C $RepositoryRoot commit -m "Hotfix v3.7.6 - concurrency safe VoIP agent panel"
    }

    & git -C $RepositoryRoot push origin main
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Git push failed. The local v3.7.6 installation is still valid."
    }
}
catch {
    Write-Warning "Git save skipped: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "v3.7.6 completed." -ForegroundColor Green
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Health: http://192.168.8.143:5088/health" -ForegroundColor Yellow
Write-Host "Backup: $backup" -ForegroundColor DarkGray

if ($testExitCode -eq 0) {
    Write-Host "Diagnostics: ALL PASS" -ForegroundColor Green
}
else {
    Write-Warning "Diagnostics still reported a failure. Check Logs\MVP-v3.7.6-*.txt."
}

Write-Host ""
Write-Host "Final real test on Issabel:" -ForegroundColor Cyan
Write-Host "digiahan-test-ring 201 09121395663" -ForegroundColor White
