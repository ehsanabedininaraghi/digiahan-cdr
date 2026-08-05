$ErrorActionPreference = "Stop"

$repo = "D:\DigiAhan\CDR3.1.0git"
$source = Join-Path $repo "Source"
$programPath = Join-Path $source "Program.cs"
$appPath = Join-Path $source "wwwroot\agent\app.js"
$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $programPath)) { throw "Program.cs not found." }
if (-not (Test-Path $appPath)) { throw "agent app.js not found." }

Write-Host "[1/5] Fixing agent route..." -ForegroundColor Cyan
$program = [IO.File]::ReadAllText($programPath)

# Remove the broad string route which also captured /agent/index.html.
$program = [regex]::Replace(
    $program,
    'app\.MapGet\("/agent/\{extension\}",\s*\(string extension\)\s*=>\s*Results\.Redirect\(\$"/agent/index\.html\?extension=\{Uri\.EscapeDataString\(extension\)\}"\)\);',
    'app.MapGet("/agent/{extension:int}", (int extension) =>`r`n    Results.Redirect($"/agent/index.html?extension={extension}"));'
)

# Repair an already malformed or duplicated route block more defensively.
$program = $program.Replace(
    'app.MapGet("/agent/{extension}", (string extension) =>' + "`r`n" +
    '    Results.Redirect($"/agent/index.html?extension={Uri.EscapeDataString(extension)}"));',
    'app.MapGet("/agent/{extension:int}", (int extension) =>' + "`r`n" +
    '    Results.Redirect($"/agent/index.html?extension={extension}"));'
)

$program = [regex]::Replace($program, 'const string AppVersion = "[^"]+";', 'const string AppVersion = "3.5.1";')
[IO.File]::WriteAllText($programPath, $program, $utf8)

Write-Host "[2/5] Making extension detection strict..." -ForegroundColor Cyan
$app = [IO.File]::ReadAllText($appPath)
$app = [regex]::Replace(
    $app,
    "const extension=params\.get\('extension'\)\|\|location\.pathname\.split\('/'\)\.filter\(Boolean\)\.pop\(\)\|\|'201';",
    "const queryExtension=params.get('extension');`r`nconst pathPart=location.pathname.split('/').filter(Boolean).pop();`r`nconst extension=/^\\d{3}$/.test(queryExtension||'')?queryExtension:(/^\\d{3}$/.test(pathPart||'')?pathPart:'201');"
)
[IO.File]::WriteAllText($appPath, $app, $utf8)

Write-Host "[3/5] Updating project version..." -ForegroundColor Cyan
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project, '<Version>[^<]+</Version>', '<Version>3.5.1</Version>')
$project = [regex]::Replace($project, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>3.5.1.0</AssemblyVersion>')
$project = [regex]::Replace($project, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>3.5.1.0</FileVersion>')
[IO.File]::WriteAllText($projectPath, $project, $utf8)

Write-Host "[4/5] Build..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed. Nothing was pushed." }
}
finally { Pop-Location }

Write-Host "[5/5] Commit, push and start..." -ForegroundColor Cyan
& git -C $repo add Source/Program.cs Source/wwwroot/agent/app.js Source/DigiAhan.CDR.Receiver.csproj
$changes = & git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
    & git -C $repo commit -m "Release v3.5.1 - fix agent routing"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
}
& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Set-Location $source
Write-Host "Correct URL: http://192.168.8.143:5088/agent/201" -ForegroundColor Green
& dotnet run --no-build --no-restore
