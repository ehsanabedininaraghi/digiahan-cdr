param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$runRoot = Join-Path $RepositoryRoot "Logs\Runs"
if (-not (Test-Path $runRoot)) {
    throw "Logs\Runs folder was not found: $runRoot"
}

$sourceRun = Get-ChildItem $runRoot -Directory -Filter "v4.0.0-*" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $sourceRun) {
    throw "No v4.0.0 run folder was found."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $runRoot "v4.0.1-logpack-$stamp"
$zipPath = "$work.zip"

New-Item -ItemType Directory -Force -Path $work | Out-Null

function Copy-OpenFileSafe {
    param(
        [string]$Source,
        [string]$Destination
    )

    $input = $null
    $output = $null

    try {
        $input = [System.IO.File]::Open(
            $Source,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        )

        $output = [System.IO.File]::Open(
            $Destination,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None
        )

        $input.CopyTo($output)
    }
    finally {
        if ($output) { $output.Dispose() }
        if ($input) { $input.Dispose() }
    }
}

$copied = @()
$failed = @()

Get-ChildItem $sourceRun.FullName -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        $destination = Join-Path $work $_.Name

        try {
            Copy-OpenFileSafe -Source $_.FullName -Destination $destination
            $copied += $_.Name
        }
        catch {
            $failed += "$($_.Name): $($_.Exception.Message)"
        }
    }

# Also collect latest top-level MVP reports.
Get-ChildItem (Join-Path $RepositoryRoot "Logs") -File -Filter "MVP-v4.0.0-*" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 4 |
    ForEach-Object {
        $destination = Join-Path $work $_.Name
        try {
            Copy-OpenFileSafe -Source $_.FullName -Destination $destination
            $copied += $_.Name
        }
        catch {
            $failed += "$($_.Name): $($_.Exception.Message)"
        }
    }

$summary = @()
$summary += "DigiAhan CDR v4.0.1 Log Pack"
$summary += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
$summary += "Source run: $($sourceRun.FullName)"
$summary += ""
$summary += "Copied files:"
$summary += ($copied | Sort-Object -Unique | ForEach-Object { "- $_" })
$summary += ""
$summary += "Copy failures:"
if ($failed.Count -eq 0) {
    $summary += "- none"
} else {
    $summary += ($failed | ForEach-Object { "- $_" })
}

$summary | Set-Content (Join-Path $work "logpack-summary.txt") -Encoding UTF8

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $work "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Log ZIP created successfully:" -ForegroundColor Green
Write-Host $zipPath -ForegroundColor Yellow
Write-Host ""
Write-Host "The dashboard was not stopped." -ForegroundColor DarkGray
