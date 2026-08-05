$ErrorActionPreference = "Stop"

$repo = "D:\DigiAhan\CDR3.1.0git"
$appPath = Join-Path $repo "Source\wwwroot\dashboard\app.js"
$programPath = Join-Path $repo "Source\Program.cs"
$indexPath = Join-Path $repo "Source\wwwroot\dashboard\index.html"

if (-not (Test-Path $appPath)) { throw "app.js not found." }

$utf8 = New-Object System.Text.UTF8Encoding($false)

$app = [IO.File]::ReadAllText($appPath)

$app = $app.Replace("let timer;``nlet currentPage = 1;``nconst callPageSize = 50;", "let timer;`r`nlet currentPage = 1;`r`nconst callPageSize = 50;")

if (-not $app.Contains("const totalPages = Math.max(1")) {
    $needle = "        $('callCount').textContent = fa(calls.total) + ' تماس یکتا';"
    $insert = @"
        $('callCount').textContent = fa(calls.total) + ' تماس یکتا';
        const totalPages = Math.max(1, Math.ceil(calls.total / calls.pageSize));
        if (currentPage > totalPages) currentPage = totalPages;
        $('pageInfo').textContent = `صفحه `${fa(calls.page)} از `${fa(totalPages)} | `${fa(calls.total)} تماس`;
        $('prevPage').disabled = calls.page <= 1;
        $('nextPage').disabled = calls.page >= totalPages;
"@
    $app = $app.Replace($needle, $insert.TrimEnd())
}

$app = $app.Replace("$('refresh').onclick = load;", "$('refresh').onclick = () => { currentPage = 1; load(); };")
$app = $app.Replace("$('extension').onchange = load;", "$('extension').onchange = () => { currentPage = 1; load(); };")
$app = $app.Replace("$('status').onchange = load;", "$('status').onchange = () => { currentPage = 1; load(); };")

if (-not $app.Contains("$('prevPage').onclick")) {
    $anchor = "$('status').onchange = () => { currentPage = 1; load(); };"
    $handlers = @"
$('status').onchange = () => { currentPage = 1; load(); };
$('prevPage').onclick = () => {
    if (currentPage > 1) {
        currentPage--;
        load();
    }
};
$('nextPage').onclick = () => {
    currentPage++;
    load();
};
"@
    $app = $app.Replace($anchor, $handlers.TrimEnd())
}

[IO.File]::WriteAllText($appPath, $app, $utf8)

if (Test-Path $programPath) {
    $program = [IO.File]::ReadAllText($programPath)
    $program = $program.Replace('const string AppVersion = "3.3.0";', 'const string AppVersion = "3.3.1";')
    [IO.File]::WriteAllText($programPath, $program, $utf8)
}
if (Test-Path $indexPath) {
    $index = [IO.File]::ReadAllText($indexPath)
    $index = $index.Replace("v3.3.0", "v3.3.1")
    [IO.File]::WriteAllText($indexPath, $index, $utf8)
}

Write-Host "[1/4] JavaScript syntax check..." -ForegroundColor Cyan
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node --check $appPath
    if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax check failed." }
} else {
    Write-Host "Node.js not installed; skipping node --check." -ForegroundColor Yellow
}

Write-Host "[2/4] Build..." -ForegroundColor Cyan
Push-Location (Join-Path $repo "Source")
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
finally {
    Pop-Location
}

Write-Host "[3/4] Commit and push..." -ForegroundColor Cyan
& git -C $repo add Source/wwwroot/dashboard/app.js Source/wwwroot/dashboard/index.html Source/Program.cs
& git -C $repo commit -m "Hotfix v3.3.1 - fix dashboard JavaScript and pagination"
if ($LASTEXITCODE -ne 0) {
    Write-Host "No new commit created or commit failed. Check git status." -ForegroundColor Yellow
}
& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Write-Host "[4/4] Start application..." -ForegroundColor Green
Set-Location (Join-Path $repo "Source")
& dotnet run --no-build --no-restore
