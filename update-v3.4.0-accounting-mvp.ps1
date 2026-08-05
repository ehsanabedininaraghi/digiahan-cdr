$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$patch = Join-Path $repo "patch\Source"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path (Join-Path $repo ".git"))) { throw "Run this file from repository root." }
if (-not (Test-Path (Join-Path $source "Program.cs"))) { throw "Source\Program.cs not found." }

Write-Host "[1/7] Copy Accounting MVP files..." -ForegroundColor Cyan
Copy-Item (Join-Path $patch "Services\AccountingSyncService.cs") (Join-Path $source "Services\AccountingSyncService.cs") -Force
Copy-Item (Join-Path $patch "Models\AccountingModels.cs") (Join-Path $source "Models\AccountingModels.cs") -Force
Copy-Item (Join-Path $patch "Sql\AccountingSchema.sql") (Join-Path $source "Sql\AccountingSchema.sql") -Force
Copy-Item (Join-Path $patch "appsettings.Accounting.example.json") (Join-Path $source "appsettings.Accounting.example.json") -Force

Write-Host "[2/7] Patch Program.cs..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)

if (-not $program.Contains('appsettings.Accounting.local.json')) {
    $needle = 'var builder = WebApplication.CreateBuilder(args);'
    $replacement = @'
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    "appsettings.Accounting.local.json",
    optional: true,
    reloadOnChange: true);
'@
    $program = $program.Replace($needle, $replacement.TrimEnd())
}

if (-not $program.Contains('AddSingleton<AccountingSyncService>')) {
    $needle = 'builder.Services.AddSingleton<DashboardRepository>();'
    $program = $program.Replace($needle, $needle + "`r`nbuilder.Services.AddSingleton<AccountingSyncService>();")
}

if (-not $program.Contains('/api/accounting/sync')) {
    $needle = 'app.MapGet("/api/dashboard/sync", async (DashboardRepository repo, CancellationToken ct) => Results.Ok(await repo.Sync(ct)));'
    $addition = @'
app.MapGet("/api/dashboard/sync", async (DashboardRepository repo, CancellationToken ct) => Results.Ok(await repo.Sync(ct)));

app.MapGet("/api/accounting/status", async (
    AccountingSyncService service,
    CancellationToken ct) =>
    Results.Ok(await service.GetStatusAsync(ct)));

app.MapPost("/api/accounting/sync", async (
    int? days,
    AccountingSyncService service,
    CancellationToken ct) =>
{
    var result = await service.SyncAsync(days ?? 30, ct);
    return result.Status == "SUCCESS"
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status500InternalServerError);
});
'@
    $program = $program.Replace($needle, $addition.TrimEnd())
}

$program = $program.Replace('const string AppVersion = "3.3.1";', 'const string AppVersion = "3.4.0";')
$program = $program.Replace('const string AppVersion = "3.3.0";', 'const string AppVersion = "3.4.0";')
[IO.File]::WriteAllText($programPath, $program, $utf8)

Write-Host "[3/7] Patch project version and local config ignore..." -ForegroundColor Cyan
$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project, '<Version>[^<]+</Version>', '<Version>3.4.0</Version>')
$project = [regex]::Replace($project, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>3.4.0.0</AssemblyVersion>')
$project = [regex]::Replace($project, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>3.4.0.0</FileVersion>')
[IO.File]::WriteAllText($projectPath, $project, $utf8)

$gitIgnore = Join-Path $repo ".gitignore"


if (Test-Path $gitIgnore)
{
    $ignoreText = [IO.File]::ReadAllText($gitIgnore)
}
else
{
    $ignoreText = ""
}


if (-not $ignoreText.Contains("appsettings.Accounting.local.json")) {
    [IO.File]::AppendAllText($gitIgnore, "`r`n# Local accounting credentials`r`nSource/appsettings.Accounting.local.json`r`n", $utf8)
}

Write-Host "[4/7] Configure Accounting connection..." -ForegroundColor Cyan
$localConfig = Join-Path $source "appsettings.Accounting.local.json"
if (-not (Test-Path $localConfig)) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\configure-accounting.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Accounting configuration failed." }
}
else {
    Write-Host "Local Accounting config already exists; keeping it unchanged." -ForegroundColor Yellow
}

Write-Host "[5/7] Build without restore..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed. Nothing was pushed." }
}
finally { Pop-Location }

Write-Host "[6/7] Commit and push..." -ForegroundColor Cyan
& git -C $repo add `
    Source/Program.cs `
    Source/DigiAhan.CDR.Receiver.csproj `
    Source/Services/AccountingSyncService.cs `
    Source/Models/AccountingModels.cs `
    Source/Sql/AccountingSchema.sql `
    Source/appsettings.Accounting.example.json `
    scripts/configure-accounting.ps1 `
    .gitignore

& git -C $repo commit -m "Release v3.4.0 - Accounting Sync MVP"
if ($LASTEXITCODE -ne 0) {
    Write-Host "No commit was created. Review git status." -ForegroundColor Yellow
}

& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "Git push failed." }

Write-Host "[7/7] Start application..." -ForegroundColor Green
Set-Location $source
& dotnet run --no-build --no-restore
