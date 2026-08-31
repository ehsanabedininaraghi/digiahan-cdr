param([string]$ConfigPath = (Join-Path $PSScriptRoot "resilience.config.json"))

$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
try {
    $health = Invoke-RestMethod -Uri $config.HealthUrl -TimeoutSec 5
    Write-Host "Dashboard: HEALTHY" -ForegroundColor Green
    Write-Host "Address:   $($config.DashboardUrl)"
    $health | ConvertTo-Json -Depth 8
    exit 0
}
catch {
    Write-Host "Dashboard: UNAVAILABLE" -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}
