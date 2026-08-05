$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$patch = Join-Path $repo "windows\patch\Source"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$token = "7978bec550204b1ebcf7a4519441cefaf789e0923f47421ea6a69900dade3e0d"

if (-not (Test-Path (Join-Path $repo ".git"))) { throw "Repository root not found." }
if (-not (Test-Path (Join-Path $source "Program.cs"))) { throw "Source\Program.cs not found." }

Write-Host "[1/7] Copy Call Intelligence files..." -ForegroundColor Cyan
Copy-Item (Join-Path $patch "Models\CallIntelligenceModels.cs") (Join-Path $source "Models\CallIntelligenceModels.cs") -Force
Copy-Item (Join-Path $patch "Services\AgentEventStore.cs") (Join-Path $source "Services\AgentEventStore.cs") -Force
Copy-Item (Join-Path $patch "Services\CustomerIntelligenceRepository.cs") (Join-Path $source "Services\CustomerIntelligenceRepository.cs") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $source "wwwroot\agent") | Out-Null
Copy-Item (Join-Path $patch "wwwroot\agent\*") (Join-Path $source "wwwroot\agent") -Force

Write-Host "[2/7] Configure VoIP token..." -ForegroundColor Cyan
$configPath = Join-Path $source "appsettings.Voip.local.json"
$config = @{
  Voip = @{
    ApiToken = $token
  }
}
$config | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding UTF8

$gitIgnore = Join-Path $repo ".gitignore"
$ignore = if (Test-Path $gitIgnore) { [IO.File]::ReadAllText($gitIgnore) } else { "" }
if (-not $ignore.Contains("appsettings.Voip.local.json")) {
  [IO.File]::AppendAllText($gitIgnore, "`r`nSource/appsettings.Voip.local.json`r`n", $utf8)
}

Write-Host "[3/7] Patch Program.cs..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)
$program = [regex]::Replace($program, 'const string AppVersion = "[^"]+";', 'const string AppVersion = "3.5.0";')

if (-not $program.Contains('appsettings.Voip.local.json')) {
  $anchor='var builder = WebApplication.CreateBuilder(args);'
  $replacement=@'
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Voip.local.json", optional: true, reloadOnChange: true);
'@
  $program=$program.Replace($anchor,$replacement.TrimEnd())
}

if (-not $program.Contains('AddSingleton<AgentEventStore>')) {
  $anchor='builder.Services.AddSingleton<DashboardRepository>();'
  $program=$program.Replace($anchor,$anchor+"`r`nbuilder.Services.AddSingleton<AgentEventStore>();`r`nbuilder.Services.AddSingleton<CustomerIntelligenceRepository>();")
}

if (-not $program.Contains('/api/voip/events')) {
  $anchor='app.MapGet("/dashboard", () => Results.Redirect("/dashboard/index.html"));'
  $endpoints=@'
app.MapGet("/dashboard", () => Results.Redirect("/dashboard/index.html"));
app.MapGet("/agent/{extension}", (string extension) =>
    Results.Redirect($"/agent/index.html?extension={Uri.EscapeDataString(extension)}"));

app.MapPost("/api/voip/events", async (
    HttpRequest http,
    VoipRingEventRequest request,
    IConfiguration configuration,
    CustomerIntelligenceRepository repository,
    AgentEventStore store,
    CancellationToken ct) =>
{
    var expected = configuration["Voip:ApiToken"];
    var supplied = http.Headers["X-Voip-Token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(expected) || supplied != expected)
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.Extension) ||
        string.IsNullOrWhiteSpace(request.CallerNumber))
        return Results.BadRequest(new { error = "Extension and CallerNumber are required." });

    var card = await repository.BuildCard(request, ct);
    return Results.Ok(store.Put(request.Extension.Trim(), card));
});

app.MapGet("/api/agent/{extension}/current", (
    string extension,
    AgentEventStore store) =>
{
    var current = store.Get(extension.Trim());
    return current is null ? Results.NoContent() : Results.Ok(current);
});
'@
  if (-not $program.Contains($anchor)) { throw "Dashboard route anchor not found." }
  $program=$program.Replace($anchor,$endpoints.TrimEnd())
}

[IO.File]::WriteAllText($programPath,$program,$utf8)

Write-Host "[4/7] Update project version..." -ForegroundColor Cyan
$projectPath=Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project=[IO.File]::ReadAllText($projectPath)
$project=[regex]::Replace($project,'<Version>[^<]+</Version>','<Version>3.5.0</Version>')
$project=[regex]::Replace($project,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>3.5.0.0</AssemblyVersion>')
$project=[regex]::Replace($project,'<FileVersion>[^<]+</FileVersion>','<FileVersion>3.5.0.0</FileVersion>')
[IO.File]::WriteAllText($projectPath,$project,$utf8)

Write-Host "[5/7] Build..." -ForegroundColor Cyan
Push-Location $source
try {
  & dotnet build --no-restore
  if ($LASTEXITCODE -ne 0) { throw "Build failed. Nothing was pushed." }
} finally { Pop-Location }

Write-Host "[6/7] Commit and push..." -ForegroundColor Cyan
& git -C $repo add Source/Program.cs Source/DigiAhan.CDR.Receiver.csproj Source/Models/CallIntelligenceModels.cs Source/Services/AgentEventStore.cs Source/Services/CustomerIntelligenceRepository.cs Source/wwwroot/agent .gitignore
$changes=& git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
  & git -C $repo commit -m "Release v3.5.0 - Call Intelligence agent popup"
  if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
}
& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Write-Host "[7/7] Starting application..." -ForegroundColor Green
Write-Host "Agent panel example: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Set-Location $source
& dotnet run --no-build --no-restore
