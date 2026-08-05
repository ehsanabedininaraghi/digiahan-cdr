$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$fixedTools = Join-Path $repo "fixed-tools"
$tools = Join-Path $repo "tools"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $repo "_backups\v3.7.1-hotfix-$stamp"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path (Join-Path $source "Program.cs"))) {
    throw "Source\Program.cs not found. Extract the hotfix inside D:\DigiAhan\CDR3.1.0git."
}

foreach ($required in @(
    "accounting-bridge-v3.7.ps1",
    "rebuild-customer-identity.ps1"
)) {
    if (-not (Test-Path (Join-Path $fixedTools $required))) {
        throw "Hotfix file missing: $required"
    }
}

Write-Host "[1/8] Stopping old dashboard..." -ForegroundColor Cyan
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "[2/8] Backing up current scripts..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backup | Out-Null
New-Item -ItemType Directory -Force -Path $tools | Out-Null

foreach ($file in @(
    "accounting-bridge-v3.7.ps1",
    "rebuild-customer-identity.ps1"
)) {
    $current = Join-Path $tools $file
    if (Test-Path $current) {
        Copy-Item $current (Join-Path $backup $file) -Force
    }

    Copy-Item (Join-Path $fixedTools $file) $current -Force
}

Write-Host "[3/8] Updating hotfix version and building..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)
$program = [regex]::Replace(
    $program,
    'const string AppVersion = "[^"]+";',
    'const string AppVersion = "3.7.1";'
)
$program = [regex]::Replace(
    $program,
    'const string BuildDate = "[^"]+";',
    'const string BuildDate = "2026-08-03";'
)
[IO.File]::WriteAllText($programPath,$program,$utf8)

$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.7.1</Version>')
$project = [regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.7.1.0</AssemblyVersion>')
$project = [regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.7.1.0</FileVersion>')
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

Write-Host "[4/8] Running full accounting synchronization..." -ForegroundColor Cyan
$bridge = Join-Path $tools "accounting-bridge-v3.7.ps1"

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridge `
    -RepositoryRoot $repo `
    -FullFiscalYear `
    -SkipIdentityRebuild

if ($LASTEXITCODE -ne 0) {
    throw "Accounting synchronization failed. The dashboard was not started so the error remains visible."
}

Write-Host "[5/8] Rebuilding unified customer identities..." -ForegroundColor Cyan
$identity = Join-Path $tools "rebuild-customer-identity.ps1"
$mappings = Join-Path $repo "config\manual-customer-mappings.csv"

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $identity `
    -RepositoryRoot $repo `
    -MappingsFile $mappings

if ($LASTEXITCODE -ne 0) {
    throw "Customer identity rebuild failed."
}

Write-Host "[6/8] Installing the 15-minute accounting task..." -ForegroundColor Cyan
$taskInstaller = Join-Path $tools "install-accounting-sync-task.ps1"

if (Test-Path $taskInstaller) {
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskInstaller `
            -RepositoryRoot $repo `
            -EveryMinutes 15 `
            -Days 45

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Scheduled task installation returned an error. Manual synchronization still works."
        }
    }
    catch {
        Write-Warning "Scheduled task was not installed: $($_.Exception.Message)"
    }
}

Write-Host "[7/8] Verifying sample mappings and latest invoice..." -ForegroundColor Cyan
$verify = Join-Path $tools "verify-v3.7.ps1"

if (Test-Path $verify) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify `
        -RepositoryRoot $repo

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Verification script returned an error."
    }
}

Write-Host "[8/8] Saving changes and starting dashboard..." -ForegroundColor Green
& git -C $repo add `
    Source/Program.cs `
    Source/DigiAhan.CDR.Receiver.csproj `
    tools/accounting-bridge-v3.7.ps1 `
    tools/rebuild-customer-identity.ps1

$changes = & git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
    & git -C $repo commit -m "Hotfix v3.7.1 - legacy accounting connection and Didar phone schema"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "git commit failed. The local installation is still valid."
    }
}

& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) {
    Write-Warning "git push failed. The local installation is still valid."
}

Write-Host ""
Write-Host "Hotfix v3.7.1 completed." -ForegroundColor Green
Write-Host "Dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Keep this window open." -ForegroundColor Cyan

Set-Location $source
& dotnet run --no-build --no-restore
