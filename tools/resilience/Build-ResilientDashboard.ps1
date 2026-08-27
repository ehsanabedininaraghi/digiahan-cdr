param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR4.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$sourceRoot = Join-Path $RepositoryRoot "Source"
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "DigiAhan.CDR.Receiver.csproj"))) {
    throw "DigiAhan project was not found under: $sourceRoot"
}

Push-Location $sourceRoot
try {
    & dotnet build "DigiAhan.CDR.Receiver.csproj" --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

Write-Host "Release build is ready for the watchdog." -ForegroundColor Green
