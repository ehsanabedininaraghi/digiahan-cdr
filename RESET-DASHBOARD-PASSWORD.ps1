param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot "Source")
)

$ErrorActionPreference = "Stop"
$configPath = Join-Path $SourceRoot "appsettings.Dashboard.local.json"

$first = Read-Host "New dashboard password" -AsSecureString
$second = Read-Host "Repeat new dashboard password" -AsSecureString
$firstText = [System.Net.NetworkCredential]::new("", $first).Password
$secondText = [System.Net.NetworkCredential]::new("", $second).Password

if ([string]::IsNullOrWhiteSpace($firstText)) {
    throw "Password cannot be empty."
}
if ($firstText.Length -lt 8) {
    throw "Password must contain at least 8 characters."
}
if ($firstText -cne $secondText) {
    throw "Passwords do not match."
}

$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($firstText)
    $hash = ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
}
finally {
    $sha.Dispose()
    $firstText = $null
    $secondText = $null
}

$config = [ordered]@{
    DashboardAccess = [ordered]@{
        PasswordHash = $hash
    }
}
$config | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $configPath -Encoding UTF8

Write-Host "Dashboard password reset successfully." -ForegroundColor Green
Write-Host "Open: http://192.168.8.143:5088/dashboard-login/index.html" -ForegroundColor Cyan

