param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0",
    [switch]$ResetDashboardPassword
)

$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "RUN-v4.3.11.ps1"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer engine is missing: $installer" }
& $installer -RepositoryRoot $RepositoryRoot -ReleaseVersion "4.3.12" -ResetDashboardPassword:$ResetDashboardPassword
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
