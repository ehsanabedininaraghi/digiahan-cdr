param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [string[]]$PilotSellerKeys = @(),
    [switch]$Enable,
    [switch]$EnableAutoCapture
)

$ErrorActionPreference = "Stop"
$sourceRoot = Join-Path $RepositoryRoot "Source"
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Source directory was not found: $sourceRoot"
}

$cleanKeys = @($PilotSellerKeys | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
if ($Enable -and $cleanKeys.Count -eq 0) {
    throw "At least one PilotSellerKeys value is required for the first pilot."
}

$configuration = [ordered]@{
    JourneyKernel = [ordered]@{
        Enabled = [bool]$Enable
        AutoCaptureSellerInteractions = [bool]($Enable -and $EnableAutoCapture)
        DefaultLeadSlaMinutes = 120
        DefaultFollowUpMinutes = 1440
        PilotSellerKeys = $cleanKeys
    }
}

$path = Join-Path $sourceRoot "appsettings.JourneyKernel.local.json"
$configuration | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8

Write-Host "Journey pilot configuration saved safely." -ForegroundColor Green
Write-Host "Enabled: $([bool]$Enable) | Auto capture: $([bool]($Enable -and $EnableAutoCapture))" -ForegroundColor Cyan
Write-Host "Pilot sellers: $($cleanKeys -join ', ')" -ForegroundColor Cyan
Write-Host "Restart is required for the feature flag to take effect." -ForegroundColor Yellow
