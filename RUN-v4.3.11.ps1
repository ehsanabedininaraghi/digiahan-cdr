param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [string]$ReleaseVersion = "4.3.11",
    [switch]$ResetDashboardPassword,
    [switch]$EnableJourneyPilot,
    [string[]]$JourneyPilotSellerKeys = @(),
    [switch]$EnableJourneyAutoCapture,
    [switch]$ValidatePackageOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$version = $ReleaseVersion
$payloadRoot = Join-Path $PSScriptRoot "payload"
$sourceRoot = Join-Path $RepositoryRoot "Source"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $RepositoryRoot "Logs\Runs\v$version-$stamp"
$backupRoot = Join-Path $RepositoryRoot "_backups\v$version-$stamp"
$dashboardStopped = $false
$filesInstalled = $false
$serviceName = "DigiAhanCdrDashboard"
$taskName = "DigiAhan CDR Dashboard"
$accountingTaskName = "DigiAhan Accounting Bridge Interactive"

function Get-RepositoryReceiverProcessIds {
    param([Parameter(Mandatory = $true)][string]$ExpectedSourceRoot)

    $expected = [IO.Path]::GetFullPath($ExpectedSourceRoot).TrimEnd('\') + '\'
    $ids = @()
    foreach ($process in @(Get-Process -Name "DigiAhan.CDR.Receiver" -ErrorAction SilentlyContinue)) {
        try {
            $path = [IO.Path]::GetFullPath([string]$process.Path)
            if ($path.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) { $ids += $process.Id }
        }
        catch { }
    }
    return @($ids)
}

function Stop-DashboardService {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -ne $task -and $task.State -ne "Ready") {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(30))
    }
}

function Install-AndStartDashboardService {
    param([Parameter(Mandatory = $true)][string]$ExpectedSourceRoot)

    $executable = Join-Path $ExpectedSourceRoot "bin\Release\net8.0\DigiAhan.CDR.Receiver.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Dashboard executable was not found: $executable"
    }

    $userId = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $executable `
        -Argument "--contentRoot `"$ExpectedSourceRoot`"" -WorkingDirectory $ExpectedSourceRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description "Persistent DigiAhan CDR dashboard with automatic recovery" -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
}

function Grant-DashboardServiceSqlAccessV441 {
    param([Parameter(Mandatory = $true)][string]$ExpectedSourceRoot)
    $runtimeConfigPath = Join-Path $ExpectedSourceRoot "appsettings.json"
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) { return }
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $runtimeConnection = [string]$runtimeConfig.ConnectionStrings.DigiAhanCdr
    $connectionBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($runtimeConnection)
    if (-not $connectionBuilder.IntegratedSecurity) { return }
    $databaseName = [string]$connectionBuilder.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($databaseName)) { throw "Integrated-security connection has no database name." }
    $escapedDatabase = $databaseName.Replace("]","]]")
    $connection = New-Object System.Data.SqlClient.SqlConnection($runtimeConnection)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 60
        $command.CommandText = @"
IF SUSER_ID(N'NT SERVICE\DigiAhanCdrDashboard') IS NULL
    CREATE LOGIN [NT SERVICE\DigiAhanCdrDashboard] FROM WINDOWS;
USE [$escapedDatabase];
IF USER_ID(N'NT SERVICE\DigiAhanCdrDashboard') IS NULL
    CREATE USER [NT SERVICE\DigiAhanCdrDashboard] FOR LOGIN [NT SERVICE\DigiAhanCdrDashboard];
IF IS_ROLEMEMBER(N'db_datareader',N'NT SERVICE\DigiAhanCdrDashboard')<>1
    ALTER ROLE [db_datareader] ADD MEMBER [NT SERVICE\DigiAhanCdrDashboard];
IF IS_ROLEMEMBER(N'db_datawriter',N'NT SERVICE\DigiAhanCdrDashboard')<>1
    ALTER ROLE [db_datawriter] ADD MEMBER [NT SERVICE\DigiAhanCdrDashboard];
GRANT EXECUTE TO [NT SERVICE\DigiAhanCdrDashboard];
GRANT VIEW DEFINITION TO [NT SERVICE\DigiAhanCdrDashboard];
"@
        $command.ExecuteNonQuery() | Out-Null
    }
    finally { $connection.Dispose() }
}

