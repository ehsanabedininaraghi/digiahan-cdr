param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"

$source = Join-Path $RepositoryRoot "Source"
$logs = Join-Path $RepositoryRoot "Logs"
New-Item -ItemType Directory -Force -Path $logs | Out-Null

if (-not (Test-Path (Join-Path $source "DigiAhan.CDR.Receiver.csproj"))) {
    throw "Project not found at $source."
}

try {
    Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_.OwningProcess -gt 0) {
                Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
            }
        }
}
catch {}

Push-Location $source
try {
    & dotnet build --no-restore

    if ($LASTEXITCODE -ne 0) {
        & dotnet build
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}
finally {
    Pop-Location
}

$stdout = Join-Path $logs "dashboard-now-output.log"
$stderr = Join-Path $logs "dashboard-now-error.log"
Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue

$process = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList @("run","--no-build","--no-restore") `
    -WorkingDirectory $source `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -WindowStyle Hidden `
    -PassThru

for ($i=0; $i -lt 35; $i++) {
    Start-Sleep -Seconds 1

    try {
        $health = Invoke-RestMethod "http://localhost:5088/health" -TimeoutSec 3

        if ($health.status -eq "healthy") {
            Write-Host "Dashboard is running. PID=$($process.Id)" -ForegroundColor Green
            Write-Host "http://192.168.8.143:5088/dashboard" -ForegroundColor Yellow
            exit 0
        }
    }
    catch {}

    if ($process.HasExited) {
        break
    }
}

if (Test-Path $stdout) { Get-Content $stdout -Tail 50 }
if (Test-Path $stderr) { Get-Content $stderr -Tail 50 }
throw "Dashboard did not become healthy."
