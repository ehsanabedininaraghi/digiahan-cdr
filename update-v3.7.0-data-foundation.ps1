$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$patch = Join-Path $repo "patch\Source"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $repo "_backups\v3.7.0-$stamp"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path (Join-Path $repo ".git"))) {
    throw "Repository root not found. Extract this package into D:\DigiAhan\CDR3.1.0git."
}
if (-not (Test-Path (Join-Path $source "Program.cs"))) {
    throw "Source\Program.cs not found."
}

$filesToBackup = @(
    "Program.cs",
    "DigiAhan.CDR.Receiver.csproj",
    "Models\CallIntelligenceModels.cs",
    "Services\CustomerIntelligenceRepository.cs",
    "Services\SalesDashboardRepository.cs",
    "Sql\DashboardCallsPage.sql",
    "Sql\DashboardCallsCount.sql",
    "Sql\DashboardSummary.sql",
    "Sql\DashboardDaily.sql",
    "wwwroot\dashboard\app.js",
    "wwwroot\dashboard\index.html"
)

function Restore-Backup {
    foreach ($relative in $filesToBackup) {
        $saved = Join-Path $backup $relative
        $target = Join-Path $source $relative
        if (Test-Path $saved) {
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item $saved $target -Force
        }
    }
}

Write-Host "[1/10] Stopping previous dashboard..." -ForegroundColor Cyan
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "[2/10] Creating backup..." -ForegroundColor Cyan
foreach ($relative in $filesToBackup) {
    $target = Join-Path $source $relative
    if (Test-Path $target) {
        $saved = Join-Path $backup $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
        Copy-Item $target $saved -Force
    }
}

Write-Host "[3/10] Installing customer identity and dashboard SQL..." -ForegroundColor Cyan
foreach ($relative in @(
    "Models\CallIntelligenceModels.cs",
    "Services\CustomerIntelligenceRepository.cs",
    "Services\SalesDashboardRepository.cs",
    "Sql\CustomerIdentitySchema.sql",
    "Sql\DashboardCallsPage.sql",
    "Sql\DashboardCallsCount.sql",
    "Sql\DashboardSummary.sql",
    "Sql\DashboardDaily.sql"
)) {
    $from = Join-Path $patch $relative
    $to = Join-Path $source $relative

    if (-not (Test-Path $from)) { throw "Patch file missing: $from" }

    New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
    Copy-Item $from $to -Force
}

Write-Host "[4/10] Fixing accounting status in main dashboard..." -ForegroundColor Cyan
$dashboardJsPath = Join-Path $source "wwwroot\dashboard\app.js"
$dashboardJs = [IO.File]::ReadAllText($dashboardJsPath)

$oldBlock = @'
        const ok = s.connected && s.lastSyncStatus === 'SUCCESS';
        $('accountingSyncDot').style.background = ok ? '#38d39f' : '#df5c61';
        $('accountingSyncText').textContent = ok ? 'حسابداری متصل' : 'حسابداری متصل نیست';
        $('accountingSyncTime').textContent = s.lastSyncAtUtc
            ? `آخرین همگام‌سازی: ${dt(s.lastSyncAtUtc)}`
            : 'همگام‌سازی موفق ثبت نشده';
'@

$newBlock = @'
        const hasAccountingData = Number(s.invoiceCount || 0) > 0;
        const latestSyncSucceeded = s.lastSyncStatus === 'SUCCESS';
        const latestFactorDate = data.salesInvoices && data.salesInvoices.length
            ? data.salesInvoices[0].factorDate
            : null;

        if (s.connected && latestSyncSucceeded) {
            $('accountingSyncDot').style.background = '#38d39f';
            $('accountingSyncText').textContent = 'حسابداری همگام است';
        } else if (hasAccountingData) {
            $('accountingSyncDot').style.background = '#f0b44d';
            $('accountingSyncText').textContent = latestSyncSucceeded
                ? 'داده حسابداری موجود است'
                : 'داده موجود است؛ آخرین تلاش همگام‌سازی ناموفق بوده';
        } else {
            $('accountingSyncDot').style.background = '#df5c61';
            $('accountingSyncText').textContent = 'داده حسابداری موجود نیست';
        }

        const successfulSyncText = s.lastSyncAtUtc
            ? `آخرین همگام‌سازی موفق: ${dt(s.lastSyncAtUtc)}`
            : 'همگام‌سازی موفق ثبت نشده';
        const factorText = latestFactorDate
            ? `آخرین فاکتور واردشده: ${latestFactorDate}`
            : 'فاکتور واردشده‌ای وجود ندارد';

        $('accountingSyncTime').textContent = `${successfulSyncText} | ${factorText}`;
'@

$dashboardJsNormalized = $dashboardJs.Replace("`r`n","`n")
$oldBlockNormalized = $oldBlock.Replace("`r`n","`n")
$newBlockNormalized = $newBlock.Replace("`r`n","`n")

if ($dashboardJsNormalized.Contains($oldBlockNormalized)) {
    $dashboardJs = $dashboardJsNormalized.Replace($oldBlockNormalized,$newBlockNormalized)
}
elseif ($dashboardJsNormalized.Contains("hasAccountingData")) {
    $dashboardJs = $dashboardJsNormalized
}
else {
    throw "Accounting status block was not found in dashboard app.js."
}

