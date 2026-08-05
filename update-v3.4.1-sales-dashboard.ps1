$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repo "Source"
$patch = Join-Path $repo "patch\Source"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path (Join-Path $repo ".git"))) { throw "Run from repository root." }
if (-not (Test-Path (Join-Path $source "Program.cs"))) { throw "Source\Program.cs not found." }

Write-Host "[1/8] Installing sales backend..." -ForegroundColor Cyan
Copy-Item (Join-Path $patch "Services\SalesDashboardRepository.cs") (Join-Path $source "Services\SalesDashboardRepository.cs") -Force
Copy-Item (Join-Path $patch "Models\SalesDashboardModels.cs") (Join-Path $source "Models\SalesDashboardModels.cs") -Force

Write-Host "[2/8] Patching Program.cs..." -ForegroundColor Cyan
$programPath = Join-Path $source "Program.cs"
$program = [IO.File]::ReadAllText($programPath)

$program = [regex]::Replace($program, 'const string AppVersion = "[^"]+";', 'const string AppVersion = "3.4.1";')
$program = [regex]::Replace($program, 'const string BuildDate = "[^"]+";', 'const string BuildDate = "2026-08-02";')

if (-not $program.Contains("AddSingleton<SalesDashboardRepository>")) {
    $anchor = "builder.Services.AddSingleton<DashboardRepository>();"
    $program = $program.Replace($anchor, $anchor + "`r`nbuilder.Services.AddSingleton<SalesDashboardRepository>();")
}

if (-not $program.Contains('/api/sales/summary')) {
    $anchor = 'app.MapGet("/api/dashboard/sync", async (DashboardRepository repo, CancellationToken ct) => Results.Ok(await repo.Sync(ct)));'
    $api = @'
app.MapGet("/api/dashboard/sync", async (DashboardRepository repo, CancellationToken ct) => Results.Ok(await repo.Sync(ct)));

app.MapGet("/api/sales/summary", async (
    SalesDashboardRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.Summary(ct)));

app.MapGet("/api/sales/by-visitor", async (
    SalesDashboardRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.ByVisitor(ct)));

app.MapGet("/api/sales/recent-invoices", async (
    int? take,
    SalesDashboardRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.RecentInvoices(take ?? 25, ct)));
'@
    if (-not $program.Contains($anchor)) { throw "Dashboard sync endpoint anchor not found in Program.cs." }
    $program = $program.Replace($anchor, $api.TrimEnd())
}
[IO.File]::WriteAllText($programPath, $program, $utf8)

Write-Host "[3/8] Patching dashboard HTML..." -ForegroundColor Cyan
$indexPath = Join-Path $source "wwwroot\dashboard\index.html"
$index = [IO.File]::ReadAllText($indexPath)
$index = [regex]::Replace($index, 'v3\.[0-9]+\.[0-9]+', 'v3.4.1')

if (-not $index.Contains('href="#sales"')) {
    $index = $index.Replace('<a href="#trend">گزارش زمانی</a>', '<a href="#trend">گزارش زمانی</a><a href="#sales">فروش</a>')
}

if (-not $index.Contains('id="accountingSyncText"')) {
    $needle = '</aside>'
    $box = @'
  <div class="sync accounting-sync"><span id="accountingSyncDot"></span><div><b id="accountingSyncText">در حال بررسی حسابداری</b><small id="accountingSyncTime">...</small></div></div>
</aside>
'@
    $index = $index.Replace($needle, $box.TrimEnd())
}

