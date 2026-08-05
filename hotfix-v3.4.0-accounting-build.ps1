$ErrorActionPreference = "Stop"

$repo = "D:\DigiAhan\CDR3.1.0git"
$source = Join-Path $repo "Source"
$file = Join-Path $source "Services\AccountingSyncService.cs"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $file)) {
    throw "AccountingSyncService.cs not found: $file"
}

Write-Host "[1/5] Fixing ItemRow naming conflict..." -ForegroundColor Cyan

$text = [IO.File]::ReadAllText($file)

# Fix the record property that had the same name as its enclosing record type.
$text = $text.Replace(
    "private sealed record ItemRow(`r`n        int ItemCode, int FactorCode, string? FactorDate, int? ItemRow,",
    "private sealed record ItemRow(`r`n        int ItemCode, int FactorCode, string? FactorDate, int? RowNumber,"
)

$text = $text.Replace(
    "private sealed record ItemRow(`n        int ItemCode, int FactorCode, string? FactorDate, int? ItemRow,",
    "private sealed record ItemRow(`n        int ItemCode, int FactorCode, string? FactorDate, int? RowNumber,"
)

$text = $text.Replace("item.ItemRow)", "item.RowNumber)")
$text = $text.Replace("item.ItemRow,", "item.RowNumber,")

[IO.File]::WriteAllText($file, $text, $utf8)

Write-Host "[2/5] Verifying fix..." -ForegroundColor Cyan

$check = [IO.File]::ReadAllText($file)
if ($check.Contains("int? ItemRow,")) {
    throw "The conflicting ItemRow property still exists."
}
if (-not $check.Contains("int? RowNumber,")) {
    throw "RowNumber property was not created."
}

Write-Host "[3/5] Building without restore..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed. Nothing was committed or pushed."
    }
}
finally {
    Pop-Location
}

Write-Host "[4/5] Commit and push..." -ForegroundColor Cyan
& git -C $repo add Source/Services/AccountingSyncService.cs

$changes = & git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
    & git -C $repo commit -m "Fix v3.4.0 accounting ItemRow naming conflict"
    if ($LASTEXITCODE -ne 0) {
        throw "git commit failed."
    }
} else {
    Write-Host "No new changes to commit." -ForegroundColor Yellow
}

& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) {
    throw "git push failed."
}

Write-Host "[5/5] Starting application..." -ForegroundColor Green
Set-Location $source
& dotnet run --no-build --no-restore