function Install-AndStartDashboardServiceV441 {
    param([Parameter(Mandatory = $true)][string]$ExpectedSourceRoot)

    $executable = Join-Path $ExpectedSourceRoot "bin\Release\net8.0\DigiAhan.CDR.Receiver.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Dashboard executable was not found: $executable"
    }
    $binaryPath = [string]::Format('"{0}" --contentRoot "{1}"',$executable,$ExpectedSourceRoot)
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        & sc.exe create $serviceName binPath= $binaryPath start= delayed-auto obj= "NT SERVICE\DigiAhanCdrDashboard" DisplayName= "DigiAhan CDR Dashboard" | Out-Null
    } else {
        & sc.exe config $serviceName binPath= $binaryPath start= delayed-auto obj= "NT SERVICE\DigiAhanCdrDashboard" | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Windows service configuration failed." }
    & sc.exe config $serviceName depend= MSSQLSERVER | Out-Null
    & sc.exe description $serviceName "DigiAhan dashboard with automatic startup and no interactive login" | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    & sc.exe failureflag $serviceName 1 | Out-Null
    Grant-DashboardServiceSqlAccessV441 -ExpectedSourceRoot $ExpectedSourceRoot
    $legacyTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -ne $legacyTask) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop }
    Start-Service -Name $serviceName -ErrorAction Stop
}

