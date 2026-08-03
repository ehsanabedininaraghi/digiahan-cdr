param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [int]$EveryMinutes = 15,
    [int]$Days = 60
)

$ErrorActionPreference = "Stop"

$EveryMinutes = [Math]::Max(5,[Math]::Min(120,$EveryMinutes))
$Days = [Math]::Max(7,[Math]::Min(365,$Days))

$bridge = Join-Path $RepositoryRoot "tools\accounting-bridge-v3.7.2.ps1"
if (-not (Test-Path $bridge)) {
    throw "Accounting bridge not found: $bridge"
}

$taskName = "DigiAhan Accounting Bridge v3.7.2"
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$bridge`" -RepositoryRoot `"$RepositoryRoot`" -Days $Days"
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments

$trigger = New-ScheduledTaskTrigger `
    -Once `
    -At ((Get-Date).AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
    -MultipleInstances IgnoreNew

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "Direct ADODB accounting sync for DigiAhan v3.7.2." `
    -Force | Out-Null

Write-Host "Scheduled task installed: $taskName" -ForegroundColor Green
Write-Host "Runs every $EveryMinutes minutes while $currentUser is logged in." -ForegroundColor Cyan
