param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$ExpectedVersion = "4.4.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$zipFull = [IO.Path]::GetFullPath($ZipPath)
if (-not (Test-Path -LiteralPath $zipFull -PathType Leaf)) { throw "Release ZIP was not found: $zipFull" }

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$extractRoot = Join-Path $tempBase ("DigiAhan-Release-Test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $extractRoot | Out-Null

try {
    Expand-Archive -LiteralPath $zipFull -DestinationPath $extractRoot -Force
    $packageRoot = Get-ChildItem -LiteralPath $extractRoot -Directory | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName "RUN-v4.4.0.ps1") -PathType Leaf
    } | Select-Object -First 1
    if ($null -eq $packageRoot) {
        if (Test-Path -LiteralPath (Join-Path $extractRoot "RUN-v4.4.0.ps1") -PathType Leaf) {
            $packagePath = $extractRoot
        }
        else { throw "ZIP does not contain the v4.4.0 package root." }
    }
    else { $packagePath = $packageRoot.FullName }

    $manifestPath = Join-Path $packagePath "MANIFEST-SHA256.txt"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "SHA-256 manifest is missing." }
    $manifestEntries = @{}
    foreach ($line in Get-Content -LiteralPath $manifestPath -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw "Invalid manifest line: $line" }
        $relative = $matches[2]
        if ($manifestEntries.ContainsKey($relative)) { throw "Duplicate manifest entry: $relative" }
        $manifestEntries[$relative] = $matches[1].ToUpperInvariant()
        $file = [IO.Path]::GetFullPath((Join-Path $packagePath $relative))
        if (-not $file.StartsWith($packagePath.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe manifest path: $relative"
        }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Manifest file is missing: $relative" }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne $manifestEntries[$relative]) { throw "Hash mismatch: $relative" }
    }

    $files = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object {
        $_.FullName -ne $manifestPath
    })
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($packagePath.TrimEnd('\').Length).TrimStart('\')
        if (-not $manifestEntries.ContainsKey($relative)) { throw "File is not covered by manifest: $relative" }
    }
    if ($files.Count -ne $manifestEntries.Count) {
        throw "Manifest coverage count mismatch. Files=$($files.Count), Entries=$($manifestEntries.Count)."
    }

    $forbidden = @($files | Where-Object {
        $relative = $_.FullName.Substring($packagePath.TrimEnd('\').Length).TrimStart('\')
        $relative -match '(^|\\)(bin|obj|Logs|_backups|\.git)(\\|$)' -or
        $relative -match '(?i)appsettings\.(Development|[^\\]+\.local)\.json$' -or
        $relative -match '(?i)(^|\\)Source\\appsettings\.json$' -or
        $relative -match '(?i)mappingfile\.xlsx$' -or
        $relative -match '(?i)\.(bak|trn|user|suo|log)$'
    })
    if ($forbidden.Count -gt 0) {
        throw "Forbidden machine-specific or generated files exist in release: $($forbidden.FullName -join ', ')"
    }

    & (Join-Path $packagePath "RUN-v4.4.0.ps1") -RepositoryRoot (Join-Path $extractRoot "unused") -ValidatePackageOnly

    $buildInfo = Get-Content -LiteralPath (Join-Path $packagePath "BUILD-INFO.txt") -Raw -Encoding UTF8
    if ($buildInfo -notmatch ('(?m)^Version=' + [regex]::Escape($ExpectedVersion) + '\r?$')) {
        throw "BUILD-INFO version does not match $ExpectedVersion."
    }
    Write-Host "PASS: ZIP extraction, manifest hashes, coverage and forbidden-file checks." -ForegroundColor Green
    Write-Host "PASS: installer validate-only mode for v$ExpectedVersion." -ForegroundColor Green
}
finally {
    $extractFull = [IO.Path]::GetFullPath($extractRoot).TrimEnd('\')
    if (-not $extractFull.StartsWith($tempBase + '\DigiAhan-Release-Test-',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe temporary cleanup target: $extractFull"
    }
    if (Test-Path -LiteralPath $extractFull) { Remove-Item -LiteralPath $extractFull -Recurse -Force }
}