if (-not $index.Contains('id="sales"')) {
    $salesSection = @'
<section id="sales" class="panel sales-panel">
  <div class="panel-head">
    <div><h2>فروش ۳۰ روز اخیر</h2><p id="salesCaption">اطلاعات واقعی از حسابداری دفتر ۱۴۰۵</p></div>
  </div>

  <section class="cards sales-cards">
    <article><span>مبلغ فروش</span><strong id="salesTotal">۰</strong><small>ریال</small></article>
    <article><span>تعداد فاکتور</span><strong id="salesInvoices">۰</strong><small>فاکتور فروش</small></article>
    <article><span>مشتریان خریدار</span><strong id="salesCustomers">۰</strong><small>کد حسابداری یکتا</small></article>
    <article><span>میانگین فاکتور</span><strong id="salesAverage">۰</strong><small>ریال</small></article>
  </section>

  <div class="grid">
    <article class="panel">
      <div class="panel-head"><div><h2>فروش به تفکیک ویزیتور</h2><p>راستی‌آزمایی مستقل از حسابداری</p></div></div>
      <div class="table-wrap"><table>
        <thead><tr><th>ویزیتور</th><th>نقش</th><th>فاکتور</th><th>فروش</th><th>میانگین</th></tr></thead>
        <tbody id="salesVisitorRows"></tbody>
      </table></div>
    </article>
    <article class="panel wide">
      <div class="panel-head"><div><h2>آخرین فاکتورهای فروش</h2><p>۳۰ روز اخیر</p></div></div>
      <div class="table-wrap"><table>
        <thead><tr><th>تاریخ</th><th>شماره</th><th>مشتری</th><th>ویزیتور</th><th>اقلام</th><th>مبلغ</th></tr></thead>
        <tbody id="salesInvoiceRows"></tbody>
      </table></div>
    </article>
  </div>
</section>

'@
    $index = $index.Replace('<section id="calls" class="panel">', $salesSection + '<section id="calls" class="panel">')
}

$index = $index.Replace('Issabel + Didar + SQL Server', 'Issabel + Didar + Accounting + SQL Server')
[IO.File]::WriteAllText($indexPath, $index, $utf8)

Write-Host "[4/8] Patching dashboard JavaScript..." -ForegroundColor Cyan
$appPath = Join-Path $source "wwwroot\dashboard\app.js"
$app = [IO.File]::ReadAllText($appPath)

if (-not $app.Contains("salesSummary: get('/api/sales/summary')")) {
    $anchor = "version: get('/api/version')"
    $replacement = @"
version: get('/api/version'),
        salesSummary: get('/api/sales/summary'),
        salesVisitors: get('/api/sales/by-visitor'),
        salesInvoices: get('/api/sales/recent-invoices?take=25')
"@
    if (-not $app.Contains($anchor)) { throw "Version request anchor not found in app.js." }
    $app = $app.Replace($anchor, $replacement.TrimEnd())
}

