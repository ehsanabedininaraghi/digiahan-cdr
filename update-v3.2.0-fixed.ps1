$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$patch = Join-Path $repo 'patch'
$source = Join-Path $repo 'Source'

if (-not (Test-Path (Join-Path $repo '.git'))) {
    throw "Run this script from the repository root. Missing .git folder: $repo"
}

if (-not (Test-Path $patch)) {
    throw "Missing patch folder: $patch"
}

if (-not (Test-Path (Join-Path $source 'DigiAhan.CDR.Receiver.csproj'))) {
    throw "Project file was not found under Source."
}

Write-Host "[1/6] Checking repository..." -ForegroundColor Cyan
& git -C $repo status --short
if ($LASTEXITCODE -ne 0) {
    throw "Git status failed."
}

Write-Host "[2/6] Applying v3.2.0 files..." -ForegroundColor Cyan
Copy-Item -Path (Join-Path $patch '*') -Destination $repo -Recurse -Force

Write-Host "[3/6] Building..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet clean
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed." }

    & dotnet build
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}
finally {
    Pop-Location
}

Write-Host "[4/6] Creating commit..." -ForegroundColor Cyan
& git -C $repo add --all
if ($LASTEXITCODE -ne 0) {
    throw "git add failed."
}

$changes = & git -C $repo status --porcelain
if ([string]::IsNullOrWhiteSpace(($changes -join ""))) {
    Write-Host "No new changes to commit." -ForegroundColor Yellow
}
else {
    & git -C $repo commit -m "Release v3.2.0 - real sync status"
    if ($LASTEXITCODE -ne 0) {
        throw "git commit failed."
    }
}

Write-Host "[5/6] Pushing to GitHub..." -ForegroundColor Cyan
& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) {
    throw "git push failed."
}

Write-Host "[6/6] Starting application..." -ForegroundColor Cyan
Set-Location $source
& dotnet run
