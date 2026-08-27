const $ = id => document.getElementById(id);
const fa = n => new Intl.NumberFormat('fa-IR').format(n || 0);
let timer;
let currentPage = 1;
const callPageSize = 50;
const requestTimings = {};
let loadController = null;
let loadSequence = 0;

const extensionNames = {
  "201":"مجید","202":"مجید","203":"شافوری","204":"شافوری","205":"ایلیا","206":"ایلیا",
  "207":"تقی‌زاده","208":"تقی‌زاده","211":"حسنی","212":"حسنی","213":"فراهانی","214":"فراهانی",
  "215":"حسنا","216":"حسنا","217":"فتحی","218":"فتحی","219":"زمانی","220":"زمانی",
  "223":"پویا","224":"پویا","225":"عابدینی","226":"عابدینی","400":"داخلی ۴۰۰","401":"داخلی ۴۰۱"
};

function localIso(d) {
    const year = d.getFullYear();
    const month = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
}

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
    v = Number(v || 0);
    const h = Math.floor(v / 3600);
    const m = Math.floor((v % 3600) / 60);
    const s = v % 60;
    if (h) return `${fa(h)} ساعت و ${fa(m)} دقیقه`;
    return m ? `${fa(m)} دقیقه و ${fa(s)} ثانیه` : `${fa(s)} ثانیه`;
}

function dt(v) {
    if (!v) return '—';
    const text = String(v);
    const utcValue = /(?:Z|[+-]\d{2}:\d{2})$/i.test(text) ? text : `${text}Z`;
    return new Date(utcValue).toLocaleString('fa-IR-u-ca-persian', { hour12: false, timeZone: 'Asia/Tehran' });
}

function dateOnly(v) {
    if (!v) return '—';
    const value = /^\d{4}-\d{2}-\d{2}$/.test(v) ? `${v}T12:00:00` : v;
    return new Date(value).toLocaleDateString('fa-IR-u-ca-persian', { timeZone: 'Asia/Tehran' });
}

