param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [string]$RunDirectory
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $RunDirectory = Join-Path $RepositoryRoot "Logs\Runs\v3.7.7-$stamp"
}

New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null

$source = Join-Path $RepositoryRoot "Source"
$logs = Join-Path $RepositoryRoot "Logs"
$summaryPath = Join-Path $RunDirectory "summary.txt"
$environmentPath = Join-Path $RunDirectory "environment.txt"
$configPath = Join-Path $RunDirectory "config-sanitized.txt"
$recentLogsPath = Join-Path $RunDirectory "recent-application-logs.txt"
$tasksPath = Join-Path $RunDirectory "scheduled-tasks.txt"
$portsPath = Join-Path $RunDirectory "ports-and-processes.txt"
$gitPath = Join-Path $RunDirectory "git-status.txt"

$environmentLines = @()
$environmentLines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
$environmentLines += "Computer: $env:COMPUTERNAME"
$environmentLines += "User: $env:USERDOMAIN\$env:USERNAME"
$environmentLines += "PowerShell: $($PSVersionTable.PSVersion)"
$environmentLines += "OS: $([Environment]::OSVersion.VersionString)"

try {
    $dotnet = & dotnet --info 2>&1
    $environmentLines += ""
    $environmentLines += "DOTNET"
    $environmentLines += $dotnet
}
catch {
    $environmentLines += "dotnet --info failed: $($_.Exception.Message)"
}

$environmentLines | Set-Content $environmentPath -Encoding UTF8

# Sanitized local configuration.
$configLines = @()
Get-ChildItem $source -Filter "appsettings*.json" -File -ErrorAction SilentlyContinue |
    Sort-Object Name |
    ForEach-Object {
        $configLines += "===== $($_.Name) ====="
        try {
            $text = Get-Content $_.FullName -Raw -Encoding UTF8
            $text = [regex]::Replace($text, '(?i)("?(Password|ApiToken|Token|Secret)"?\s*:\s*")[^"]*(")', '$1***$3')
            $text = [regex]::Replace($text, '(?i)(Password|PWD)=[^;]*', '$1=***')
            $configLines += $text
        }
        catch {
            $configLines += "READ ERROR: $($_.Exception.Message)"
        }
        $configLines += ""
    }
$configLines | Set-Content $configPath -Encoding UTF8

# Recent app logs from both possible locations.
$logFiles = @()
$logFiles += Get-ChildItem (Join-Path $source "Logs") -File -ErrorAction SilentlyContinue
$logFiles += Get-ChildItem $logs -File -ErrorAction SilentlyContinue

$selectedLogs = $logFiles |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 8

$recent = @()
foreach ($file in $selectedLogs) {
    $recent += "===== $($file.FullName) | $($file.LastWriteTime) ====="
    try {
        $recent += Get-Content $file.FullName -Tail 400 -ErrorAction Stop
    }
    catch {
        $recent += "READ ERROR: $($_.Exception.Message)"
    }
    $recent += ""
}
$recent | Set-Content $recentLogsPath -Encoding UTF8

try {
    Get-ScheduledTask -ErrorAction Stop |
        Where-Object { $_.TaskName -like "*DigiAhan*" } |
        Format-List * |
        Out-String |
        Set-Content $tasksPath -Encoding UTF8
}
catch {
    "Scheduled task read failed: $($_.Exception.Message)" |
        Set-Content $tasksPath -Encoding UTF8
}

try {
    $portInfo = @()
    $portInfo += "NETSTAT 5088"
    $portInfo += (& netstat -ano | Select-String ":5088")
    $portInfo += ""
    $portInfo += "DOTNET PROCESSES"
    $portInfo += (Get-Process dotnet -ErrorAction SilentlyContinue | Format-List * | Out-String)
    $portInfo | Set-Content $portsPath -Encoding UTF8
}
catch {
    "Port/process read failed: $($_.Exception.Message)" |
        Set-Content $portsPath -Encoding UTF8
}

try {
    $git = @()
    $git += (& git -C $RepositoryRoot status --short 2>&1)
    $git += ""
    $git += (& git -C $RepositoryRoot log -1 --oneline 2>&1)
    $git | Set-Content $gitPath -Encoding UTF8
}
catch {
    "Git read failed: $($_.Exception.Message)" |
        Set-Content $gitPath -Encoding UTF8
}

$diagnosticReports = Get-ChildItem $logs -Filter "MVP-v3.7.7-*" -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 2

foreach ($report in $diagnosticReports) {
    Copy-Item $report.FullName (Join-Path $RunDirectory $report.Name) -Force
}

$summary = @()
$summary += "DigiAhan CDR v3.7.7 Diagnostic Bundle"
$summary += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
$summary += "Run directory: $RunDirectory"
$summary += ""
$summary += "Files included:"
Get-ChildItem $RunDirectory -File | Sort-Object Name | ForEach-Object {
    $summary += "- $($_.Name) [$($_.Length) bytes]"
}
$summary += ""
$summary += "Important search terms:"
$summary += "- VoIP event started"
$summary += "- VoIP full BuildCard failed"
$summary += "- FALLBACK"
$summary += "- history persistence failed"
$summary += "- Unhandled request error"
$summary += "- deadlocked"
$summary += "- Execution Timeout"
$summary | Set-Content $summaryPath -Encoding UTF8

$zipPath = "$RunDirectory.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $RunDirectory "*") -DestinationPath $zipPath -Force

Write-Host "Diagnostic bundle created:" -ForegroundColor Green
Write-Host $zipPath -ForegroundColor Yellow
