param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [switch]$ResetDashboardPassword,
    [switch]$EnableJourneyPilot,
    [string[]]$JourneyPilotSellerKeys = @(),
    [switch]$EnableJourneyAutoCapture,
    [switch]$ValidatePackageOnly
)

$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "RUN-v4.3.11.ps1"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer engine is missing: $installer" }
& $installer @PSBoundParameters -ReleaseVersion "4.4.3"

