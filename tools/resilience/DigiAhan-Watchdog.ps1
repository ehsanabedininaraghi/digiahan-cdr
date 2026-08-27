param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "resilience.config.json"),
    [string]$RepositoryRoot,
    [switch]$Once
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$script:WatchdogLog = $null

function Write-WatchdogLog {
    param([string]$Message, [ValidateSet("INFO", "WARN", "ERROR")][string]$Level = "INFO")
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Add-Content -LiteralPath $script:WatchdogLog -Value $line -Encoding UTF8
}

function Test-DashboardHealth {
    try {
        $response = Invoke-RestMethod -Uri $script:Config.HealthUrl -TimeoutSec 5
        return ($null -ne $response -and [string]$response.status -eq "healthy")
    }
    catch { return $false }
}

function Get-DashboardListenerProcess {
    try {
        $port = ([uri]$script:Config.HealthUrl).Port
        $connection = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop |
            Select-Object -First 1
        if ($null -ne $connection -and $connection.OwningProcess -gt 0) {
            return Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
        }
    }
    catch { }
    return $null
}

function Start-Dashboard {
    $sourceRoot = Join-Path $script:Config.RepositoryRoot "Source"
    if (-not (Test-Path -LiteralPath $sourceRoot)) {
        throw "Source directory was not found: $sourceRoot"
    }

    $releaseRoot = Join-Path $sourceRoot "bin\Release\net8.0"
    $appExe = Join-Path $releaseRoot "DigiAhan.CDR.Receiver.exe"
    $appDll = Join-Path $releaseRoot "DigiAhan.CDR.Receiver.dll"
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stdout = Join-Path $script:LogRoot "dashboard-$stamp.stdout.log"
    $stderr = Join-Path $script:LogRoot "dashboard-$stamp.stderr.log"

    if (Test-Path -LiteralPath $appExe) {
        $process = Start-Process -FilePath $appExe -WorkingDirectory $sourceRoot `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    }
    elseif (Test-Path -LiteralPath $appDll) {
        $process = Start-Process -FilePath "dotnet" -ArgumentList @($appDll) -WorkingDirectory $sourceRoot `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    }
    else {
        throw "No Release build was found. Run Build-ResilientDashboard.ps1 once."
    }

    Write-WatchdogLog "Dashboard start requested. PID=$($process.Id)"
    for ($second = 0; $second -lt [int]$script:Config.StartupGraceSeconds; $second++) {
        Start-Sleep -Seconds 1
        if (Test-DashboardHealth) {
            Write-WatchdogLog "Dashboard is healthy. PID=$($process.Id)"
            return $true
        }
        if ($process.HasExited) {
            Write-WatchdogLog "Dashboard exited during startup. ExitCode=$($process.ExitCode); stderr=$stderr" "ERROR"
            return $false
        }
    }
    Write-WatchdogLog "Dashboard did not become healthy within the startup grace period. PID=$($process.Id)" "ERROR"
    return $false
}

if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Configuration file was not found: $ConfigPath" }
$script:Config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $script:Config.RepositoryRoot = $RepositoryRoot
}
$script:Config.RepositoryRoot = [Environment]::ExpandEnvironmentVariables([string]$script:Config.RepositoryRoot)
$script:LogRoot = Join-Path $script:Config.RepositoryRoot "Logs\Resilience"
New-Item -ItemType Directory -Force -Path $script:LogRoot | Out-Null
$script:WatchdogLog = Join-Path $script:LogRoot ("watchdog-{0}.log" -f (Get-Date -Format "yyyyMMdd"))

$mutexName = "Local\DigiAhanDashboardWatchdog"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false
try {
    $ownsMutex = $mutex.WaitOne(0, $false)
    if (-not $ownsMutex) { exit 0 }

    Get-ChildItem -LiteralPath $script:LogRoot -File -ErrorAction SilentlyContinue |
        Where-Object LastWriteTime -lt (Get-Date).AddDays(-[int]$script:Config.LogRetentionDays) |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Write-WatchdogLog "Watchdog started. Repository=$($script:Config.RepositoryRoot)"
    $failures = 0
    $effectiveFailureThreshold = if ($Once) { 1 } else { [int]$script:Config.FailureThreshold }
    do {
        if (Test-DashboardHealth) {
            $failures = 0
        }
        else {
            $failures++
            Write-WatchdogLog "Health check failed ($failures/$effectiveFailureThreshold)." "WARN"
            if ($failures -ge $effectiveFailureThreshold) {
                $listener = Get-DashboardListenerProcess
                if ($null -ne $listener) {
                    Write-WatchdogLog "Port is occupied by unhealthy PID=$($listener.Id); stopping it." "WARN"
                    Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                }
                try { [void](Start-Dashboard) }
                catch { Write-WatchdogLog $_.Exception.Message "ERROR" }
                $failures = 0
                if (-not $Once) { Start-Sleep -Seconds ([int]$script:Config.RestartCooldownSeconds) }
            }
        }
        if (-not $Once) { Start-Sleep -Seconds ([int]$script:Config.CheckIntervalSeconds) }
    } while (-not $Once)
}
catch {
    if ($null -ne $script:WatchdogLog) { Write-WatchdogLog $_.Exception.ToString() "ERROR" }
    throw
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