function esc(v) {
    return String(v ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#039;');
}

async function get(url, signal) {
    const started = performance.now();
    const response = await fetch(url, { cache: 'no-store', signal });
    requestTimings[url.split('?')[0]] = Math.round(performance.now() - started);
    if (!response.ok) throw new Error(await response.text());
    return response.json();
}

function formatJalaliDate(date) {
    const parts = new Intl.DateTimeFormat('fa-IR-u-ca-persian-nu-latn', {
        year: 'numeric', month: '2-digit', day: '2-digit', timeZone: 'Asia/Tehran'
    }).formatToParts(date);
    const value = type => parts.find(x => x.type === type)?.value;
    return `${value('year')}/${value('month')}/${value('day')}`;
}

function jalaliToLocalDate(year, month, day) {
    const target = `${year}/${String(month).padStart(2, '0')}/${String(day).padStart(2, '0')}`;
    const start = new Date(year + 621, 1, 20, 12, 0, 0);
    for (let i = 0; i < 410; i++) {
        const candidate = new Date(start);
        candidate.setDate(start.getDate() + i);
        if (formatJalaliDate(candidate) === target) return candidate;
    }
    return null;
}

function showReportProgress(completed, total, text = 'در حال دریافت اطلاعات گزارش…') {
    const percent = Math.round(completed * 100 / Math.max(1, total));
    $('reportProgress').classList.remove('hidden');
    $('reportProgressText').textContent = text;
    $('reportProgressPercent').textContent = `${fa(percent)}٪`;
    $('reportProgressBar').style.width = `${percent}%`;
}

function hideReportProgress() {
    $('reportProgress').classList.add('hidden');
}

function customerCell(call) {
    if (call.isNewCustomer) {
        return `<div class="customer-cell"><span class="badge customer-new">مشتری جدید</span><small>${esc(call.customerPhone || '')}</small></div>`;
    }
    if (call.customerName) {
        const company = call.companyName && call.companyName !== call.customerName
            ? `<small>${esc(call.companyName)}</small>` : '';
        return `<div class="customer-cell"><b>${esc(call.customerName)}</b>${company}</div>`;
    }
    return '<span class="muted">تماس داخلی / بدون شماره خارجی</span>';
}

function setPeriod(period) {
    const today = new Date();
    let start = new Date(today);
    let end = new Date(today);

    if (period === 'week') {
        const daysSinceSaturday = (today.getDay() + 1) % 7;
        start.setDate(today.getDate() - daysSinceSaturday);
    } else if (period === 'month') {
        const parts = formatJalaliDate(today).split('/').map(Number);
        start = jalaliToLocalDate(parts[0], parts[1], 1) || new Date(today.getFullYear(), today.getMonth(), 1);
    } else if (period === 'custom') {
        document.querySelectorAll('.period').forEach(x => x.classList.toggle('active', x.dataset.period === period));
        return;
    }

    $('startDate').value = localIso(start);
    $('endDate').value = localIso(end);
    document.querySelectorAll('.period').forEach(x => x.classList.toggle('active', x.dataset.period === period));
    load();
}

function queryBase() {
    const startDate = $('startDate').value;
    const endDate = $('endDate').value;
    const extension = $('extension').value;
    return `startDate=${encodeURIComponent(startDate)}&endDate=${encodeURIComponent(endDate)}&extension=${encodeURIComponent(extension)}`;
}

async function load() {
    const sequence = ++loadSequence;
    if (loadController) loadController.abort();
    loadController = new AbortController();
    const signal = loadController.signal;
    const base = queryBase();
    const requests = {
        summary: `/api/dashboard/summary?${base}`,
        hourly: `/api/dashboard/hourly?${base}`,
        daily: `/api/dashboard/daily?${base}`,
        extensions: `/api/dashboard/extensions?startDate=${encodeURIComponent($('startDate').value)}&endDate=${encodeURIComponent($('endDate').value)}`,
        calls: `/api/dashboard/calls?${base}&search=${encodeURIComponent($('search').value)}&status=${$('status').value}&page=${currentPage}&pageSize=${callPageSize}`,
        sync: '/api/dashboard/sync',
        version: '/api/version',
        salesSummary: `/api/sales/summary?${base}`,
        salesVisitors: `/api/sales/by-visitor?${base}`,
        salesInvoices: `/api/sales/recent-invoices?${base}&take=25`,
        sellerPerformance: `/api/dashboard/seller-performance?${base}`,
        systemHealth: '/api/system/health'
    };

    $('rangeCaption').textContent = `گزارش از ${dateOnly($('startDate').value)} تا ${dateOnly($('endDate').value)}${$('extension').value !== 'all' ? ` برای داخلی ${$('extension').value}` : ''}`;

    const entries = Object.entries(requests);
    let completed = 0;
    showReportProgress(0, entries.length);
    const results = await Promise.allSettled(entries.map(([, url]) =>
        get(url, signal).finally(() => {
            completed++;
            if (sequence === loadSequence) showReportProgress(completed, entries.length,
                completed === entries.length ? 'در حال نمایش نتیجه…' : 'در حال دریافت اطلاعات گزارش…');
        })));
    if (sequence !== loadSequence) return;
    const keys = entries.map(([key]) => key);
    const data = {};
    const failed = [];

    results.forEach((result, index) => {
        if (result.status === 'fulfilled') data[keys[index]] = result.value;
        else {
            failed.push(keys[index]);
            console.error(`Dashboard section failed: ${keys[index]}`, result.reason);
        }
    });

    if (data.version) {
        $('versionBadge').textContent = `v${data.version.version}`;
        $('footerVersion').textContent = `v${data.version.version}`;
    }

    if (data.summary) {
        const s = data.summary;
        $('total').textContent = fa(s.totalCalls);
        $('answered').textContent = fa(s.answeredCalls);
        $('missed').textContent = fa(s.missedCalls);
        $('inbound').textContent = fa(s.inboundCalls);
        $('outbound').textContent = fa(s.outboundCalls);
        $('knownCustomers').textContent = fa(s.knownCustomerCalls);
        $('newCustomers').textContent = fa(s.newCustomerCalls);
        $('talk').textContent = sec(s.totalTalkSeconds);
        $('avg').textContent = 'میانگین ' + sec(s.averageTalkSeconds);
        const rate = s.totalCalls ? Math.round(s.answeredCalls * 100 / s.totalCalls) : 0;
        $('answerRate').textContent = fa(rate) + '٪ نرخ پاسخ';
        $('donutValue').textContent = fa(rate) + '٪';
        $('donut').style.background = `conic-gradient(var(--blue) 0 ${rate * 3.6}deg,var(--orange) ${rate * 3.6}deg 360deg)`;
    }

    if (data.hourly) {
        const map = Object.fromEntries(data.hourly.map(x => [x.hour, x]));
        const max = Math.max(1, ...data.hourly.map(x => x.total));
        $('chart').innerHTML = Array.from({ length: 24 }, (_, i) => {
            const x = map[i] || { answered: 0, missed: 0, total: 0 };
            return `<div class="bar-wrap" title="${i}:00 | کل ${x.total}"><div class="bars"><i class="bar ok" style="height:${x.answered / max * 100}%"></i><i class="bar bad" style="height:${x.missed / max * 100}%"></i></div><small>${fa(i)}</small></div>`;
        }).join('');
    }

    if (data.daily) {
        $('dailyRows').innerHTML = data.daily.length
            ? data.daily.map(x => `<tr><td>${dateOnly(x.date)}</td><td>${fa(x.total)}</td><td>${fa(x.answered)}</td><td>${fa(x.missed)}</td><td>${fa(x.inbound)}</td><td>${fa(x.outbound)}</td><td>${fa(x.newCustomers)}</td><td>${fa(x.knownCustomers)}</td><td>${sec(x.talkSeconds)}</td></tr>`).join('')
            : '<tr><td colspan="9">داده‌ای در این بازه وجود ندارد</td></tr>';
    }

    if (data.extensions) {
        $('extRows').innerHTML = data.extensions.length
            ? data.extensions.map(x => `<tr><td><b>${esc(x.extension)}</b></td><td>${esc(extensionNames[x.extension] || '—')}</td><td>${fa(x.total)}</td><td><b class="direction-in">${fa(x.inbound)}</b></td><td><b class="direction-out">${fa(x.outbound)}</b></td><td>${fa(x.answered)}</td><td>${fa(x.missed)}</td><td>${sec(x.talkSeconds)}</td><td>${sec(x.averageTalkSeconds)}</td></tr>`).join('')
            : '<tr><td colspan="9">داده‌ای نیست</td></tr>';
    }

    if (data.calls) {
        const calls = data.calls;
        $('callCount').textContent = fa(calls.total) + ' تماس یکتا';
        $('callRows').innerHTML = calls.items.length
            ? calls.items.map(x => `<tr class="${x.isNewCustomer ? 'new-customer-row' : ''}">
                <td>${dt(x.calldate)}</td><td><b>${esc(x.src || '—')}</b></td><td>${esc(x.dst || '—')}</td>
                <td>${customerCell(x)}</td><td>${x.companyName ? esc(x.companyName) : '—'}</td>
                <td>${x.direction === 'inbound' ? 'ورودی' : x.direction === 'outbound' ? 'خروجی' : 'داخلی/نامشخص'}</td>
                <td><span class="badge ${(x.disposition === 'ANSWERED' || x.billsec > 0) ? 'ok' : 'bad'}">${(x.disposition === 'ANSWERED' || x.billsec > 0) ? 'پاسخ' : 'بی‌پاسخ'}</span></td>
                <td>${sec(x.duration)}</td><td>${sec(x.billsec)}</td>
                <td>${x.recordingFile ? '<span class="record" title="مسیر ضبط ثبت شده">●</span>' : '—'}</td>
            </tr>`).join('')
            : '<tr><td colspan="10">داده‌ای یافت نشد</td></tr>';
    } else {
        $('callCount').textContent = 'خطا در دریافت تماس‌ها';
        $('callRows').innerHTML = '<tr><td colspan="10">بخش تماس‌ها موقتاً در دسترس نیست.</td></tr>';
    }

    if (data.sync) {
        const sync = data.sync;
        const cdrDate = sync.lastCdrAt ? new Date(sync.lastCdrAt) : null;
        const receivedDate = sync.lastReceivedAtUtc ? new Date(sync.lastReceivedAtUtc) : null;
        const ageMinutes = cdrDate ? Math.max(0, (Date.now() - cdrDate.getTime()) / 60000) : Number.POSITIVE_INFINITY;
        const state = ageMinutes <= 5 ? 'good' : ageMinutes <= 15 ? 'warn' : 'bad';

        $('syncDot').style.background = state === 'good' ? '#38d39f' : state === 'warn' ? '#f0b44d' : '#df5c61';
        $('syncText').textContent = state === 'good' ? 'دریافت تماس فعال' : state === 'warn' ? 'دریافت تماس با تأخیر' : 'دریافت تماس متوقف یا قدیمی';

        const cdrText = cdrDate ? `آخرین تماس: ${dt(sync.lastCdrAt)}` : 'تماسی ثبت نشده';
        const sqlText = receivedDate ? `آخرین ورود SQL: ${dt(sync.lastReceivedAtUtc)}` : 'ورودی SQL ثبت نشده';
        $('syncTime').textContent = `${cdrText} | ${sqlText}`;
        $('syncTime').title = `ردیف‌های یک ساعت اخیر: ${fa(sync.rowsLastHour || 0)}`;
    }

    if (failed.length) toast(`خطا در بخش: ${failed.join('، ')}`);

    if (data.salesSummary) {
        const s = data.salesSummary;
        $('salesTotal').textContent = money(s.totalSales);
        $('salesInvoices').textContent = fa(s.invoiceCount);
        $('salesCustomers').textContent = fa(s.customerCount);
        $('salesAverage').textContent = money(s.averageInvoice);
        $('salesCaption').textContent = `منبع: ${s.sourceDatabase} | سال مالی ${fa(s.fiscalYear)} | ${fa(s.invoiceCount)} فاکتور`;

        const hasAccountingData = Number(s.invoiceCount || 0) > 0;
        const latestSyncSucceeded = s.lastSyncStatus === 'SUCCESS';
        const latestFactorDate = s.latestFactorDate;

        if (s.connected && latestSyncSucceeded) {
            $('accountingSyncDot').style.background = '#38d39f';
            $('accountingSyncText').textContent = 'حسابداری همگام است';
        } else if (hasAccountingData) {
            $('accountingSyncDot').style.background = '#f0b44d';
            $('accountingSyncText').textContent = latestSyncSucceeded
                ? 'داده حسابداری موجود است'
                : 'داده موجود است؛ آخرین تلاش همگام‌سازی ناموفق بوده';
        } else {
            $('accountingSyncDot').style.background = '#df5c61';
            $('accountingSyncText').textContent = 'داده حسابداری موجود نیست';
        }

        const successfulSyncText = s.lastSyncAtUtc
            ? `آخرین همگام‌سازی موفق: ${dt(s.lastSyncAtUtc)}`
            : 'همگام‌سازی موفق ثبت نشده';
        const factorText = latestFactorDate
            ? `آخرین فاکتور واردشده: ${latestFactorDate}`
            : 'فاکتور واردشده‌ای وجود ندارد';

        $('accountingSyncTime').textContent = `${successfulSyncText} | ${factorText}`;
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

    if (data.sellerPerformance) {
        $('sellerPerformanceRows').innerHTML = data.sellerPerformance.length
            ? data.sellerPerformance.map(x => `<tr>
                <td><b>${esc(x.displayName)}</b></td><td>${esc(x.extensions)}</td>
                <td><b>${fa(x.handledCalls)}</b></td><td class="direction-in">${fa(x.inboundAnswered)}</td>
                <td class="direction-out">${fa(x.outboundCalls)}</td><td>${fa(x.answeredCalls)}</td>
                <td>${fa(x.interactions)}</td><td class="${x.missingInteractions ? 'quality-bad' : 'quality-good'}">${fa(x.missingInteractions)}</td>
                <td><span class="${x.qualityPercent >= 90 ? 'quality-good' : x.qualityPercent >= 75 ? 'quality-warn' : 'quality-bad'}">${fa(x.qualityPercent)}٪</span></td>
                <td>${sec(x.talkSeconds)}</td><td>${sec(x.averageTalkSeconds)}</td>
                <td>${fa(x.quotes)}</td><td>${fa(x.followUps)}</td><td>${fa(x.orders)}</td><td>${fa(x.lost)}</td>
              </tr>`).join('')
            : '<tr><td colspan="15">در این بازه فروشندهٔ فعالی یا تماس منتسبی وجود ندارد.</td></tr>';
        renderManagementInsights(data.summary, data.sellerPerformance);
    }

    if (data.systemHealth) {
        const h = data.systemHealth;
        $('healthSql').textContent = h.sqlStatus;
        $('healthDatabaseSize').textContent = `حجم دیتابیس: ${fa(h.databaseSizeMb)} MB`;
        $('healthDidar').textContent = h.didarStatus;
        $('healthDidarCount').textContent = `${fa(h.didarContacts)} مخاطب | ${fa(h.didarPhones)} تلفن | آخرین منبع: ${dt(h.lastDidarSourceSyncAtUtc)}`;
        $('healthIssabel').textContent = h.issabelStatus;
        $('healthLastCdr').textContent = `آخرین CDR: ${dt(h.lastCdrAt)}`;
        $('healthAccounting').textContent = h.accountingStatus;
        $('healthLastAccounting').textContent = `آخرین موفق: ${dt(h.lastAccountingSyncAtUtc)} | فاکتور: ${h.lastAccountingFactorDate || '—'}`;
        $('healthLog').textContent = `${fa(h.logSizeMb)} MB`;
        $('healthRecovery').textContent = `Recovery: ${h.recoveryModel}`;
        $('healthJobRows').innerHTML = h.jobs.map(x => `<tr>
            <td><b>${esc(x.displayName)}</b><small>${esc(x.jobKey)}</small></td>
            <td>${fa(x.intervalMinutes)} دقیقه</td>
            <td><span class="badge ${x.lastStatus === 'SUCCESS' ? 'ok' : x.lastStatus === 'FAILED' ? 'bad' : 'neutral'}">${esc(x.lastStatus || 'در انتظار')}</span></td>
            <td>${dt(x.lastFinishedAtUtc || x.lastStartedAtUtc)}</td>
            <td>${x.lastDurationMs == null ? '—' : `${fa(x.lastDurationMs)} ms`}</td>
            <td>${dt(x.nextRunAtUtc)}</td>
            <td class="health-error">${esc(x.lastError || '—')}</td>
          </tr>`).join('');
    }

    $('queryMetrics').textContent = Object.entries(requestTimings)
        .map(([name, duration]) => `${name.replace('/api/', '')}: ${fa(duration)} ms`)
        .join(' | ');
    hideReportProgress();
}

function renderManagementInsights(summary, sellers) {
    if (!summary || !sellers) return;
    const bestAnswered = [...sellers].sort((a, b) => b.answeredCalls - a.answeredCalls)[0];
    const bestQuality = [...sellers].filter(x => x.handledCalls > 0)
        .sort((a, b) => b.qualityPercent - a.qualityPercent || b.handledCalls - a.handledCalls)[0];
    const missing = sellers.reduce((sum, x) => sum + Number(x.missingInteractions || 0), 0);
    const handled = sellers.reduce((sum, x) => sum + Number(x.handledCalls || 0), 0);
    const completion = handled ? Math.round((handled - missing) * 100 / handled) : 100;
    const answerRate = summary.totalCalls ? Math.round(summary.answeredCalls * 100 / summary.totalCalls) : 0;
    const qualityClass = completion >= 90 ? 'quality-good' : completion >= 75 ? 'quality-warn' : 'quality-bad';
    $('managementInsights').innerHTML = [
        `<article><b>پاسخ‌گویی تیم: ${fa(answerRate)}٪</b>${fa(summary.answeredCalls)} تماس از ${fa(summary.totalCalls)} تماس یکتا پاسخ داده شده؛ زنگ هم‌زمان داخلی‌ها دوباره شمرده نمی‌شود.</article>`,
        `<article><b>بیشترین پاسخ: ${esc(bestAnswered?.displayName || '—')}</b>${bestAnswered ? `${fa(bestAnswered.answeredCalls)} پاسخ و ${sec(bestAnswered.talkSeconds)} مکالمه` : 'هنوز تماس منتسبی وجود ندارد'}.</article>`,
        `<article><b>بهترین ثبت تعامل: ${esc(bestQuality?.displayName || '—')}</b>${bestQuality ? `${fa(bestQuality.qualityPercent)}٪ تکمیل نتیجه از ${fa(bestQuality.handledCalls)} مکالمه` : 'هنوز داده کافی وجود ندارد'}.</article>`,
        `<article><b>انضباط ثبت تیم: <span class="${qualityClass}">${fa(completion)}٪</span></b>${missing ? `${fa(missing)} مکالمهٔ واقعی هنوز تعامل ثبت‌شده ندارد.` : 'برای همهٔ مکالمه‌های منتسب نتیجه ثبت شده است.'}</article>`
    ].join('');
}

function toast(text) {
    $('toast').textContent = text;
    $('toast').style.display = 'block';
    setTimeout(() => $('toast').style.display = 'none', 3500);
}

async function runAllHealth() {
    const button = $('runAllHealth');
    if (button.disabled) return;
    button.disabled = true;
    const original = button.textContent;
    button.textContent = 'در حال اجرای اتصال‌ها…';
    try {
        const response = await fetch('/api/system/schedules/run-all', { method: 'POST', cache: 'no-store', credentials: 'same-origin' });
        let payload = null;
        try { payload = await response.json(); } catch { }
        if (!response.ok) throw new Error(payload?.error || 'اجرای اتصال‌ها ناموفق بود.');
        const failed = (Array.isArray(payload) ? payload : []).filter(x => x.status === 'FAILED');
        toast(failed.length ? `${fa(failed.length)} اتصال با خطا تمام شد؛ جزئیات در System Health ثبت شد.` : 'همهٔ اتصال‌های فعال اجرا و بروزرسانی شدند.');
        await load();
    } catch (error) { toast(error.message || 'اجرای اتصال‌ها ناموفق بود.'); }
    finally { button.disabled = false; button.textContent = original; }
}

document.querySelectorAll('.period').forEach(btn => btn.onclick = () => setPeriod(btn.dataset.period));
$('refresh').onclick = load;
$('runAllHealth').onclick = runAllHealth;
$('extension').onchange = load;
$('status').onchange = load;
$('startDate').onchange = () => document.querySelectorAll('.period').forEach(x => x.classList.remove('active'));
$('endDate').onchange = () => document.querySelectorAll('.period').forEach(x => x.classList.remove('active'));
$('search').oninput = () => {
    clearTimeout(timer);
    timer = setTimeout(() => { currentPage = 1; load(); }, 450);
};

const today = new Date();
$('startDate').value = localIso(today);
$('endDate').value = localIso(today);

load();
setInterval(() => {
    if ($('reportProgress').classList.contains('hidden')) load();
}, 60000);
