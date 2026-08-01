const $ = id => document.getElementById(id);
const fa = n => new Intl.NumberFormat('fa-IR').format(n || 0);
let timer;

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
    return new Date(v).toLocaleString('fa-IR', { hour12: false });
}

function dateOnly(v) {
    if (!v) return '—';
    return new Date(v).toLocaleDateString('fa-IR');
}

function esc(v) {
    return String(v ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#039;');
}

async function get(url) {
    const response = await fetch(url, { cache: 'no-store' });
    if (!response.ok) throw new Error(await response.text());
    return response.json();
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
        start.setDate(today.getDate() - 6);
    } else if (period === 'month') {
        start = new Date(today.getFullYear(), today.getMonth(), 1);
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
    const base = queryBase();
    const requests = {
        summary: get(`/api/dashboard/summary?${base}`),
        hourly: get(`/api/dashboard/hourly?${base}`),
        daily: get(`/api/dashboard/daily?${base}`),
        extensions: get(`/api/dashboard/extensions?startDate=${encodeURIComponent($('startDate').value)}&endDate=${encodeURIComponent($('endDate').value)}`),
        calls: get(`/api/dashboard/calls?${base}&search=${encodeURIComponent($('search').value)}&status=${$('status').value}&pageSize=100`),
        sync: get('/api/dashboard/sync'),
        version: get('/api/version')
    };

    $('rangeCaption').textContent = `گزارش از ${dateOnly($('startDate').value)} تا ${dateOnly($('endDate').value)}${$('extension').value !== 'all' ? ` برای داخلی ${$('extension').value}` : ''}`;

    const keys = Object.keys(requests);
    const results = await Promise.allSettled(Object.values(requests));
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
            ? data.extensions.map(x => `<tr><td><b>${esc(x.extension)}</b></td><td>${esc(extensionNames[x.extension] || '—')}</td><td>${fa(x.total)}</td><td>${fa(x.answered)}</td><td>${fa(x.missed)}</td><td>${sec(x.talkSeconds)}</td><td>${sec(x.averageTalkSeconds)}</td></tr>`).join('')
            : '<tr><td colspan="7">داده‌ای نیست</td></tr>';
    }

    if (data.calls) {
        const calls = data.calls;
        $('callCount').textContent = fa(calls.total) + ' ردیف CDR';
        $('callRows').innerHTML = calls.items.length
            ? calls.items.map(x => `<tr class="${x.isNewCustomer ? 'new-customer-row' : ''}">
                <td>${dt(x.calldate)}</td><td><b>${esc(x.src || '—')}</b></td><td>${esc(x.dst || '—')}</td>
                <td>${customerCell(x)}</td><td>${x.companyName ? esc(x.companyName) : '—'}</td>
                <td>${x.ownerName ? esc(x.ownerName) : '—'}</td>
                <td>${x.direction === 'inbound' ? 'ورودی' : x.direction === 'outbound' ? 'خروجی' : 'داخلی/نامشخص'}</td>
                <td><span class="badge ${(x.disposition === 'ANSWERED' || x.billsec > 0) ? 'ok' : 'bad'}">${(x.disposition === 'ANSWERED' || x.billsec > 0) ? 'پاسخ' : 'بی‌پاسخ'}</span></td>
                <td>${sec(x.duration)}</td><td>${sec(x.billsec)}</td>
                <td>${x.recordingFile ? '<span class="record" title="مسیر ضبط ثبت شده">●</span>' : '—'}</td>
            </tr>`).join('')
            : '<tr><td colspan="11">داده‌ای یافت نشد</td></tr>';
    } else {
        $('callCount').textContent = 'خطا در دریافت تماس‌ها';
        $('callRows').innerHTML = '<tr><td colspan="11">بخش تماس‌ها موقتاً در دسترس نیست.</td></tr>';
    }

    if (data.sync) {
        const sync = data.sync;
        const good = sync.lastBatchStatus === 'SUCCESS' && sync.lastBatchErrors === 0;
        $('syncDot').style.background = good ? '#38d39f' : '#f0b44d';
        $('syncText').textContent = good ? 'همگام‌سازی فعال' : (sync.lastBatchStatus || 'بدون وضعیت');
        $('syncTime').textContent = sync.lastReceivedAtUtc ? 'آخرین دریافت: ' + dt(sync.lastReceivedAtUtc) : 'هنوز داده‌ای نرسیده';
    }

    if (failed.length) toast(`خطا در بخش: ${failed.join('، ')}`);
}

function toast(text) {
    $('toast').textContent = text;
    $('toast').style.display = 'block';
    setTimeout(() => $('toast').style.display = 'none', 3500);
}

document.querySelectorAll('.period').forEach(btn => btn.onclick = () => setPeriod(btn.dataset.period));
$('refresh').onclick = load;
$('extension').onchange = load;
$('status').onchange = load;
$('startDate').onchange = () => document.querySelectorAll('.period').forEach(x => x.classList.remove('active'));
$('endDate').onchange = () => document.querySelectorAll('.period').forEach(x => x.classList.remove('active'));
$('search').oninput = () => {
    clearTimeout(timer);
    timer = setTimeout(load, 450);
};

const today = new Date();
$('startDate').value = localIso(today);
$('endDate').value = localIso(today);

load();
setInterval(load, 60000);
