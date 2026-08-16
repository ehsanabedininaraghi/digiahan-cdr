param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [switch]$ResetDashboardPassword,
    [switch]$EnableJourneyPilot,
    [string[]]$JourneyPilotSellerKeys = @(),
    [switch]$EnableJourneyAutoCapture
)

$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "RUN-v4.3.11.ps1"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer engine is missing: $installer" }

$parameters = @{
    RepositoryRoot = $RepositoryRoot
    ReleaseVersion = "4.4.0"
    ResetDashboardPassword = $ResetDashboardPassword
    EnableJourneyPilot = $EnableJourneyPilot
    JourneyPilotSellerKeys = @($JourneyPilotSellerKeys)
    EnableJourneyAutoCapture = $EnableJourneyAutoCapture
}
& $installer @parameters
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
