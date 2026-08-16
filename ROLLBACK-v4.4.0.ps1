param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [string]$BackupPath = ""
)

$ErrorActionPreference = "Stop"
$repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
$backupContainer = [IO.Path]::GetFullPath((Join-Path $repositoryFull "_backups")).TrimEnd('\')
$sourceRoot = Join-Path $repositoryFull "Source"
$serviceName = "DigiAhanCdrDashboard"

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
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(30))
    }
}

function Install-AndStartDashboardService {
    $executable = Join-Path $sourceRoot "bin\Release\net8.0\DigiAhan.CDR.Receiver.exe"
    $serviceCommand = '"' + $executable + '" --contentRoot "' + $sourceRoot + '"'
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        New-Service -Name $serviceName -BinaryPathName $serviceCommand `
            -DisplayName "DigiAhan CDR Dashboard" -Description "DigiAhan CDR dashboard and integration workers" `
            -StartupType Automatic | Out-Null
    }
    else {
        & sc.exe config $serviceName "binPath= $serviceCommand" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to update the dashboard Windows service." }
    }
    Set-Service -Name $serviceName -StartupType Automatic
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    & sc.exe failureflag $serviceName 1 | Out-Null
    Start-Service -Name $serviceName -ErrorAction Stop
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Repository source was not found: $sourceRoot"
}

if ([string]::IsNullOrWhiteSpace($BackupPath)) {
    $candidate = Get-ChildItem -LiteralPath $backupContainer -Directory -Filter "v4.4.0-*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $candidate) { throw "No v4.4.0 backup was found in $backupContainer" }
    $BackupPath = $candidate.FullName
}

$backupFull = [IO.Path]::GetFullPath($BackupPath).TrimEnd('\')
if (-not $backupFull.StartsWith($backupContainer + '\',[StringComparison]::OrdinalIgnoreCase)) {
    throw "Backup must be inside $backupContainer"
}
if (-not (Test-Path -LiteralPath $backupFull -PathType Container)) {
    throw "Backup directory was not found: $backupFull"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $repositoryFull "Logs\Runs\rollback-v4.4.0-$stamp"
$quarantineRoot = Join-Path $backupFull "rollback-quarantine-$stamp"
New-Item -ItemType Directory -Force -Path $runDir,$quarantineRoot | Out-Null
Start-Transcript -Path (Join-Path $runDir "rollback-transcript.txt") -Force | Out-Null

try {
    Write-Host "[1/5] Stopping DigiAhan dashboard for rollback..." -ForegroundColor Cyan
    Stop-DashboardService
    $processIds = @(
        Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -gt 0 } | Select-Object -ExpandProperty OwningProcess
        Get-RepositoryReceiverProcessIds -ExpectedSourceRoot $sourceRoot
    ) | Sort-Object -Unique
    foreach ($processId in $processIds) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }

    Write-Host "[2/5] Restoring every backed-up file..." -ForegroundColor Cyan
    $backupFiles = Get-ChildItem -LiteralPath $backupFull -File -Recurse | Where-Object {
        -not $_.FullName.StartsWith($quarantineRoot + '\',[StringComparison]::OrdinalIgnoreCase)
    }
    foreach ($file in $backupFiles) {
        $relative = $file.FullName.Substring($backupFull.Length).TrimStart('\')
        if ($relative -eq "Source\appsettings.JourneyKernel.local.json") { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $repositoryFull $relative))
        if (-not $target.StartsWith($repositoryFull + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe rollback target: $target"
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    Write-Host "[3/5] Quarantining v4.4-only files (recoverable)..." -ForegroundColor Cyan
    $v440OnlyFiles = @(
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
    foreach ($relative in $v440OnlyFiles) {
        $target = [IO.Path]::GetFullPath((Join-Path $repositoryFull $relative))
        $originalBackup = Join-Path $backupFull $relative
        if ((Test-Path -LiteralPath $target -PathType Leaf) -and -not (Test-Path -LiteralPath $originalBackup -PathType Leaf)) {
            $quarantine = Join-Path $quarantineRoot $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $quarantine) | Out-Null
            Move-Item -LiteralPath $target -Destination $quarantine -Force
        }
    }
    $journeyConfig = Join-Path $sourceRoot "appsettings.JourneyKernel.local.json"
    $journeyConfigBackup = Join-Path $backupFull "Source\appsettings.JourneyKernel.local.json"
    if (Test-Path -LiteralPath $journeyConfigBackup -PathType Leaf) {
        Copy-Item -LiteralPath $journeyConfigBackup -Destination $journeyConfig -Force
    }
    elseif (Test-Path -LiteralPath $journeyConfig -PathType Leaf) {
        $quarantine = Join-Path $quarantineRoot "Source\appsettings.JourneyKernel.local.json"
        New-Item -ItemType Directory -Force -Path (Split-Path $quarantine) | Out-Null
        Move-Item -LiteralPath $journeyConfig -Destination $quarantine -Force
    }

    Write-Host "[4/5] Building the restored version..." -ForegroundColor Cyan
    Push-Location $sourceRoot
    try {
        dotnet build --configuration Release --no-restore -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Restored version build failed." }
    }
    finally { Pop-Location }

    Write-Host "[5/5] Starting and health-checking the restored dashboard..." -ForegroundColor Cyan
    Install-AndStartDashboardService
    $healthy = $false
    foreach ($attempt in 1..60) {
        Start-Sleep -Seconds 1
        try {
            $health = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3
            if ($health.status -eq "healthy") { $healthy = $true; break }
        }
        catch { }
    }
    if (-not $healthy) { throw "Restored dashboard did not become healthy." }
    $runningVersion = (Invoke-RestMethod "http://localhost:5088/api/version" -TimeoutSec 10).version
    "SUCCESS`nRestoredVersion=$runningVersion`nBackup=$backupFull`nQuarantine=$quarantineRoot" |
        Set-Content -LiteralPath (Join-Path $runDir "summary.txt") -Encoding UTF8
    Write-Host "Rollback succeeded. Running version: $runningVersion" -ForegroundColor Green
    Write-Host "v4.4-only files were moved to: $quarantineRoot" -ForegroundColor Yellow
}
finally {
    try { Stop-Transcript | Out-Null } catch { }
}
