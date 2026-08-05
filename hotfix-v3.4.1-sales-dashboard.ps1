$ErrorActionPreference = "Stop"

$repo = "D:\DigiAhan\CDR3.1.0git"
$source = Join-Path $repo "Source"
$appPath = Join-Path $source "wwwroot\dashboard\app.js"
$programPath = Join-Path $source "Program.cs"
$indexPath = Join-Path $source "wwwroot\dashboard\index.html"
$projectPath = Join-Path $source "DigiAhan.CDR.Receiver.csproj"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $appPath)) { throw "app.js not found: $appPath" }

Write-Host "[1/6] Repairing app.js with a robust anchor..." -ForegroundColor Cyan
$app = [IO.File]::ReadAllText($appPath)

# Ensure sales API requests exist.
if (-not $app.Contains("salesSummary: get('/api/sales/summary')")) {
    $versionPattern = "version:\s*get\('/api/version'\)"
    if (-not [regex]::IsMatch($app, $versionPattern)) {
        throw "Version request entry not found in app.js."
    }

    $app = [regex]::Replace(
        $app,
        $versionPattern,
        "version: get('/api/version'),`r`n        salesSummary: get('/api/sales/summary'),`r`n        salesVisitors: get('/api/sales/by-visitor'),`r`n        salesInvoices: get('/api/sales/recent-invoices?take=25')",
        1
    )
}

# Ensure money and role helpers exist.
if (-not $app.Contains("function money(")) {
    $secIndex = $app.IndexOf("function sec(")
    if ($secIndex -lt 0) { throw "function sec() not found in app.js." }

    $helpers = @'
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

'@
    $app = $app.Insert($secIndex, $helpers)
}

# Insert sales rendering before function toast(), regardless of the exact failed-toast line.
if (-not $app.Contains("if (data.salesSummary)")) {
    $toastIndex = $app.IndexOf("function toast(")
    if ($toastIndex -lt 0) { throw "function toast() not found in app.js." }

    # Find the closing brace of load() immediately before function toast().
    $prefix = $app.Substring(0, $toastIndex)
    $loadClose = $prefix.LastIndexOf("}")
    if ($loadClose -lt 0) { throw "Could not locate load() closing brace." }

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

'@
    $app = $app.Insert($loadClose, $render)
}

[IO.File]::WriteAllText($appPath, $app, $utf8)

Write-Host "[2/6] Ensuring version 3.4.1..." -ForegroundColor Cyan
if (Test-Path $programPath) {
    $program = [IO.File]::ReadAllText($programPath)
    $program = [regex]::Replace($program, 'const string AppVersion = "[^"]+";', 'const string AppVersion = "3.4.1";')
    [IO.File]::WriteAllText($programPath, $program, $utf8)
}
if (Test-Path $indexPath) {
    $index = [IO.File]::ReadAllText($indexPath)
    $index = [regex]::Replace($index, 'v3\.[0-9]+\.[0-9]+', 'v3.4.1')
    [IO.File]::WriteAllText($indexPath, $index, $utf8)
}
if (Test-Path $projectPath) {
    $project = [IO.File]::ReadAllText($projectPath)
    $project = [regex]::Replace($project, '<Version>[^<]+</Version>', '<Version>3.4.1</Version>')
    $project = [regex]::Replace($project, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>3.4.1.0</AssemblyVersion>')
    $project = [regex]::Replace($project, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>3.4.1.0</FileVersion>')
    [IO.File]::WriteAllText($projectPath, $project, $utf8)
}

Write-Host "[3/6] JavaScript syntax check..." -ForegroundColor Cyan
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node --check $appPath
    if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax check failed." }
} else {
    Write-Host "Node.js not installed; syntax check skipped." -ForegroundColor Yellow
}

Write-Host "[4/6] Build..." -ForegroundColor Cyan
Push-Location $source
try {
    & dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed. Nothing was pushed." }
}
finally { Pop-Location }

Write-Host "[5/6] Commit and push..." -ForegroundColor Cyan
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
    & git -C $repo commit -m "Hotfix v3.4.1 - robust sales dashboard patch"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
} else {
    Write-Host "No new changes to commit." -ForegroundColor Yellow
}

& git -C $repo push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Write-Host "[6/6] Starting application..." -ForegroundColor Green
Set-Location $source
& dotnet run --no-build --no-restore
