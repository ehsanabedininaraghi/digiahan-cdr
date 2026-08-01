param(
    [string]$RepoName = "digiahan-cdr-receiver",
    [string]$Description = "DigiAhan CDR Receiver, Didar integration and call-center dashboard",
    [switch]$Public
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git نصب نیست. ابتدا Git for Windows را نصب کنید."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host "GitHub CLI نصب نیست؛ در حال نصب..." -ForegroundColor Yellow
        winget install --id GitHub.cli --exact --source winget
        $env:Path += ";C:\Program Files\GitHub CLI"
    } else {
        throw "GitHub CLI نصب نیست و winget هم موجود نیست. GitHub CLI را نصب کنید."
    }
}

try {
    gh auth status | Out-Null
} catch {
    Write-Host "یک بار وارد GitHub شوید." -ForegroundColor Yellow
    gh auth login --web --git-protocol https
}

if (-not (Test-Path "Source\appsettings.json")) {
    Copy-Item "Source\appsettings.example.json" "Source\appsettings.json"
    Write-Host "Source\appsettings.json ساخته شد. قبل از اجرا توکن و Connection String را وارد کنید." -ForegroundColor Yellow
}

if (-not (Test-Path ".git")) {
    git init
}

git checkout -B main
git add .
git commit -m "Initial release v3.1.0" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Commit جدیدی لازم نبود یا Git identity تنظیم نیست." -ForegroundColor Yellow
    if (-not (git config user.email)) { git config user.email "ehsan@digiahan.local" }
    if (-not (git config user.name)) { git config user.name "DigiAhan" }
    git add .
    git commit -m "Initial release v3.1.0"
}

$visibility = if ($Public) { "--public" } else { "--private" }
$existing = gh repo view $RepoName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Repository از قبل وجود دارد: $RepoName" -ForegroundColor Yellow
    if (-not (git remote get-url origin 2>$null)) {
        $login = gh api user --jq .login
        git remote add origin "https://github.com/$login/$RepoName.git"
    }
    git push -u origin main
} else {
    gh repo create $RepoName $visibility --description $Description --source . --remote origin --push
}

Write-Host "`nتمام شد. Repository ساخته و Push شد." -ForegroundColor Green
gh repo view --web
