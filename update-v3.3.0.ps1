$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$patch = Join-Path $repo "patch\Source"
$backup = Join-Path $repo ("backup-v3.3.0-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
if (-not (Test-Path (Join-Path $repo ".git"))) { throw "Run from repository root." }
if (-not (Test-Path (Join-Path $source "DigiAhan.CDR.Receiver.csproj"))) { throw "Project not found." }
New-Item -ItemType Directory -Force -Path $backup | Out-Null
$files = @("Program.cs","wwwroot\dashboard\index.html","wwwroot\dashboard\app.js","Sql\DashboardCallsCount.sql","Sql\DashboardCallsPage.sql")
foreach ($f in $files) { $s=Join-Path $source $f; if(Test-Path $s){$d=Join-Path $backup $f; New-Item -ItemType Directory -Force -Path (Split-Path -Parent $d)|Out-Null; Copy-Item $s $d -Force}}
Copy-Item (Join-Path $patch "Sql\DashboardCallsCount.sql") (Join-Path $source "Sql\DashboardCallsCount.sql") -Force
Copy-Item (Join-Path $patch "Sql\DashboardCallsPage.sql") (Join-Path $source "Sql\DashboardCallsPage.sql") -Force
$enc=New-Object System.Text.UTF8Encoding($false)
$pp=Join-Path $source "Program.cs"; $p=[IO.File]::ReadAllText($pp); $p=$p.Replace('const string AppVersion = "3.2.0";','const string AppVersion = "3.3.0";'); [IO.File]::WriteAllText($pp,$p,$enc)
$ip=Join-Path $source "wwwroot\dashboard\index.html"; $i=[IO.File]::ReadAllText($ip); $i=$i.Replace('v3.2.0','v3.3.0'); $i=$i.Replace('<h2>جزئیات تماس‌ها</h2>','<h2>تماس‌های یکتا و قابل پیگیری</h2>'); if(-not $i.Contains('id="callPager"')){$i=$i.Replace('</tbody></table></div>\n</section>\n\n<footer>','</tbody></table></div><div id="callPager" class="pager"><button id="prevPage">قبلی</button><span id="pageInfo">صفحه ۱</span><button id="nextPage">بعدی</button></div>\n</section>\n\n<footer>')} [IO.File]::WriteAllText($ip,$i,$enc)
$ap=Join-Path $source "wwwroot\dashboard\app.js"; $a=[IO.File]::ReadAllText($ap); if(-not $a.Contains('let currentPage = 1;')){$a=$a.Replace('let timer;','let timer;`nlet currentPage = 1;`nconst callPageSize = 50;')}; $a=$a.Replace('pageSize=100','page=${currentPage}&pageSize=${callPageSize}'); $a=$a.Replace("fa(calls.total) + ' ردیف CDR'","fa(calls.total) + ' تماس یکتا'"); if(-not $a.Contains('const totalPages = Math.max(1')){$a=$a.Replace("        $('callRows').innerHTML = calls.items.length","        const totalPages = Math.max(1, Math.ceil(calls.total / calls.pageSize));`n        $('pageInfo').textContent = `صفحه ${fa(calls.page)} از ${fa(totalPages)} | ${fa(calls.total)} تماس`;`n        $('prevPage').disabled = calls.page <= 1;`n        $('nextPage').disabled = calls.page >= totalPages;`n`n        $('callRows').innerHTML = calls.items.length")}; $a=$a.Replace("$('refresh').onclick = load;","$('refresh').onclick = () => { currentPage = 1; load(); };"); $a=$a.Replace("$('extension').onchange = load;","$('extension').onchange = () => { currentPage = 1; load(); };"); $a=$a.Replace("$('status').onchange = load;","$('status').onchange = () => { currentPage = 1; load(); };`n$('prevPage').onclick = () => { if (currentPage > 1) { currentPage--; load(); } };`n$('nextPage').onclick = () => { currentPage++; load(); };"); $a=$a.Replace('timer = setTimeout(load, 450);','timer = setTimeout(() => { currentPage = 1; load(); }, 450);'); [IO.File]::WriteAllText($ap,$a,$enc)
Push-Location $source
try { & dotnet build --no-restore; if($LASTEXITCODE -ne 0){throw "Build failed. No commit created."} } finally { Pop-Location }
& git -C $repo add Source/Program.cs Source/wwwroot/dashboard/index.html Source/wwwroot/dashboard/app.js Source/Sql/DashboardCallsCount.sql Source/Sql/DashboardCallsPage.sql
& git -C $repo commit -m "Release v3.3.0 - unique calls and pagination"
if($LASTEXITCODE -ne 0){throw "git commit failed."}
& git -C $repo push origin main
if($LASTEXITCODE -ne 0){throw "git push failed."}
Set-Location $source
& dotnet run --no-build --no-restore