if (-not $app.Contains("function money(")) {
    $anchor = "function sec(v) {"
    $money = @'
function money(v) {
    const n = Number(v || 0);
    if (Math.abs(n) >= 1e12) return `${fa((n / 1e12).toFixed(2))} تریلیون`;
    if (Math.abs(n) >= 1e9) return `${fa((n / 1e9).toFixed(2))} میلیارد`;
    if (Math.abs(n) >= 1e6) return `${fa((n / 1e6).toFixed(1))} میلیون`;
    return fa(Math.round(n));
}

function accountingRole(role, active) {
    if (!active) return 'غیرفعال';
    if (role === 'COLLECTIONS') return 'مطالبات / مالی';
    if (role === 'SHARED') return 'بین‌دفتری';
    return 'فروش';
}

function sec(v) {
'@
    $app = $app.Replace($anchor, $money.TrimEnd())
}

if (-not $app.Contains("if (data.salesSummary)")) {
    $anchor = "    if (failed.length) toast(`خطا در بخش: ${failed.join('، ')}`);"
    $render = @'
    if (data.salesSummary) {
        const s = data.salesSummary;
        $('salesTotal').textContent = money(s.totalSales);
        $('salesInvoices').textContent = fa(s.invoiceCount);
        $('salesCustomers').textContent = fa(s.customerCount);
        $('salesAverage').textContent = money(s.averageInvoice);
        $('salesCaption').textContent = `منبع: ${s.sourceDatabase} | سال مالی ${fa(s.fiscalYear)} | ${fa(s.invoiceCount)} فاکتور`;

        const ok = s.connected && s.lastSyncStatus === 'SUCCESS';
        $('accountingSyncDot').style.background = ok ? '#38d39f' : '#df5c61';
        $('accountingSyncText').textContent = ok ? 'حسابداری متصل' : 'حسابداری متصل نیست';
        $('accountingSyncTime').textContent = s.lastSyncAtUtc
            ? `آخرین همگام‌سازی: ${dt(s.lastSyncAtUtc)}`
            : 'همگام‌سازی موفق ثبت نشده';
    }

    if (data.salesVisitors) {
        $('salesVisitorRows').innerHTML = data.salesVisitors.length
            ? data.salesVisitors.map(x => `<tr class="${x.isActive ? '' : 'inactive-row'}">
                <td><b>${esc(x.visitorName)}</b></td>
                <td>${accountingRole(x.roleType, x.isActive)}</td>
                <td>${fa(x.invoiceCount)}</td>
                <td>${money(x.totalSales)}</td>
                <td>${money(x.averageInvoice)}</td>
              </tr>`).join('')
            : '<tr><td colspan="5">داده فروشنده‌ای وجود ندارد</td></tr>';
    }

    if (data.salesInvoices) {
        $('salesInvoiceRows').innerHTML = data.salesInvoices.length
            ? data.salesInvoices.map(x => `<tr>
                <td>${esc(x.factorDate || '—')}</td>
                <td>${x.factorNumber ? fa(x.factorNumber) : fa(x.factorCode)}</td>
                <td><b>${esc(x.customerName || 'بدون نام')}</b><small>${esc(x.customerDetailCode || '')}</small></td>
                <td>${esc(x.visitorName || 'نامشخص')}</td>
                <td>${fa(x.itemCount)}</td>
                <td><b>${money(x.amount)}</b></td>
              </tr>`).join('')
            : '<tr><td colspan="6">فاکتوری وجود ندارد</td></tr>';
    }

    if (failed.length) toast(`خطا در بخش: ${failed.join('، ')}`);
'@
    if (-not $app.Contains($anchor)) { throw "Failed-toast anchor not found in app.js." }
    $app = $app.Replace($anchor, $render.TrimEnd())
}
[IO.File]::WriteAllText($appPath, $app, $utf8)

Write-Host "[5/8] Patching styles..." -ForegroundColor Cyan
$stylePath = Join-Path $source "wwwroot\dashboard\style.css"
$style = [IO.File]::ReadAllText($stylePath)
if (-not $style.Contains(".accounting-sync")) {
    $style += @'

.accounting-sync{margin-top:10px}
.sales-panel{scroll-margin-top:20px}
.sales-cards{margin:0 0 18px}
.sales-cards article:first-child{border-top:3px solid var(--orange)}
.sales-cards strong{font-size:clamp(1.25rem,2vw,2rem)}
.inactive-row{opacity:.55}
#salesInvoiceRows td small{display:block;color:var(--muted);margin-top:4px}
'@
}
[IO.File]::WriteAllText($stylePath, $style, $utf8)

Write-Host "[6/8] Updating project version..." -ForegroundColor Cyan
$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$project = [IO.File]::ReadAllText($projectPath)
$project = [regex]::Replace($project, '<Version>[^<]+</Version>', '<Version>3.4.1</Version>')
$project = [regex]::Replace($project, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>3.4.1.0</AssemblyVersion>')
$project = [regex]::Replace($project, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>3.4.1.0</FileVersion>')
[IO.File]::WriteAllText($projectPath, $project, $utf8)

Write-Host "[7/8] Building..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed. Nothing was pushed." }
}
finally { Pop-Location }

Write-Host "[8/8] Commit, push and start..." -ForegroundColor Cyan
& git -C $repo add `
  Source/Program.cs `
  Source/DigiAhan.CDR.Receiver.csproj `
  Source/Services/SalesDashboardRepository.cs `
  Source/Models/SalesDashboardModels.cs `
  Source/wwwroot/dashboard/index.html `
  Source/wwwroot/dashboard/app.js `
  Source/wwwroot/dashboard/style.css

$changes = & git -C $repo status --porcelain
if (-not [string]::IsNullOrWhiteSpace(($changes -join ""))) {
    & git -C $repo commit -m "Release v3.4.1 - sales dashboard and accounting status"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
}
& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Set-Location $source
& dotnet run --no-build --no-restore
