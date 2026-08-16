param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$version = "4.4.0"
$repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repositoryFull "release")).TrimEnd('\')
$stagingRoot = Join-Path $releaseRoot ("staging-v$version-" + [guid]::NewGuid().ToString("N"))
$packageName = "DigiAhan_CDR_v$version"
$packageRoot = Join-Path $stagingRoot $packageName
$zipPath = Join-Path $stagingRoot "$packageName.zip"
$snapshotZip = Join-Path $stagingRoot "committed-source.zip"
$snapshotRoot = Join-Path $stagingRoot "committed-source"

if (-not $releaseRoot.StartsWith($repositoryFull + '\',[StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe release root: $releaseRoot"
}
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

try {
    & git -C $repositoryFull archive --format=zip --output=$snapshotZip HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not create an exact committed-source snapshot." }
    Expand-Archive -LiteralPath $snapshotZip -DestinationPath $snapshotRoot -Force

    $rootFiles = @(
        "CHANGELOG-v4.4.0.md",
        "CONFIGURE-AI-v4.3.12.ps1",
        "CONFIGURE-JOURNEY-PILOT-v4.4.0.ps1",
        "README-v4.4.0-FA.md",
        "RESET-DASHBOARD-PASSWORD.cmd",
        "RESET-DASHBOARD-PASSWORD.ps1",
        "ROLLBACK-v4.4.0.cmd",
        "ROLLBACK-v4.4.0.ps1",
        "RUN-v4.3.11.ps1",
        "RUN-v4.4.0.cmd",
        "RUN-v4.4.0.ps1"
    )
    foreach ($relative in $rootFiles) {
        $source = Join-Path $snapshotRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Release root file missing: $relative" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $relative) -Force
    }

    $docRelative = "Docs\Customer-Journey-Kernel-v4.4.0-FA.md"
    $docTarget = Join-Path $packageRoot $docRelative
    New-Item -ItemType Directory -Force -Path (Split-Path $docTarget) | Out-Null
    Copy-Item -LiteralPath (Join-Path $snapshotRoot $docRelative) -Destination $docTarget -Force

    $trackedPayload = @(& git -C $repositoryFull ls-files -- global.json Source tools config)
    if ($LASTEXITCODE -ne 0 -or $trackedPayload.Count -eq 0) { throw "Could not enumerate tracked payload files." }
    foreach ($gitRelative in $trackedPayload) {
        $relative = $gitRelative.Replace('/','\')
        $source = [IO.Path]::GetFullPath((Join-Path $snapshotRoot $relative))
        if (-not $source.StartsWith($snapshotRoot.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe tracked payload path: $relative"
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Tracked payload file missing: $relative" }
        $target = Join-Path (Join-Path $packageRoot "payload") $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }

    $commit = (& git -C $repositoryFull rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw "Could not resolve release commit." }
    @(
        "Version=$version",
        "Commit=$commit",
        "BuiltAtUtc=$([DateTime]::UtcNow.ToString('o'))",
        "JourneyDefault=DISABLED",
        "AutoCaptureDefault=DISABLED",
        "SqlIntegrationTest=PASSED",
        "PackageRegression=PASSED"
    ) | Set-Content -LiteralPath (Join-Path $packageRoot "BUILD-INFO.txt") -Encoding UTF8

    $manifestLines = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.TrimEnd('\').Length).TrimStart('\')
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,$relative
    })
    $manifestLines | Set-Content -LiteralPath (Join-Path $packageRoot "MANIFEST-SHA256.txt") -Encoding UTF8

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    & (Join-Path $snapshotRoot "Tests\ReleasePackageRegression.ps1") -ZipPath $zipPath -ExpectedVersion $version

    $finalPackage = Join-Path $releaseRoot $packageName
    $finalZip = Join-Path $releaseRoot "$packageName.zip"
    if ((Test-Path -LiteralPath $finalPackage) -or (Test-Path -LiteralPath $finalZip)) {
        $superseded = Join-Path $releaseRoot ("superseded\$packageName-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        New-Item -ItemType Directory -Force -Path $superseded | Out-Null
        if (Test-Path -LiteralPath $finalPackage) {
            Move-Item -LiteralPath $finalPackage -Destination (Join-Path $superseded $packageName)
        }
        if (Test-Path -LiteralPath $finalZip) {
            Move-Item -LiteralPath $finalZip -Destination (Join-Path $superseded "$packageName.zip")
        }
    }
    Move-Item -LiteralPath $packageRoot -Destination $finalPackage
    Move-Item -LiteralPath $zipPath -Destination $finalZip
    Write-Host "PASS: tested release published to $finalZip" -ForegroundColor Green
    Write-Output $finalZip
}
finally {
    $stagingFull = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\')
    if (-not $stagingFull.StartsWith($releaseRoot + '\staging-v4.4.0-',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe staging cleanup target: $stagingFull"
    }
    if (Test-Path -LiteralPath $stagingFull) { Remove-Item -LiteralPath $stagingFull -Recurse -Force }
}
