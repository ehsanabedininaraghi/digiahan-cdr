$ErrorActionPreference = "Stop"

$repo = "D:\DigiAhan\CDR3.1.0git"
$source = Join-Path $repo "Source"
$programPath = Join-Path $source "Program.cs"
$appPath = Join-Path $source "wwwroot\agent\app.js"
$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $programPath)) { throw "Program.cs not found: $programPath" }
if (-not (Test-Path $appPath)) { throw "app.js not found: $appPath" }

Write-Host "[1/5] Stopping old app..." -ForegroundColor Cyan
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "[2/5] Fixing Program.cs route..." -ForegroundColor Cyan
$program = [IO.File]::ReadAllText($programPath)

# Remove every old /agent/{extension} redirect route.
$program = [regex]::Replace(
    $program,
    '(?ms)app\.MapGet\("/agent/\{extension(?::int)?\}".*?;\s*',
    ''
)

# Add the correct route immediately before the first /api route or dashboard route.
$route = @'
app.MapGet("/agent/{extension:int}", (int extension) =>
    Results.Redirect($"/agent/index.html?extension={extension}"));

'@

if ($program.Contains('app.MapGet("/dashboard"')) {
    $program = $program.Replace('app.MapGet("/dashboard"', $route + 'app.MapGet("/dashboard"')
} elseif ($program.Contains('app.MapGet("/api/')) {
    $program = $program.Replace('app.MapGet("/api/', $route + 'app.MapGet("/api/')
} else {
    throw "Could not find insertion point in Program.cs."
}

$program = [regex]::Replace(
    $program,
    'const string AppVersion = "[^"]+";',
    'const string AppVersion = "3.5.2";'
)

[IO.File]::WriteAllText($programPath, $program, $utf8)

Write-Host "[3/5] Replacing agent extension detection..." -ForegroundColor Cyan
$app = [IO.File]::ReadAllText($appPath)

$oldPattern = "const\s+extension\s*=.*?;"
$newBlock = @'
const queryExtension = new URLSearchParams(location.search).get('extension');
const pathPart = location.pathname.split('/').filter(Boolean).pop();
const extension = /^\d{3}$/.test(queryExtension || '')
    ? queryExtension
    : (/^\d{3}$/.test(pathPart || '') ? pathPart : '201');
'@

if ([regex]::IsMatch($app, $oldPattern)) {
    $app = [regex]::Replace($app, $oldPattern, $newBlock.TrimEnd(), 1)
} elseif (-not $app.Contains("const queryExtension")) {
    $app = $newBlock + "`r`n" + $app
}

[IO.File]::WriteAllText($appPath, $app, $utf8)

Write-Host "[4/5] Updating version and building..." -ForegroundColor Cyan
if (Test-Path $projectPath) {
    $project = [IO.File]::ReadAllText($projectPath)
    $project = [regex]::Replace($project, '<Version>[^<]+</Version>', '<Version>3.5.2</Version>')
    $project = [regex]::Replace($project, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>3.5.2.0</AssemblyVersion>')
    $project = [regex]::Replace($project, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>3.5.2.0</FileVersion>')
    [IO.File]::WriteAllText($projectPath, $project, $utf8)
}

Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
finally {
    Pop-Location
}

Write-Host "[5/5] Starting fixed app..." -ForegroundColor Green
Write-Host "Open: http://192.168.8.143:5088/agent/201" -ForegroundColor Yellow
Set-Location $source
& dotnet run --no-build --no-restore
