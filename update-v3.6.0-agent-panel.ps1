$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$patch = Join-Path $repo "patch\Source"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $repo "_backups\v3.6.0-$stamp"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path (Join-Path $repo ".git"))) { throw "Repository root not found." }
if (-not (Test-Path (Join-Path $source "Program.cs"))) { throw "Source\Program.cs not found." }

Write-Host "[1/8] Stopping previous application..." -ForegroundColor Cyan
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "[2/8] Backup current version..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backup | Out-Null
Copy-Item (Join-Path $source "Program.cs") $backup -Force
Copy-Item (Join-Path $source "DigiAhan.CDR.Receiver.csproj") $backup -Force
if (Test-Path (Join-Path $source "wwwroot\agent")) {
    Copy-Item (Join-Path $source "wwwroot\agent") (Join-Path $backup "agent") -Recurse -Force
}
foreach ($file in @(
    "Models\CallIntelligenceModels.cs",
    "Models\AgentPanelModels.cs",
    "Services\CustomerIntelligenceRepository.cs",
    "Services\AgentPanelRepository.cs"
)) {
    $full = Join-Path $source $file
    if (Test-Path $full) {
        $targetDir = Join-Path $backup (Split-Path $file)
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        Copy-Item $full $targetDir -Force
    }
}

Write-Host "[3/8] Installing Agent Panel v3.6.0..." -ForegroundColor Cyan
Copy-Item (Join-Path $patch "Models\CallIntelligenceModels.cs") (Join-Path $source "Models\CallIntelligenceModels.cs") -Force
Copy-Item (Join-Path $patch "Models\AgentPanelModels.cs") (Join-Path $source "Models\AgentPanelModels.cs") -Force
Copy-Item (Join-Path $patch "Services\CustomerIntelligenceRepository.cs") (Join-Path $source "Services\CustomerIntelligenceRepository.cs") -Force
Copy-Item (Join-Path $patch "Services\AgentPanelRepository.cs") (Join-Path $source "Services\AgentPanelRepository.cs") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $source "wwwroot\agent") | Out-Null
Copy-Item (Join-Path $patch "wwwroot\agent\*") (Join-Path $source "wwwroot\agent") -Force

Write-Host "[4/8] Patching Program.cs..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)

$program = [regex]::Replace($program, 'const string AppVersion = "[^"]+";', 'const string AppVersion = "3.6.0";')
$program = [regex]::Replace($program, 'const string BuildDate = "[^"]+";', 'const string BuildDate = "2026-08-03";')

$route = @'
app.MapGet("/agent/{extension:int}", (int extension) =>
    Results.Redirect($"/agent/index.html?extension={extension}"));
'@

$program = [regex]::Replace(
    $program,
    '(?ms)app\.MapGet\("/agent/\{extension(?::int)?\}".*?;\s*',
    ''
)

$dashboardRoute = 'app.MapGet("/dashboard", () => Results.Redirect("/dashboard/index.html"));'
if (-not $program.Contains($dashboardRoute)) { throw "Dashboard route not found." }
$program = $program.Replace($dashboardRoute, $dashboardRoute + "`r`n" + $route.TrimEnd())

if (-not $program.Contains("AddSingleton<AgentPanelRepository>")) {
    $anchor = "builder.Services.AddSingleton<CustomerIntelligenceRepository>();"
    if (-not $program.Contains($anchor)) { throw "CustomerIntelligenceRepository registration not found." }
    $program = $program.Replace($anchor, $anchor + "`r`nbuilder.Services.AddSingleton<AgentPanelRepository>();")
}

if (-not $program.Contains("AgentPanelRepository panelRepository")) {
    $program = [regex]::Replace(
        $program,
        'AgentEventStore store,\s*CancellationToken ct\) =>',
        "AgentEventStore store,`r`n    AgentPanelRepository panelRepository,`r`n    CancellationToken ct) =>",
        1
    )
}

if (-not $program.Contains("RecordIncoming(card")) {
    $program = [regex]::Replace(
        $program,
        'var card = await repository\.BuildCard\(request, ct\);\s*return Results\.Ok\(store\.Put\(request\.Extension\.Trim\(\), card\)\);',
        "var card = await repository.BuildCard(request, ct);`r`n    await panelRepository.RecordIncoming(card, ct);`r`n    return Results.Ok(store.Put(request.Extension.Trim(), card));",
        1
    )
}

if (-not $program.Contains('app.MapPost("/api/agent/outcomes"')) {
    $anchor = 'app.MapGet("/api/dashboard/summary"'
    if (-not $program.Contains($anchor)) { throw "Dashboard summary route anchor not found." }

    $endpoints = @'
app.MapPost("/api/agent/outcomes", async (
    AgentOutcomeRequest request,
    AgentPanelRepository repository,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Extension) ||
        string.IsNullOrWhiteSpace(request.CallerNumber) ||
        string.IsNullOrWhiteSpace(request.Outcome))
        return Results.BadRequest(new { error = "Extension, CallerNumber and Outcome are required." });

    return Results.Ok(await repository.SaveOutcome(request, ct));
});

app.MapGet("/api/agent/history", async (
    string? extensions,
    int? take,
    AgentPanelRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.RecentIncoming(extensions ?? "201", take ?? 20, ct)));

app.MapGet("/api/agent/outcomes", async (
    string? extensions,
    int? take,
    AgentPanelRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.RecentOutcomes(extensions ?? "201", take ?? 15, ct)));

app.MapGet("/api/agent/stats", async (
    string? extensions,
    AgentPanelRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.Stats(extensions ?? "201", ct)));

'@
    $program = $program.Replace($anchor, $endpoints + $anchor)
}

[IO.File]::WriteAllText($programPath, $program, $utf8)

Write-Host "[5/8] Updating project version..." -ForegroundColor Cyan
$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project, '<Version>[^<]+</Version>', '<Version>3.6.0</Version>')
$project = [regex]::Replace($project, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>3.6.0.0</AssemblyVersion>')
$project = [regex]::Replace($project, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>3.6.0.0</FileVersion>')
[IO.File]::WriteAllText($projectPath, $project, $utf8)

Write-Host "[6/8] Building and validating..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed. Restoring backup..." -ForegroundColor Red
        Copy-Item (Join-Path $backup "Program.cs") $programPath -Force
        Copy-Item (Join-Path $backup "DigiAhan.CDR.Receiver.csproj") $projectPath -Force
        if (Test-Path (Join-Path $backup "agent")) {
            Remove-Item (Join-Path $source "wwwroot\agent") -Recurse -Force
            Copy-Item (Join-Path $backup "agent") (Join-Path $source "wwwroot\agent") -Recurse -Force
        }
        throw "Build failed and previous Program/UI version was restored."
    }
}
finally {
    Pop-Location
}

Write-Host "[7/8] Commit and push..." -ForegroundColor Cyan
& git -C $repo add `
    Source/Program.cs `
    Source/DigiAhan.CDR.Receiver.csproj `
    Source/Models/CallIntelligenceModels.cs `
    Source/Models/AgentPanelModels.cs `
    Source/Services/CustomerIntelligenceRepository.cs `
    Source/Services/AgentPanelRepository.cs `
    Source/wwwroot/agent

$changes = & git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
    & git -C $repo commit -m "Release v3.6.0 - practical sales agent panel"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
}
& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Write-Host "[8/8] Starting v3.6.0..." -ForegroundColor Green
Write-Host "Panel: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Write-Host "Backup: $backup" -ForegroundColor DarkGray
Set-Location $source
& dotnet run --no-build --no-restore
