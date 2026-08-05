$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$target = Join-Path $repo "Source\appsettings.Accounting.local.json"

Write-Host "DigiAhan Accounting MVP configuration" -ForegroundColor Cyan
Write-Host "This file stays local and must not be committed." -ForegroundColor Yellow

$server = Read-Host "SQL server [COREI5]"
if ([string]::IsNullOrWhiteSpace($server)) { $server = "COREI5" }

$database = Read-Host "Accounting database [daftar1405]"
if ([string]::IsNullOrWhiteSpace($database)) { $database = "daftar1405" }

$fiscalYearText = Read-Host "Fiscal year [1405]"
if ([string]::IsNullOrWhiteSpace($fiscalYearText)) { $fiscalYearText = "1405" }
$fiscalYear = [int]$fiscalYearText

$mode = Read-Host "Authentication: 1=Windows, 2=SQL username/password [1]"
if ([string]::IsNullOrWhiteSpace($mode)) { $mode = "1" }

if ($mode -eq "2") {
    $user = Read-Host "Read-only SQL username"
    $securePassword = Read-Host "Password" -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

    $connectionString = "Server=$server;Database=$database;User Id=$user;Password=$password;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;"
}
else {
    $connectionString = "Server=$server;Database=$database;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;"
}

$config = @{
    ConnectionStrings = @{
        AccountingLegacy = $connectionString
    }
    Accounting = @{
        Server = $server
        Database = $database
        FiscalYear = $fiscalYear
    }
}

$config | ConvertTo-Json -Depth 5 | Set-Content -Path $target -Encoding UTF8
Write-Host "Created: $target" -ForegroundColor Green
Write-Host "Do not send this file to anyone because it may contain the SQL password." -ForegroundColor Yellow
