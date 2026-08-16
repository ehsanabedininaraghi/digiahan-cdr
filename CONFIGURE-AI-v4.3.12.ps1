param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [Parameter(Mandatory = $true)][string]$IssabelUsername,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [Parameter(Mandatory = $true)][string]$KnownHostsPath,
    [string]$PythonExecutable = "python",
    [string]$PythonPath = "",
    [string]$ModelCache = "Models\Whisper",
    [switch]$Enable
)

$ErrorActionPreference = "Stop"
$sourceRoot = Join-Path $RepositoryRoot "Source"
if (-not (Test-Path -LiteralPath $sourceRoot)) { throw "Source folder not found: $sourceRoot" }
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw "Private key not found: $PrivateKeyPath" }
if (-not (Test-Path -LiteralPath $KnownHostsPath -PathType Leaf)) { throw "known_hosts not found: $KnownHostsPath" }
$sftp = Get-Command sftp.exe -ErrorAction Stop
$python = Get-Command $PythonExecutable -ErrorAction Stop
$scriptPath = Join-Path $RepositoryRoot "tools\ai\transcribe_sample.py"
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "Transcription script not found: $scriptPath" }

$settings = @{
    RecordingIngestion = @{
        Enabled = [bool]$Enable
        SourceName = "issabel-primary"
        Host = "192.168.8.2"
        Port = 22
        Username = $IssabelUsername
        PrivateKeyPath = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
        KnownHostsPath = (Resolve-Path -LiteralPath $KnownHostsPath).Path
        SftpExecutable = $sftp.Source
        RemoteRoot = "/var/spool/asterisk/monitor"
        StagingRoot = "Recordings/Staging"
        PollSeconds = 300
        StabilitySeconds = 180
        BatchSize = 20
        LeaseSeconds = 1800
        MaxAttempts = 5
        LocalRetentionHours = 24
        TimeZoneId = "Iran Standard Time"
        TargetDateOffsetDays = 0
        Transcription = @{
            PythonExecutable = $python.Source
            PythonPath = $PythonPath
            ScriptPath = "tools/ai/transcribe_sample.py"
            ModelName = "small"
            ModelCache = $ModelCache
            Threads = 2
            TimeoutMinutes = 60
            InitialPrompt = "Persian steel sales call with product, price, stock and delivery details"
            Hotwords = "DigiAhan steel beam rebar sheet profile Isfahan"
        }
    }
}

$target = Join-Path $sourceRoot "appsettings.RecordingIngestion.local.json"
$settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $target -Encoding UTF8
Write-Host "AI recording configuration saved: $target" -ForegroundColor Green
Write-Host "Enabled=$([bool]$Enable). Re-run RUN-v4.3.12.cmd to restart and verify the service." -ForegroundColor Yellow