[IO.File]::WriteAllText($dashboardJsPath,$dashboardJs,$utf8)

$dashboardIndexPath = Join-Path $source "wwwroot\dashboard\index.html"
$dashboardIndex = [IO.File]::ReadAllText($dashboardIndexPath)
$dashboardIndex = $dashboardIndex.Replace('href="style.css"','href="style.css?v=370"')
$dashboardIndex = $dashboardIndex.Replace('src="app.js"','src="app.js?v=370"')
$dashboardIndex = $dashboardIndex.Replace('v3.4.1','v3.7.0')
$dashboardIndex = $dashboardIndex.Replace('شماره یکتای ثبت‌نشده در دیدار','شماره یکتای ثبت‌نشده در دیدار یا حسابداری')
$dashboardIndex = $dashboardIndex.Replace('شماره یکتای موجود در دیدار','شماره یکتای شناسایی‌شده')
$dashboardIndex = $dashboardIndex.Replace('فقط مخاطبان دیدار','فقط مشتریان شناسایی‌شده')
[IO.File]::WriteAllText($dashboardIndexPath,$dashboardIndex,$utf8)

Write-Host "[5/10] Updating application version..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)
$program = [regex]::Replace($program,'const string AppVersion = "[^"]+";','const string AppVersion = "3.7.0";')
$program = [regex]::Replace($program,'const string BuildDate = "[^"]+";','const string BuildDate = "2026-08-03";')
[IO.File]::WriteAllText($programPath,$program,$utf8)

$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.7.0</Version>')
$project = [regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.7.0.0</AssemblyVersion>')
$project = [regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.7.0.0</FileVersion>')
[IO.File]::WriteAllText($projectPath,$project,$utf8)

Write-Host "[6/10] Building and validating..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) {
        Restore-Backup
        throw "Build failed. Previous source files were restored."
    }
}
finally {
    Pop-Location
}

Write-Host "[7/10] Running full accounting bridge..." -ForegroundColor Cyan
$bridge = Join-Path $repo "tools\accounting-bridge-v3.7.ps1"
$bridgeSucceeded = $false

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridge `
        -RepositoryRoot $repo `
        -FullFiscalYear `
        -SkipIdentityRebuild

    $bridgeSucceeded = ($LASTEXITCODE -eq 0)
}
catch {
    Write-Warning "Initial accounting sync failed: $($_.Exception.Message)"
}

if ($bridgeSucceeded) {
    Write-Host "Initial accounting sync succeeded." -ForegroundColor Green
}
else {
    Write-Warning "Initial accounting sync did not finish. Existing accounting data will remain available and the scheduled task will retry."
}

Write-Host "[8/10] Rebuilding unified customer identities..." -ForegroundColor Cyan
$identity = Join-Path $repo "tools\rebuild-customer-identity.ps1"

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $identity `
    -RepositoryRoot $repo `
    -MappingsFile (Join-Path $repo "config\manual-customer-mappings.csv")

if ($LASTEXITCODE -ne 0) {
    throw "Customer identity rebuild failed."
}

Write-Host "[9/10] Installing automatic accounting sync..." -ForegroundColor Cyan
$taskInstaller = Join-Path $repo "tools\install-accounting-sync-task.ps1"

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskInstaller `
        -RepositoryRoot $repo `
        -EveryMinutes 15 `
        -Days 45

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Scheduled task installation returned an error."
    }
}
catch {
    Write-Warning "Scheduled task was not installed: $($_.Exception.Message)"
}

Write-Host "[10/10] Verifying, committing and starting..." -ForegroundColor Cyan
$verify = Join-Path $repo "tools\verify-v3.7.ps1"

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -RepositoryRoot $repo
}
catch {
    Write-Warning "Verification tool failed: $($_.Exception.Message)"
}

& git -C $repo add `
    Source/Program.cs `
    Source/DigiAhan.CDR.Receiver.csproj `
    Source/Models/CallIntelligenceModels.cs `
    Source/Services/CustomerIntelligenceRepository.cs `
    Source/Services/SalesDashboardRepository.cs `
    Source/Sql/CustomerIdentitySchema.sql `
    Source/Sql/DashboardCallsPage.sql `
    Source/Sql/DashboardCallsCount.sql `
    Source/Sql/DashboardSummary.sql `
    Source/Sql/DashboardDaily.sql `
    Source/wwwroot/dashboard/app.js `
    Source/wwwroot/dashboard/index.html `
    tools `
    config/manual-customer-mappings.csv

$changes = & git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
    & git -C $repo commit -m "Release v3.7.0 - accounting sync and unified customer identity"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "git commit failed. The local installation is still valid."
    }
}

& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) {
    Write-Warning "git push failed. The local installation is still valid."
}

Write-Host ""
Write-Host "DigiAhan v3.7.0 is ready." -ForegroundColor Green
Write-Host "Main dashboard: http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
Write-Host "Agent 201:     http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Backup:        $backup" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Keep this PowerShell window open." -ForegroundColor Cyan

Set-Location $source
& dotnet run --no-build --no-restore
