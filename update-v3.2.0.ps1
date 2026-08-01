$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$patch = Join-Path $repo 'patch'

if (-not (Test-Path (Join-Path $repo '.git'))) {
    throw "این فایل باید از ریشه Repository اجرا شود. پوشه .git پیدا نشد: $repo"
}

Write-Host "[1/6] Checking repository..." -ForegroundColor Cyan
git -C $repo status --short

Write-Host "[2/6] Applying v3.2.0 files..." -ForegroundColor Cyan
Copy-Item (Join-Path $patch '*') $repo -Recurse -Force

Write-Host "[3/6] Building..." -ForegroundColor Cyan
Push-Location (Join-Path $repo 'Source')
try {
    dotnet clean
    dotnet build
}
finally {
    Pop-Location
}

Write-Host "[4/6] Creating commit..." -ForegroundColor Cyan
git -C $repo add Source/Sql/DashboardSync.sql Source/Models/DashboardModels.cs Source/Services/DashboardRepository.cs Source/wwwroot/dashboard/app.js Source/wwwroot/dashboard/index.html Source/Program.cs Source/DigiAhan.CDR.Receiver.csproj Docs/CHANGELOG.md
git -C $repo commit -m "Release v3.2.0 - real sync status"

Write-Host "[5/6] Pushing to GitHub..." -ForegroundColor Cyan
git -C $repo push origin main

Write-Host "[6/6] Starting application..." -ForegroundColor Cyan
Set-Location (Join-Path $repo 'Source')
dotnet run
