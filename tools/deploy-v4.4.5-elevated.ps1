param()

$ErrorActionPreference = 'Stop'
$diagnosticPath = 'D:\ChatGPT\DIGIAHAN\v431-repo\tools\deploy-v4.4.5-elevated.log'
trap {
    $_ | Format-List * -Force | Out-String | Set-Content -LiteralPath $diagnosticPath -Encoding UTF8
    exit 1
}
$repoSource = 'D:\ChatGPT\DIGIAHAN\v431-repo\Source'
$liveRoot = 'D:\DigiAhan\CDR4.0'
$liveSource = Join-Path $liveRoot 'Source'
$serviceName = 'DigiAhanCdrDashboard'
$relativeFiles = @(
    'DigiAhan.CDR.Receiver.csproj', 'Models\SystemOperationsModels.cs', 'Program.cs',
    'Services\IntegrationSchedulerRepository.cs', 'Services\IntegrationSchedulerService.cs',
    'Sql\DashboardCallsCount.sql', 'Sql\DashboardCallsPage.sql',
    'wwwroot\dashboard\app.js', 'wwwroot\dashboard\index.html', 'wwwroot\dashboard\style.css',
    'wwwroot\sms-dashboard\app.js', 'wwwroot\sms-dashboard\index.html', 'wwwroot\sms-dashboard\style.css',
    'wwwroot\seller-v2\app.js', 'wwwroot\seller-v2\style.css', 'wwwroot\seller-v2\enhancements.css',
    'wwwroot\seller-v2\balance.css', 'wwwroot\seller-v2\index.html',
    'wwwroot\version.js'
)

function Get-ConfigHashes {
    param([string]$Root)
    $hashes = @{}
    Get-ChildItem -LiteralPath $Root -File -Filter 'appsettings*.json' | ForEach-Object {
        $stream = [IO.File]::OpenRead($_.FullName)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hashes[$_.Name] = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('X2') }) }
        finally { $sha.Dispose(); $stream.Dispose() }
    }
    return $hashes
}

function Assert-SameConfigHashes {
    param([hashtable]$Before, [hashtable]$After)
    if ($Before.Keys.Count -ne $After.Keys.Count) { throw 'Configuration file count changed during deployment.' }
    foreach ($name in $Before.Keys) {
        if ($After[$name] -ne $Before[$name]) { throw "Configuration changed during deployment: $name" }
    }
}

function Stop-LiveReceiverProcesses {
    param([string]$ExpectedSourceRoot)
    $prefix = [IO.Path]::GetFullPath($ExpectedSourceRoot).TrimEnd('\') + '\'
    Get-Process -Name 'DigiAhan.CDR.Receiver' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $path = [IO.Path]::GetFullPath([string]$_.Path)
            if ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force -ErrorAction Stop
            }
        }
        catch { }
    }
    Start-Sleep -Seconds 2
}

if (-not (Test-Path -LiteralPath $repoSource)) { throw "Build source is missing: $repoSource" }
if (-not (Test-Path -LiteralPath $liveSource)) { throw "Live source is missing: $liveSource" }
if (-not (Test-Path -LiteralPath (Join-Path $repoSource 'bin\Release\net8.0\DigiAhan.CDR.Receiver.exe'))) {
    throw 'Release build output is missing.'
}

$beforeConfigs = Get-ConfigHashes $liveSource
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupRoot = Join-Path $liveRoot "_backups\v4.4.5-$stamp"
$liveBin = Join-Path $liveSource 'bin\Release\net8.0'
$backupBin = Join-Path $backupRoot 'bin\Release\net8.0'
New-Item -ItemType Directory -Force -Path $backupRoot, $backupBin | Out-Null

foreach ($relative in $relativeFiles) {
    $target = Join-Path $liveSource $relative
    if (Test-Path -LiteralPath $target) {
        $backup = Join-Path $backupRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
        Copy-Item -LiteralPath $target -Destination $backup -Force
    }
}
Get-ChildItem -LiteralPath $liveBin -File | Where-Object { $_.Name -notlike 'appsettings*.json' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $backupBin $_.Name) -Force
}

$service = Get-Service -Name $serviceName -ErrorAction Stop
Stop-Service -Name $serviceName -ErrorAction Stop
$service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
Stop-LiveReceiverProcesses $liveSource

try {
    foreach ($relative in $relativeFiles) {
        $source = Join-Path $repoSource $relative
        $target = Join-Path $liveSource $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    $repoBin = Join-Path $repoSource 'bin\Release\net8.0'
    Get-ChildItem -LiteralPath $repoBin -File | Where-Object { $_.Name -notlike 'appsettings*.json' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $liveBin $_.Name) -Force
    }
    Start-Service -Name $serviceName -ErrorAction Stop
    $healthy = $false
    foreach ($attempt in 1..30) {
        Start-Sleep -Seconds 1
        try {
            $health = Invoke-RestMethod 'http://localhost:5088/health' -TimeoutSec 3
            if ($health.status -eq 'healthy') { $healthy = $true; break }
        }
        catch { }
    }
    if (-not $healthy) { throw 'Service did not become healthy within 30 seconds.' }
    Assert-SameConfigHashes $beforeConfigs (Get-ConfigHashes $liveSource)
    $version = (Invoke-RestMethod 'http://localhost:5088/api/version' -TimeoutSec 10).version
    if ($version -ne '4.4.5') { throw "Unexpected running version: $version" }
    [pscustomobject]@{ Status = 'SUCCESS'; Version = $version; Backup = $backupRoot; ConfigHashesPreserved = $true } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $backupRoot 'deploy-result.json') -Encoding UTF8
    Write-Host "SUCCESS Version=$version Backup=$backupRoot" -ForegroundColor Green
}
catch {
    $errorText = $_.Exception.ToString()
    Set-Content -LiteralPath (Join-Path $backupRoot 'deploy-error.txt') -Value $errorText -Encoding UTF8
    Stop-LiveReceiverProcesses $liveSource
    foreach ($relative in $relativeFiles) {
        $backup = Join-Path $backupRoot $relative
        if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination (Join-Path $liveSource $relative) -Force }
    }
    Get-ChildItem -LiteralPath $backupBin -File -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $liveBin $_.Name) -Force
    }
    if ((Get-Service -Name $serviceName).Status -ne 'Running') { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }
    throw
}