function Install-AccountingInteractiveTaskV444 {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedRepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceRoot
    )

    $wrapper = Join-Path $ExpectedRepositoryRoot "tools\accounting-bridge-interactive-task.ps1"
    if (-not (Test-Path -LiteralPath $wrapper -PathType Leaf)) {
        throw "Interactive accounting bridge wrapper was not found: $wrapper"
    }

    $exchangeDirectory = Join-Path $ExpectedRepositoryRoot "runtime\accounting-bridge"
    New-Item -ItemType Directory -Path $exchangeDirectory -Force | Out-Null

    $configPath = Join-Path $ExpectedSourceRoot "appsettings.DataGathering.local.json"
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    else {
        $config = [pscustomobject]@{}
    }
    if ($null -eq $config.PSObject.Properties["DataGathering"]) {
        $config | Add-Member -NotePropertyName DataGathering -NotePropertyValue ([pscustomobject]@{})
    }
    foreach ($entry in @{
        AccountingBridgeTaskName = $accountingTaskName
        AccountingBridgeTaskTimeoutSeconds = 600
    }.GetEnumerator()) {
        $property = $config.DataGathering.PSObject.Properties[$entry.Key]
        if ($null -eq $property) {
            $config.DataGathering | Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value
        }
        else {
            $property.Value = $entry.Value
        }
    }
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8

    $userId = "$env:USERDOMAIN\$env:USERNAME"
    $arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$wrapper`" -ExchangeDirectory `"$exchangeDirectory`""
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $accountingTaskName -Action $action -Principal $principal `
        -Settings $settings -Description "Runs accounting SQL sync in the signed-in user's network session." -Force | Out-Null

    $serviceAccount = "NT SERVICE\$serviceName"
    & icacls.exe $exchangeDirectory /grant "${serviceAccount}:(OI)(CI)M" /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant the dashboard service access to the accounting exchange directory." }

    $serviceSid = ([Security.Principal.NTAccount]$serviceAccount).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    $scheduler = New-Object -ComObject "Schedule.Service"
    $scheduler.Connect()
    $task = $scheduler.GetFolder("\").GetTask($accountingTaskName)
    $dacl = $task.GetSecurityDescriptor(4)
    if ($dacl -notmatch [regex]::Escape($serviceSid)) {
        $task.SetSecurityDescriptor($dacl + "(A;;GRGX;;;$serviceSid)",0)
    }
}

$relativeFiles = @(
    "global.json",
    "Source\Program.cs",
    "Source\DigiAhan.CDR.Receiver.csproj",
    "Source\appsettings.example.json",
    "Source\appsettings.DataGathering.example.json",
    "Source\appsettings.Accounting.example.json",
    "Source\appsettings.Didar.example.json",
    "Source\Models\DashboardModels.cs",
    "Source\Models\AiAnalysisApiModels.cs",
    "Source\Models\AiAnalysisModels.cs",
    "Source\Models\AiPipelineModels.cs",
    "Source\Models\RecordingSyncModels.cs",
    "Source\Models\InvoiceNotificationModels.cs",
    "Source\Models\CallIntelligenceModels.cs",
    "Source\Models\CustomerMappingModels.cs",
    "Source\Models\SellerWorkspaceModels.cs",
    "Source\Models\SalesDashboardModels.cs",
    "Source\Models\SystemOperationsModels.cs",
    "Source\Services\AccountingSyncService.cs",
    "Source\Services\AiAnalysisRepository.cs",
    "Source\Services\AiCallDiscoveryWorker.cs",
    "Source\Services\AiPipelineRepository.cs",
    "Source\Services\AiTranscriptAnalyzer.cs",
    "Source\Services\DailyRecordingIngestionWorker.cs",
    "Source\Services\DashboardRepository.cs",
    "Source\Services\CustomerIntelligenceRepository.cs",
    "Source\Services\CustomerMappingService.cs",
    "Source\Services\CustomerIdentityReconcileService.cs",
    "Source\Services\DataGatheringCoordinator.cs",
    "Source\Services\DatabaseMaintenanceService.cs",
    "Source\Services\DeliveryVoucherParser.cs",
    "Source\Services\IntegrationSchedulerService.cs",
    "Source\Services\IntegrationSchedulerRepository.cs",
    "Source\Services\IntegrationSchedulerWorker.cs",
    "Source\Services\InvoiceNotificationRepository.cs",
    "Source\Services\FasterWhisperTranscriber.cs",
    "Source\Services\IssabelRecordingPathResolver.cs",
    "Source\Services\IssabelSftpRecordingClient.cs",
    "Source\Services\LegacyAccountingBridgeRunner.cs",
    "Source\Services\LegacyAgentBridgeService.cs",
    "Source\Services\MappingValueNormalizer.cs",
    "Source\Services\AgentEventStore.cs",
    "Source\Services\AgentCallStateStore.cs",
    "Source\Services\PublicTokenService.cs",
    "Source\Services\RecordingAssetRepository.cs",
    "Source\Services\RecordingAudioValidator.cs",
    "Source\Services\SellerWorkspaceAccessService.cs",
    "Source\Services\SellerWorkspaceRepository.cs",
    "Source\Services\SalesDashboardRepository.cs",
    "Source\Services\SystemHealthService.cs",
    "Source\Services\DidarApiSyncService.cs",
    "Source\Services\DidarPhoneRebuildService.cs",
    "Source\Services\ExcelMappingReader.cs",
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
    "Source\wwwroot\seller-activity\app.js",
    "Source\wwwroot\seller-activity\index.html",
    "Source\wwwroot\seller-activity\style.css",
    "Source\wwwroot\seller-v2\app.js",
    "Source\wwwroot\seller-v2\balance.css",
    "Source\wwwroot\seller-v2\enhancements.css",
    "Source\wwwroot\seller-v2\index.html",
    "Source\wwwroot\seller-v2\style.css",
    "Source\wwwroot\seller-admin\app.js",
    "Source\wwwroot\seller-admin\index.html",
    "Source\wwwroot\seller-admin\style.css",
    "Source\wwwroot\seller-mapping\app.js",
    "Source\wwwroot\seller-mapping\index.html",
    "Source\wwwroot\seller-mapping\style.css",
    "Source\wwwroot\version.js",
    "tools\accounting-bridge-v4.3.10.ps1",
    "tools\accounting-bridge-interactive-task.ps1",
    "tools\diagnose-didar-phone.ps1",
    "issabel\digiahan_call_intelligence.py",
    "tools\ai\transcribe_sample.py"
)

# Local configuration files contain site-specific credentials and settings. They are
# deliberately excluded from the release payload, but every update must still back
# them up. When an operator runs the installer from the maintained repository and an
# active Didar/Accounting file is missing, recover it from that repository source.
$protectedLocalConfigs = @(
    "Source\appsettings.Voip.local.json",
    "Source\appsettings.RecordingIngestion.local.json",
    "Source\appsettings.SellerWorkspace.local.json",
    "Source\appsettings.JourneyKernel.local.json",
    "Source\appsettings.Dashboard.local.json",
    "Source\appsettings.Didar.local.json",
    "Source\appsettings.Accounting.local.json",
    "Source\appsettings.DataGathering.local.json"
)
$recoverableRepositoryConfigs = @(
    "Source\appsettings.Didar.local.json",
    "Source\appsettings.Accounting.local.json"
)

if ([version]$version -ge [version]"4.4.0") {
    $relativeFiles += @(
        "Source\appsettings.JourneyKernel.example.json",
        "Source\Features\Journey\CustomerJourneyEndpoints.cs",
        "Source\Models\CustomerJourneyModels.cs",
        "Source\Services\CustomerJourneyRepository.cs",
        "Source\Services\CustomerJourneyRules.cs",
        "Source\Sql\CustomerJourneyKernelV440.sql",
        "Source\wwwroot\seller-v3\app.js",
        "Source\wwwroot\seller-v3\index.html",
        "Source\wwwroot\seller-v3\style.css",
        "Source\wwwroot\journey-control\app.js",
        "Source\wwwroot\journey-control\index.html",
        "Source\wwwroot\journey-control\style.css"
    )
}

if (-not (Test-Path -LiteralPath $payloadRoot)) { throw "Release payload not found: $payloadRoot" }

if ($ValidatePackageOnly) {
    $missing = @($relativeFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $payloadRoot $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { throw "Release payload is incomplete: $($missing -join ', ')" }

    $scripts = @(
        Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter "*.ps1" -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $payloadRoot -File -Filter "*.ps1" -Recurse -ErrorAction SilentlyContinue
    )
    foreach ($script in $scripts) {
        $tokens = $null
        $errors = $null
        $scriptText = Get-Content -LiteralPath $script.FullName -Raw -Encoding UTF8
        [Management.Automation.Language.Parser]::ParseInput(
            $scriptText,$script.FullName,[ref]$tokens,[ref]$errors) | Out-Null
        if ($errors.Count -gt 0) { throw "PowerShell syntax error in $($script.FullName): $($errors[0].Message)" }
    }
    $program = Get-Content -LiteralPath (Join-Path $payloadRoot "Source\Program.cs") -Raw -Encoding UTF8
    if ($program -notmatch ('AppVersion\s*=\s*"' + [regex]::Escape($version) + '"')) {
        throw "Payload Program.cs does not identify v$version."
    }
    Write-Host "PASS: v$version package payload is complete and all PowerShell scripts parse." -ForegroundColor Green
    return
}

if (-not (Test-Path -LiteralPath $sourceRoot)) { throw "Repository source not found: $sourceRoot" }

# A Windows service restarts processes behind the installer's back. Validate it before
# disabling tasks, stopping the dashboard, writing backups, or copying any payload file.
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    $serviceConfig = (& sc.exe qc $serviceName 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect Windows service $serviceName; no files were changed." }
    if ($serviceConfig.IndexOf($sourceRoot,[StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Windows service $serviceName points outside $sourceRoot. Correct its path before installing; no files were changed."
    }
}

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
    Stop-DashboardService
    $processIds = @(
        Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -gt 0 } |
            Select-Object -ExpandProperty OwningProcess
        Get-RepositoryReceiverProcessIds -ExpectedSourceRoot $sourceRoot
    ) | Sort-Object -Unique
    foreach ($processId in $processIds) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    $remainingAppProcesses = @(Get-RepositoryReceiverProcessIds -ExpectedSourceRoot $sourceRoot)
    if ($remainingAppProcesses.Count -gt 0) {
        throw "The existing DigiAhan.CDR.Receiver process could not be stopped."
    }
    $dashboardStopped = $true

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
    foreach ($relative in $protectedLocalConfigs) {
        $target = Join-Path $RepositoryRoot $relative
        if (Test-Path -LiteralPath $target -PathType Leaf) {
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
    foreach ($relative in $recoverableRepositoryConfigs) {
        $target = Join-Path $RepositoryRoot $relative
        $repositorySource = Join-Path $PSScriptRoot $relative
        if ((-not (Test-Path -LiteralPath $target -PathType Leaf)) -and
            (Test-Path -LiteralPath $repositorySource -PathType Leaf)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath $repositorySource -Destination $target -Force
            Write-Host "Recovered missing local configuration: $relative" -ForegroundColor Green
        }
    }
    $filesInstalled = $true

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

    if ($EnableJourneyAutoCapture -and -not $EnableJourneyPilot) {
        throw "EnableJourneyAutoCapture requires EnableJourneyPilot."
    }
    if ($EnableJourneyPilot) {
        $cleanPilotKeys = @($JourneyPilotSellerKeys | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
        if ($cleanPilotKeys.Count -eq 0) {
            throw "At least one JourneyPilotSellerKeys value is required for a safe first pilot."
        }
        $journeyConfigPath = Join-Path $sourceRoot "appsettings.JourneyKernel.local.json"
        if (Test-Path -LiteralPath $journeyConfigPath) {
            $journeyConfigBackup = Join-Path $backupRoot "Source\appsettings.JourneyKernel.local.json"
            New-Item -ItemType Directory -Force -Path (Split-Path $journeyConfigBackup) | Out-Null
            Copy-Item -LiteralPath $journeyConfigPath -Destination $journeyConfigBackup -Force
        }
        [ordered]@{
            JourneyKernel = [ordered]@{
                Enabled = $true
                AutoCaptureSellerInteractions = [bool]$EnableJourneyAutoCapture
                DefaultLeadSlaMinutes = 120
                DefaultFollowUpMinutes = 1440
                PilotSellerKeys = $cleanPilotKeys
            }
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $journeyConfigPath -Encoding UTF8
        Write-Host "Journey Kernel pilot enabled only for: $($cleanPilotKeys -join ', ')" -ForegroundColor Yellow
        Write-Host "Automatic Seller v2 capture: $([bool]$EnableJourneyAutoCapture)" -ForegroundColor Yellow
    }
    elseif ([version]$version -ge [version]"4.4.0") {
        Write-Host "Journey Kernel remains disabled/preserved. Seller v2 is the active production workspace." -ForegroundColor Green
    }

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
    Write-Host "[6/8] Installing and starting the persistent dashboard service..." -ForegroundColor Cyan
    Install-AndStartDashboardServiceV441 -ExpectedSourceRoot $sourceRoot
    Install-AccountingInteractiveTaskV444 -ExpectedRepositoryRoot $RepositoryRoot -ExpectedSourceRoot $sourceRoot

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
    if ([version]$version -ge [version]"4.4.0") {
        $sellerV3Page = Invoke-WebRequest "http://localhost:5088/seller-v3/index.html" -UseBasicParsing -TimeoutSec 30
        if ($sellerV3Page.StatusCode -ne 200 -or $sellerV3Page.Content -notmatch 'id="workQueue"' -or
            $sellerV3Page.Content -match '/dashboard') {
            throw "Seller v3 isolated workspace verification failed."
        }
        if ($EnableJourneyPilot) {
            $journeyExceptions = Invoke-RestMethod "http://localhost:5088/api/journey-control/exceptions?take=1" `
                -WebSession $adminSession -TimeoutSec 180
        }
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
JourneyPilot=$([bool]$EnableJourneyPilot)
JourneyAutoCapture=$([bool]($EnableJourneyPilot -and $EnableJourneyAutoCapture))
"@ | Set-Content (Join-Path $runDir "summary.txt") -Encoding UTF8

    Write-Host "v$version installed successfully." -ForegroundColor Green
    Write-Host "Dashboard: http://localhost:5088/dashboard" -ForegroundColor Green
    Write-Host "Seller users: http://localhost:5088/seller-admin" -ForegroundColor Green
    Write-Host "Seller login: http://localhost:5088/seller-v2" -ForegroundColor Green
    if ([version]$version -ge [version]"4.4.0") {
        Write-Host "Seller v3 pilot: http://localhost:5088/seller-v3" -ForegroundColor Green
        Write-Host "Journey control: http://localhost:5088/journey-control" -ForegroundColor Green
    }
    Write-Host "Invoice notifications: http://localhost:5088/invoice-notifications" -ForegroundColor Green
    Write-Host "SMS operator dashboard: http://localhost:5088/sms-dashboard" -ForegroundColor Green
    Write-Host "SQL Log: $($systemHealth.logSizeMb) MB | Recovery: $($systemHealth.recoveryModel)" -ForegroundColor Yellow
}
catch {
    $fatalError = $_
    $fatalError | Format-List * -Force | Out-String | Set-Content (Join-Path $runDir "fatal-error.txt") -Encoding UTF8
    "FAILED`nVersion=$version`nPhase=$phase`nBackup=$backupRoot`nError=$($fatalError.Exception.Message)" |
        Set-Content (Join-Path $runDir "summary.txt") -Encoding UTF8
    Write-Host $fatalError.Exception.ToString() -ForegroundColor Red
    if ($dashboardStopped) {
        Write-Host "Attempting automatic rollback to the pre-installation files..." -ForegroundColor Yellow
        try {
            Stop-DashboardService
            $rollbackProcessIds = @(
                Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
                    Where-Object { $_.OwningProcess -gt 0 } |
                    Select-Object -ExpandProperty OwningProcess
                Get-RepositoryReceiverProcessIds -ExpectedSourceRoot $sourceRoot
            ) | Sort-Object -Unique
            foreach ($rollbackProcessId in $rollbackProcessIds) {
                Stop-Process -Id $rollbackProcessId -Force -ErrorAction SilentlyContinue
            }

            if ($filesInstalled) {
                $quarantineRoot = Join-Path $backupRoot "failed-v$version-files"
                foreach ($relative in $relativeFiles) {
                    $target = Join-Path $RepositoryRoot $relative
                    $backup = Join-Path $backupRoot $relative
                    if (Test-Path -LiteralPath $backup -PathType Leaf) {
                        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
                        Copy-Item -LiteralPath $backup -Destination $target -Force
                    }
                    elseif (Test-Path -LiteralPath $target -PathType Leaf) {
                        $quarantine = Join-Path $quarantineRoot $relative
                        New-Item -ItemType Directory -Force -Path (Split-Path $quarantine) | Out-Null
                        Move-Item -LiteralPath $target -Destination $quarantine -Force
                    }
                }

                if ($EnableJourneyPilot) {
                    $journeyConfigPath = Join-Path $sourceRoot "appsettings.JourneyKernel.local.json"
                    $journeyConfigBackup = Join-Path $backupRoot "Source\appsettings.JourneyKernel.local.json"
                    if (Test-Path -LiteralPath $journeyConfigBackup -PathType Leaf) {
                        Copy-Item -LiteralPath $journeyConfigBackup -Destination $journeyConfigPath -Force
                    }
                    elseif (Test-Path -LiteralPath $journeyConfigPath -PathType Leaf) {
                        $journeyConfigQuarantine = Join-Path $quarantineRoot "Source\appsettings.JourneyKernel.local.json"
                        New-Item -ItemType Directory -Force -Path (Split-Path $journeyConfigQuarantine) | Out-Null
                        Move-Item -LiteralPath $journeyConfigPath -Destination $journeyConfigQuarantine -Force
                    }
                }
            }

            Push-Location $sourceRoot
            try {
                dotnet build --configuration Release --no-restore -p:NuGetAudit=false
                if ($LASTEXITCODE -ne 0) { throw "Rollback build failed." }
            }
            finally { Pop-Location }

            Install-AndStartDashboardServiceV441 -ExpectedSourceRoot $sourceRoot
            $rollbackHealthy = $false
            foreach ($rollbackAttempt in 1..60) {
                Start-Sleep -Seconds 1
                try {
                    $rollbackHealth = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3
                    if ($rollbackHealth.status -eq "healthy") { $rollbackHealthy = $true; break }
                }
                catch { }
            }
            if (-not $rollbackHealthy) { throw "Rollback application did not become healthy." }
            Write-Host "Automatic rollback succeeded; the previous dashboard is healthy." -ForegroundColor Green
            Add-Content -LiteralPath (Join-Path $runDir "summary.txt") -Value "`nAutomaticRollback=SUCCESS" -Encoding UTF8
        }
        catch {
            Write-Host "Automatic rollback failed: $($_.Exception.Message)" -ForegroundColor Red
            Add-Content -LiteralPath (Join-Path $runDir "summary.txt") -Value "`nAutomaticRollback=FAILED`nRollbackError=$($_.Exception.Message)" -Encoding UTF8
        }
    }
    throw $fatalError
}
finally {
    try { Stop-Transcript | Out-Null } catch { }
    if (Test-Path -LiteralPath $runDir) {
        Compress-Archive -Path (Join-Path $runDir "*") -DestinationPath "$runDir.zip" -Force
        Write-Host "Diagnostic ZIP: $runDir.zip" -ForegroundColor Yellow
    }
}
