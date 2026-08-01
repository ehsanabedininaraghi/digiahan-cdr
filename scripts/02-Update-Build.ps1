param(
    [string]$Branch = "main"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

git fetch origin
git checkout $Branch
git pull --ff-only origin $Branch

Set-Location "$root\Source"
if (-not (Test-Path "appsettings.json")) {
    Copy-Item "appsettings.example.json" "appsettings.json"
    throw "appsettings.json ساخته شد. تنظیمات محرمانه را وارد کنید و اسکریپت را دوباره اجرا کنید."
}

dotnet clean
dotnet restore
dotnet build -c Release
dotnet publish -c Release -o "$root\runtime\publish-new"
Write-Host "Build آماده است: $root\runtime\publish-new" -ForegroundColor Green
