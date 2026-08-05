param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = Join-Path $RepositoryRoot "Source"
$services = Join-Path $source "Services"
$tools = Join-Path $RepositoryRoot "tools"
$logs = Join-Path $RepositoryRoot "Logs"
$runs = Join-Path $logs "Runs"
$payload = Join-Path $PSScriptRoot "payload"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path $runs "v3.7.7-$stamp"
$backup = Join-Path $RepositoryRoot "_backups\v3.7.7-$stamp"
$transcript = Join-Path $runDirectory "installer-transcript.txt"
$summary = Join-Path $runDirectory "installer-summary.txt"
$utf8 = New-Object System.Text.UTF8Encoding($false)

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $backup | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null
New-Item -ItemType Directory -Force -Path $logs | Out-Null

Start-Transcript -Path $transcript -Force | Out-Null

$phase = "START"
$buildOk = $false
$appHealthy = $false
$testsOk = $false
$appProcess = $null

function Write-Phase {
    param([string]$Name)
    $script:phase = $Name
    Write-Host ""
    Write-Host "[$Name]" -ForegroundColor Cyan
}

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
    $stdout = Join-Path $runDirectory "application-stdout.log"
    $stderr = Join-Path $runDirectory "application-stderr.log"

    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("run","--no-build","--no-restore") `
        -WorkingDirectory $source `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru

    for ($i=0; $i -lt 40; $i++) {
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

    if (Test-Path $stdout) { Get-Content $stdout -Tail 80 }
    if (Test-Path $stderr) { Get-Content $stderr -Tail 80 }

    throw "Dashboard did not become healthy on port 5088."
}

try {
    Write-Host ""
    Write-Host "DigiAhan CDR v3.7.7 - Resilient VoIP + Complete Run Logs" -ForegroundColor Cyan
    Write-Host "This version does not re-import accounting." -ForegroundColor DarkGray

    if (-not (Test-Path (Join-Path $source "Program.cs"))) {
        throw "Project not found at $source."
    }

    Write-Phase "1/7 STOP"
    Stop-Dashboard

    Write-Phase "2/7 BACKUP"
    foreach ($path in @(
        (Join-Path $source "Program.cs"),
        (Join-Path $services "CustomerIntelligenceRepository.cs"),
        (Join-Path $services "AgentPanelRepository.cs"),
        (Join-Path $source "DigiAhan.CDR.Receiver.csproj")
    )) {
        if (Test-Path $path) {
            Copy-Item $path (Join-Path $backup ([IO.Path]::GetFileName($path))) -Force
        }
    }

    Write-Phase "3/7 INSTALL"
    Copy-Item (Join-Path $payload "Source\Program.cs") `
        (Join-Path $source "Program.cs") -Force
    Copy-Item (Join-Path $payload "Source\Services\CustomerIntelligenceRepository.cs") `
        (Join-Path $services "CustomerIntelligenceRepository.cs") -Force
    Copy-Item (Join-Path $payload "Source\Services\AgentPanelRepository.cs") `
        (Join-Path $services "AgentPanelRepository.cs") -Force
    Copy-Item (Join-Path $payload "tools\*") $tools -Recurse -Force

    $projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
    $project = [IO.File]::ReadAllText($projectPath)
    $project = [regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.7.7</Version>')
    $project = [regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.7.7.0</AssemblyVersion>')
    $project = [regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.7.7.0</FileVersion>')
    [IO.File]::WriteAllText($projectPath,$project,$utf8)

    Write-Phase "4/7 BUILD"
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
        $buildOk = $true
    }
    finally {
        Pop-Location
    }

    Write-Phase "5/7 START"
    $appProcess = Start-Dashboard
    $appHealthy = $true
    Write-Host "Dashboard healthy. PID=$($appProcess.Id)" -ForegroundColor Green

    Write-Phase "6/7 TEST"
    $testLog = Join-Path $runDirectory "diagnostics-console.txt"

    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $tools "test-all-connections-v3.7.7.ps1") `
        -RepositoryRoot $RepositoryRoot `
        -ServerUrl "http://localhost:5088" `
        -Strict 2>&1 |
        Tee-Object -FilePath $testLog

    $testsOk = ($LASTEXITCODE -eq 0)

    if (-not $testsOk) {
        Write-Warning "One or more diagnostics failed."
    }

    Write-Phase "7/7 BUNDLE"
}
catch {
    Write-Host ""
    Write-Host "INSTALL ERROR" -ForegroundColor Red
    Write-Host $_.Exception.ToString() -ForegroundColor Red

    $_ | Format-List * -Force |
        Out-String |
        Set-Content (Join-Path $runDirectory "fatal-error.txt") -Encoding UTF8
}
finally {
    $lines = @()
    $lines += "DigiAhan CDR v3.7.7 Installer Summary"
    $lines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
    $lines += "LastPhase: $phase"
    $lines += "BuildOk: $buildOk"
    $lines += "AppHealthy: $appHealthy"
    $lines += "TestsOk: $testsOk"
    $lines += "AppPid: $(if ($appProcess) { $appProcess.Id } else { 'none' })"
    $lines += "Backup: $backup"
    $lines += "RunDirectory: $runDirectory"
    $lines | Set-Content $summary -Encoding UTF8

    try { Stop-Transcript | Out-Null } catch {}

    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $tools "create-diagnostic-bundle-v3.7.7.ps1") `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $runDirectory
    }
    catch {
        Write-Warning "Diagnostic ZIP creation failed: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Health: http://192.168.8.143:5088/health" -ForegroundColor Yellow
Write-Host ""
Write-Host "Diagnostic file to send:" -ForegroundColor Cyan
Write-Host "$runDirectory.zip" -ForegroundColor White

if ($buildOk -and $appHealthy -and $testsOk) {
    exit 0
}

exit 1
