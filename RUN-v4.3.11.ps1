param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [string]$ReleaseVersion = "4.3.11",
    [switch]$ResetDashboardPassword
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$version = $ReleaseVersion
$payloadRoot = Join-Path $PSScriptRoot "payload"
$sourceRoot = Join-Path $RepositoryRoot "Source"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $RepositoryRoot "Logs\Runs\v$version-$stamp"
$backupRoot = Join-Path $RepositoryRoot "_backups\v$version-$stamp"

$relativeFiles = @(
    "Source\Program.cs",
    "Source\DigiAhan.CDR.Receiver.csproj",
    "Source\appsettings.example.json",
    "Source\appsettings.DataGathering.example.json",
    "Source\Models\DashboardModels.cs",
    "Source\Models\AiAnalysisApiModels.cs",
    "Source\Models\AiAnalysisModels.cs",
    "Source\Models\AiPipelineModels.cs",
    "Source\Models\RecordingSyncModels.cs",
    "Source\Models\InvoiceNotificationModels.cs",
    "Source\Models\CallIntelligenceModels.cs",
    "Source\Models\CustomerMappingModels.cs",
    "Source\Models\SellerWorkspaceModels.cs",
    "Source\Models\SystemOperationsModels.cs",
    "Source\Services\AccountingSyncService.cs",
    "Source\Services\AiAnalysisRepository.cs",
    "Source\Services\AiCallDiscoveryWorker.cs",
    "Source\Services\AiPipelineRepository.cs",
    "Source\Services\AiTranscriptAnalyzer.cs",
    "Source\Services\DailyRecordingIngestionWorker.cs",
    "Source\Services\DashboardRepository.cs",
    "Source\Services\CustomerIntelligenceRepository.cs",
    "Source\Services\CustomerIdentityReconcileService.cs",
    "Source\Services\DataGatheringCoordinator.cs",
    "Source\Services\DeliveryVoucherParser.cs",
    "Source\Services\IntegrationSchedulerService.cs",
    "Source\Services\InvoiceNotificationRepository.cs",
    "Source\Services\FasterWhisperTranscriber.cs",
    "Source\Services\IssabelRecordingPathResolver.cs",
    "Source\Services\IssabelSftpRecordingClient.cs",
    "Source\Services\LegacyAccountingBridgeRunner.cs",
    "Source\Services\AgentEventStore.cs",
    "Source\Services\PublicTokenService.cs",
    "Source\Services\RecordingAssetRepository.cs",
    "Source\Services\RecordingAudioValidator.cs",
    "Source\Services\SellerWorkspaceAccessService.cs",
    "Source\Services\SellerWorkspaceRepository.cs",
    "Source\Services\SystemHealthService.cs",
    "Source\Services\TehranClock.cs",
    "Source\Sql\AccountingSchema.sql",
    "Source\Sql\DashboardExtensions.sql",
    "Source\Sql\DashboardCallsCount.sql",
    "Source\Sql\DashboardCallsPage.sql",
    "Source\Sql\DashboardDaily.sql",
    "Source\Sql\DashboardHourly.sql",
    "Source\Sql\DashboardSummary.sql",
    "Source\Sql\CustomerIdentityDidarReconcileV412.sql",
    "Source\Sql\CustomerIdentitySchema.sql",
    "Source\Sql\SellerWorkspaceV1.sql",
    "Source\Sql\SystemOperationsV42.sql",
    "Source\Sql\InvoiceNotificationsV43.sql",
    "Source\Sql\AiAnalysisVNext.sql",
    "Source\Sql\AiPipelineVNext.sql",
    "Source\Sql\AiRecordingSyncVNext.sql",
    "Source\wwwroot\dashboard\app.js",
    "Source\wwwroot\dashboard\index.html",
    "Source\wwwroot\dashboard\style.css",
    "Source\wwwroot\dashboard-login\app.js",
    "Source\wwwroot\dashboard-login\index.html",
    "Source\wwwroot\dashboard-login\style.css",
    "Source\wwwroot\agent\app.js",
    "Source\wwwroot\agent\index.html",
    "Source\wwwroot\ai\app.js",
    "Source\wwwroot\ai\index.html",
    "Source\wwwroot\ai\sample-data.json",
    "Source\wwwroot\ai\style.css",
    "Source\wwwroot\invoice-notifications\app.js",
    "Source\wwwroot\invoice-notifications\index.html",
    "Source\wwwroot\invoice-notifications\style.css",
    "Source\wwwroot\sms-dashboard\app.js",
    "Source\wwwroot\sms-dashboard\index.html",
    "Source\wwwroot\sms-dashboard\style.css",
    "Source\wwwroot\order\app.js",
    "Source\wwwroot\order\index.html",
    "Source\wwwroot\order\style.css",
    "Source\wwwroot\seller-v2\app.js",
    "Source\wwwroot\seller-v2\balance.css",
    "Source\wwwroot\seller-v2\index.html",
    "Source\wwwroot\seller-v2\style.css",
    "Source\wwwroot\seller-admin\app.js",
    "Source\wwwroot\seller-admin\index.html",
    "Source\wwwroot\seller-admin\style.css",
    "Source\wwwroot\version.js",
    "tools\accounting-bridge-v4.3.10.ps1",
    "tools\ai\transcribe_sample.py"
)

if (-not (Test-Path -LiteralPath $sourceRoot)) { throw "Repository source not found: $sourceRoot" }
if (-not (Test-Path -LiteralPath $payloadRoot)) { throw "Release payload not found: $payloadRoot" }

New-Item -ItemType Directory -Force -Path $runDir,$backupRoot | Out-Null
Start-Transcript -Path (Join-Path $runDir "installer-transcript.txt") -Force | Out-Null

$phase = "START"
try {
    $phase = "LEGACY_TASKS"
    Write-Host "[1/8] Disabling legacy recurring PowerShell tasks..." -ForegroundColor Cyan
    $legacyTasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
        $text = ($_.Actions | ForEach-Object {
            $executeProperty = $_.PSObject.Properties["Execute"]
            $argumentsProperty = $_.PSObject.Properties["Arguments"]
            $execute = if ($null -ne $executeProperty) { [string]$executeProperty.Value } else { "" }
            $arguments = if ($null -ne $argumentsProperty) { [string]$argumentsProperty.Value } else { "" }
            "$execute $arguments"
        }) -join " "
        $text -match '(?i)accounting-bridge-v3\.7\.[0-9]+\.ps1|RUN-v3\.7\.[0-9]+|DigiAhan.*Accounting'
    }
    foreach ($task in $legacyTasks) {
        Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue | Out-Null
        Write-Host "Disabled legacy task: $($task.TaskPath)$($task.TaskName)" -ForegroundColor Yellow
    }

    $phase = "STOP"
    Write-Host "[2/8] Stopping the current dashboard process and orphan workers..." -ForegroundColor Cyan
    $processIds = @(
        Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -gt 0 } |
            Select-Object -ExpandProperty OwningProcess
        Get-Process -Name "DigiAhan.CDR.Receiver" -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Id
    ) | Sort-Object -Unique
    foreach ($processId in $processIds) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    $remainingAppProcesses = @(Get-Process -Name "DigiAhan.CDR.Receiver" -ErrorAction SilentlyContinue)
    if ($remainingAppProcesses.Count -gt 0) {
        throw "The existing DigiAhan.CDR.Receiver process could not be stopped."
    }

    $phase = "BACKUP"
    Write-Host "[3/8] Backing up files that will change..." -ForegroundColor Cyan
    foreach ($relative in $relativeFiles) {
        $target = Join-Path $RepositoryRoot $relative
        if (Test-Path -LiteralPath $target) {
            $backup = Join-Path $backupRoot $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null
            Copy-Item -LiteralPath $target -Destination $backup -Force
        }
    }

    $phase = "INSTALL"
    Write-Host "[4/8] Installing v$version files..." -ForegroundColor Cyan
    foreach ($relative in $relativeFiles) {
        $source = Join-Path $payloadRoot $relative
        if (-not (Test-Path -LiteralPath $source)) { throw "Payload file missing: $source" }
        $target = Join-Path $RepositoryRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }

    $dashboardConfigPath = Join-Path $sourceRoot "appsettings.Dashboard.local.json"
    $passwordHash = $null
    if ((Test-Path -LiteralPath $dashboardConfigPath) -and -not $ResetDashboardPassword) {
        $dashboardConfig = Get-Content -LiteralPath $dashboardConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $passwordHash = [string]$dashboardConfig.DashboardAccess.PasswordHash
        if ($passwordHash -notmatch '^[A-Fa-f0-9]{64}$') { $passwordHash = $null }
    }
    if ([string]::IsNullOrWhiteSpace($passwordHash)) {
        Write-Host "Set the private management-dashboard password (minimum 8 characters)." -ForegroundColor Yellow
        $securePassword = Read-Host "Dashboard password" -AsSecureString
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
        try { $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
        if ([string]::IsNullOrWhiteSpace($plainPassword) -or $plainPassword.Length -lt 8) {
            throw "Dashboard password must contain at least 8 characters."
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $passwordBytes = [Text.Encoding]::UTF8.GetBytes($plainPassword)
            $passwordHash = -join ($sha.ComputeHash($passwordBytes) | ForEach-Object { $_.ToString("X2") })
        }
        finally { $sha.Dispose(); $plainPassword = $null }
        @{ DashboardAccess = @{ PasswordHash = $passwordHash } } |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $dashboardConfigPath -Encoding UTF8
        Write-Host "Dashboard password created; only its hash was saved." -ForegroundColor Green
    }
    else {
        Write-Host "Existing dashboard password was preserved." -ForegroundColor Green
    }

    $mappingPath = Join-Path $sourceRoot "config\mappingfile.xlsx"
    if (-not (Test-Path -LiteralPath $mappingPath)) {
        Write-Warning "Mapping file is missing: $mappingPath. The daily mapping job will report FAILED until the file is copied there."
    }

    $localConfigPath = Join-Path $sourceRoot "appsettings.DataGathering.local.json"
    if (Test-Path -LiteralPath $localConfigPath) {
        $dataConfig = Get-Content -LiteralPath $localConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    else {
        $dataConfig = [pscustomobject]@{
            DataGathering = [pscustomobject]@{}
            DatabaseMaintenance = [pscustomobject]@{}
        }
    }
    if ($null -eq $dataConfig.DataGathering) {
        $dataConfig | Add-Member -MemberType NoteProperty -Name DataGathering -Value ([pscustomobject]@{}) -Force
    }
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name Enabled -Value $true -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name RunOnStartup -Value $false -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name IntervalMinutes -Value 10 -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name IncrementalAccountingDays -Value 2 -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name AccountingDays -Value 2 -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name UnmappedAlertHours -Value 24 -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name MappingFile -Value "config/mappingfile.xlsx" -Force
    $dataConfig.DataGathering | Add-Member -MemberType NoteProperty -Name AccountingBridgeScript -Value "tools/accounting-bridge-v4.3.10.ps1" -Force
    $dataConfig | Add-Member -MemberType NoteProperty -Name InvoiceNotifications -Value ([pscustomobject]@{
        PublicOrderBaseUrl = "https://www.digiahan.com/order"
        TokenExpiryDays = 7
    }) -Force
    $dataConfig | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $localConfigPath -Encoding UTF8

    $phase = "BUILD"
    Write-Host "[5/8] Building v$version..." -ForegroundColor Cyan
    Push-Location $sourceRoot
    try {
        $assetsFile = Join-Path $sourceRoot "obj\project.assets.json"
        if (Test-Path -LiteralPath $assetsFile) {
            Write-Host "Using the existing restored packages (offline fast build)." -ForegroundColor DarkCyan
            dotnet build --configuration Release --no-restore -p:NuGetAudit=false
        }
        else {
            Write-Host "Package assets are missing; restoring once with NuGet audit disabled." -ForegroundColor Yellow
            dotnet restore --ignore-failed-sources -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "Package restore failed." }
            dotnet build --configuration Release --no-restore -p:NuGetAudit=false
        }
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    }
    finally { Pop-Location }

    $phase = "START"
    Write-Host "[6/8] Starting the dashboard..." -ForegroundColor Cyan
    $stdout = Join-Path $runDir "application-stdout.log"
    $stderr = Join-Path $runDir "application-stderr.log"
    Start-Process dotnet -ArgumentList @("run","--no-build","--configuration","Release") `
        -WorkingDirectory $sourceRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
        -WindowStyle Hidden | Out-Null

    $healthy = $false
    foreach ($attempt in 1..60) {
        Start-Sleep -Seconds 1
        try {
            $health = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3
            if ($health.status -eq "healthy") { $healthy = $true; break }
        }
        catch { }
    }
    if (-not $healthy) { throw "Dashboard did not become healthy." }

    $phase = "SCHEMA"
    Write-Host "[7/8] Creating notification schema, schedules and health indexes..." -ForegroundColor Cyan
    $systemHealth = Invoke-RestMethod "http://localhost:5088/api/system/health" -TimeoutSec 300
    $schedules = Invoke-RestMethod "http://localhost:5088/api/system/schedules" -TimeoutSec 30
    $notifications = Invoke-RestMethod "http://localhost:5088/api/invoice-notifications?take=1" -TimeoutSec 60
    $sessionSha = [Security.Cryptography.SHA256]::Create()
    try {
        $sessionBytes = [Text.Encoding]::UTF8.GetBytes("DigiAhan-Dashboard-Session|$passwordHash")
        $dashboardCookie = -join ($sessionSha.ComputeHash($sessionBytes) | ForEach-Object { $_.ToString("X2") })
    }
    finally { $sessionSha.Dispose() }
    $adminSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $adminSession.Cookies.Add((New-Object Net.Cookie("digiahan_dashboard_auth",$dashboardCookie,"/","localhost")))
    $sellerAdminPage = Invoke-WebRequest "http://localhost:5088/seller-admin/index.html" -WebSession $adminSession -UseBasicParsing -TimeoutSec 30
    if ($sellerAdminPage.StatusCode -ne 200 -or $sellerAdminPage.Content -notmatch 'id="userRows"') {
        throw "Seller user management page verification failed."
    }
    $sellerUsers = Invoke-RestMethod "http://localhost:5088/api/seller-admin/users" -WebSession $adminSession -TimeoutSec 60
    $sellerPage = Invoke-WebRequest "http://localhost:5088/seller-v2/index.html" -UseBasicParsing -TimeoutSec 30
    if ($sellerPage.Content -match 'شرکت ساختمانی آریا سازه|علی احمدی|value="20"' -or
        $sellerPage.Content -notmatch 'id="interactionDrawerTitle"') {
        throw "Seller workspace regression verification failed."
    }
    $versionScript = Invoke-WebRequest "http://localhost:5088/version.js" -UseBasicParsing -TimeoutSec 30
    if ($versionScript.StatusCode -ne 200 -or $versionScript.Content -notmatch [regex]::Escape($version)) {
        throw "Global version badge verification failed."
    }
    $actualVersion = (Invoke-RestMethod "http://localhost:5088/api/version" -TimeoutSec 10).version
    if ($actualVersion -ne $version) { throw "Unexpected running version: $actualVersion" }

    $phase = "VERIFY"
    Write-Host "[8/8] Verifying release..." -ForegroundColor Cyan
    $systemHealth | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $runDir "system-health.json") -Encoding UTF8
    $schedules | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $runDir "schedules.json") -Encoding UTF8
    @"
SUCCESS
Version=$actualVersion
RunDir=$runDir
Backup=$backupRoot
SQL=$($systemHealth.sqlStatus)
Didar=$($systemHealth.didarStatus)
Issabel=$($systemHealth.issabelStatus)
Accounting=$($systemHealth.accountingStatus)
RecoveryModel=$($systemHealth.recoveryModel)
LogMb=$($systemHealth.logSizeMb)
Schedules=$($schedules.Count)
NotificationApi=OK
SellerAdminApi=OK
SellerUsers=$(@($sellerUsers).Count)
"@ | Set-Content (Join-Path $runDir "summary.txt") -Encoding UTF8

    Write-Host "v$version installed successfully." -ForegroundColor Green
    Write-Host "Dashboard: http://localhost:5088/dashboard" -ForegroundColor Green
    Write-Host "Seller users: http://localhost:5088/seller-admin" -ForegroundColor Green
    Write-Host "Seller login: http://localhost:5088/seller-v2" -ForegroundColor Green
    Write-Host "Invoice notifications: http://localhost:5088/invoice-notifications" -ForegroundColor Green
    Write-Host "SMS operator dashboard: http://localhost:5088/sms-dashboard" -ForegroundColor Green
    Write-Host "SQL Log: $($systemHealth.logSizeMb) MB | Recovery: $($systemHealth.recoveryModel)" -ForegroundColor Yellow
}
catch {
    $_ | Format-List * -Force | Out-String | Set-Content (Join-Path $runDir "fatal-error.txt") -Encoding UTF8
    "FAILED`nVersion=$version`nPhase=$phase`nBackup=$backupRoot`nError=$($_.Exception.Message)" |
        Set-Content (Join-Path $runDir "summary.txt") -Encoding UTF8
    Write-Host $_.Exception.ToString() -ForegroundColor Red
    throw
}
finally {
    try { Stop-Transcript | Out-Null } catch { }
    if (Test-Path -LiteralPath $runDir) {
        Compress-Archive -Path (Join-Path $runDir "*") -DestinationPath "$runDir.zip" -Force
        Write-Host "Diagnostic ZIP: $runDir.zip" -ForegroundColor Yellow
    }
}
