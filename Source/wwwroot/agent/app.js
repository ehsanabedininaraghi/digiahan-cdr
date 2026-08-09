const $=id=>document.getElementById(id);
const AGENTS={
"201":{name:"مجید پورمهدی",extensions:["201","202"],product:"تیرآهن"},"202":{name:"مجید پورمهدی",extensions:["201","202"],product:"تیرآهن"},
"203":{name:"مینا شافوری",extensions:["203","204"],product:"مطالبات و مالی"},"204":{name:"مینا شافوری",extensions:["203","204"],product:"مطالبات و مالی"},
"205":{name:"ایلیا حاجی",extensions:["205","206"],product:"میلگرد"},"206":{name:"ایلیا حاجی",extensions:["205","206"],product:"میلگرد"},
"207":{name:"مهدی تقی‌زاده",extensions:["207","208"],product:"پروفیل، نبشی و ناودانی"},"208":{name:"مهدی تقی‌زاده",extensions:["207","208"],product:"پروفیل، نبشی و ناودانی"},
"211":{name:"مهدی حسنی",extensions:["211","212"],product:"حسابداری"},"212":{name:"مهدی حسنی",extensions:["211","212"],product:"حسابداری"},
"213":{name:"مهدی فراهانی",extensions:["213","214"],product:"ورق"},"214":{name:"مهدی فراهانی",extensions:["213","214"],product:"ورق"},
"215":{name:"حسنا مظاهری",extensions:["215","216"],product:"تیرآهن"},"216":{name:"حسنا مظاهری",extensions:["215","216"],product:"تیرآهن"},
"217":{name:"فتحی",extensions:["217","218"],product:"فروش"},"218":{name:"فتحی",extensions:["217","218"],product:"فروش"},
"219":{name:"عباس زمانی",extensions:["219","220"],product:"مارکتینگ"},"220":{name:"عباس زمانی",extensions:["219","220"],product:"مارکتینگ"},
"223":{name:"پویا",extensions:["223","224"],product:"حسابداری"},"224":{name:"پویا",extensions:["223","224"],product:"حسابداری"},
"225":{name:"احسان عابدینی",extensions:["225","226"],product:"مدیریت"},"226":{name:"احسان عابدینی",extensions:["225","226"],product:"مدیریت"}};

const params=new URLSearchParams(location.search);
const queryExtension=params.get("extension");
const pathPart=location.pathname.split("/").filter(Boolean).pop();
const primary=/^\d{3}$/.test(queryExtension||"")?queryExtension:(/^\d{3}$/.test(pathPart||"")?pathPart:"201");
const agent=AGENTS[primary]||{name:`داخلی ${primary}`,extensions:[primary],product:""};
const extensionCsv=agent.extensions.join(",");
let currentCard=null,selectedOutcome="";
let lastSequence=Number(sessionStorage.getItem(`agent-seq-${primary}`)||0);
let firstPoll=true;
let pollInFlight=false;
let consecutivePollFailures=0;

document.title=`پنل فروش ${agent.name}`;
$("agentTitle").textContent=`${agent.name} · داخلی ${agent.extensions.join(" و ")}${agent.product?` · ${agent.product}`:""}`;

const fa=v=>new Intl.NumberFormat("fa-IR").format(Number(v||0));
const esc=v=>String(v??"").replace(/[&<>"']/g,m=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#039;"}[m]));
const money=v=>{const n=Number(v||0);if(!n)return"۰ ریال";if(Math.abs(n)>=1e12)return`${fa((n/1e12).toFixed(2))} تریلیون ریال`;if(Math.abs(n)>=1e9)return`${fa((n/1e9).toFixed(2))} میلیارد ریال`;if(Math.abs(n)>=1e6)return`${fa((n/1e6).toFixed(1))} میلیون ریال`;return`${fa(Math.round(n))} ریال`};
const relativeDate=v=>{if(!v)return"ثبت نشده";const d=new Date(v);if(Number.isNaN(d.getTime()))return v;const days=Math.max(0,Math.floor((Date.now()-d.getTime())/86400000));return days===0?"امروز":days===1?"دیروز":`${fa(days)} روز پیش`};
const dateTime=v=>v?new Date(v).toLocaleString("fa-IR",{dateStyle:"short",timeStyle:"short"}):"—";
const outcomeLabel=v=>({FOLLOW_UP:"پیگیری",QUOTED:"قیمت داده شد",ORDER:"سفارش",NO_NEED:"عدم نیاز"})[v]||v;
const identityLabel=v=>({DIDAR_ACCOUNTING:"دیدار + حسابداری",DIDAR:"فقط دیدار",ACCOUNTING:"فقط حسابداری",NEW:"شماره جدید"})[v]||"—";
const tempLabel=v=>v==="HOT"?"داغ":v==="WARM"?"گرم":"سرد";
const setText=(id,value)=>$(id).textContent=value??"—";

function setConnection(ok){$("connectionState").className=`connection ${ok?"online":"offline"}`;$("connectionState").textContent=ok?"متصل":"ارتباط قطع"}
function toast(message){$("toast").textContent=message;$("toast").classList.remove("hidden");clearTimeout(window.__toastTimer);window.__toastTimer=setTimeout(()=>$("toast").classList.add("hidden"),2600)}

function renderCard(env,showPopup=true){
const c=env.card;currentCard=c;$("empty").classList.add("hidden");$("workspace").classList.remove("hidden");$("status").className="call-state ringing";$("status").textContent="تماس ورودی";
setText("extensionLabel",`داخلی ${c.extension}`);setText("customerName",c.customerName||c.companyName||"مشتری جدید");setText("companyName",c.companyName||"شرکت ثبت نشده");setText("rank",c.customerRank||"NEW");setText("phone",c.callerNumber);setText("owner",c.ownerName||"ثبت نشده");setText("lastCall",relativeDate(c.lastCallAt));setText("identitySource",identityLabel(c.identitySource));setText("lastProduct",c.lastProduct||"بدون خرید ثبت‌شده");setText("lastAmount",c.lastInvoiceAmount?money(c.lastInvoiceAmount):"مبلغ ثبت نشده");setText("lastPurchase",c.lastInvoiceDaysAgo==null?(c.lastInvoiceDate||"تاریخ ثبت نشده"):(c.lastInvoiceDaysAgo===0?"امروز":`${fa(c.lastInvoiceDaysAgo)} روز پیش`));setText("calls30",fa(c.callsLast30Days));setText("invoiceCount",fa(c.invoiceCount30Days));setText("sales30",money(c.sales30Days));setText("accountingCode",c.accountingCustomerCode||"بدون کد حسابداری");setText("knownState",c.isKnownCustomer?"مشتری شناسایی‌شده":"مشتری جدید");setText("suggestion",c.suggestedOpening||"نیاز مشتری را دقیق مشخص کنید.");
$("temperature").className=`temperature ${(c.temperature||"COLD").toLowerCase()}`;$("temperature").textContent=tempLabel(c.temperature);
if(showPopup){setText("popupName",c.customerName||c.companyName||"مشتری جدید");setText("popupPhone",c.callerNumber);setText("popupHint",c.suggestedOpening||"");$("popupSignals").innerHTML=`<span>رتبه ${esc(c.customerRank||"NEW")}</span><span>${esc(tempLabel(c.temperature))}</span>${c.lastProduct?`<span>${esc(c.lastProduct)}</span>`:""}`;$("popup").classList.remove("hidden");if("Notification"in window&&Notification.permission==="granted")new Notification(`تماس ورودی برای ${agent.name}`,{body:`${c.customerName||c.companyName||"مشتری جدید"}\n${c.callerNumber}`})}
}

async function pollCurrent(){
  if(pollInFlight)return;
  pollInFlight=true;
  try{
    const results=await Promise.allSettled(agent.extensions.map(async ext=>{
      const controller=new AbortController();
      const timeout=setTimeout(()=>controller.abort(),5000);
      try{
        const r=await fetch(`/api/agent/${encodeURIComponent(ext)}/current`,{cache:"no-store",signal:controller.signal});
        if(r.status===204)return null;
        if(!r.ok)throw new Error(await r.text());
        return await r.json();
      }finally{clearTimeout(timeout)}
    }));
    const successful=results.filter(x=>x.status==="fulfilled");
    if(!successful.length)throw new Error("All agent polling requests failed.");
    consecutivePollFailures=0;
    setConnection(true);
    const latest=successful.map(x=>x.value).filter(Boolean).sort((a,b)=>b.sequence-a.sequence)[0];
    if(!latest)return;
    if(latest.sequence>lastSequence){lastSequence=latest.sequence;sessionStorage.setItem(`agent-seq-${primary}`,String(lastSequence));renderCard(latest,true);await loadAll()}
    else if(firstPoll&&!currentCard)renderCard(latest,false);
    firstPoll=false;
  }catch(e){
    consecutivePollFailures++;
    if(consecutivePollFailures>=3)setConnection(false);
    console.error(e);
  }finally{pollInFlight=false}
}

async function loadStats(){try{const r=await fetch(`/api/agent/stats?extensions=${encodeURIComponent(extensionCsv)}`,{cache:"no-store"});if(!r.ok)throw new Error(await r.text());const s=await r.json();setText("statCalls",fa(s.callsToday));setText("statOutcomes",fa(s.outcomesToday));setText("statQuotes",fa(s.quotesToday));setText("statOrders",fa(s.ordersToday));setText("statPending",fa(s.pendingFollowUps))}catch(e){console.error(e)}}
async function loadHistory(){try{const r=await fetch(`/api/agent/history?extensions=${encodeURIComponent(extensionCsv)}&take=20`,{cache:"no-store"});if(!r.ok)throw new Error(await r.text());const rows=await r.json();$("historyRows").innerHTML=rows.length?rows.map(x=>`<div class="timeline-row" data-history='${encodeURIComponent(JSON.stringify(x))}'><div class="avatar">${esc((x.customerName||x.companyName||"؟").slice(0,1))}</div><div class="row-main"><b>${esc(x.customerName||x.companyName||"مشتری جدید")}</b><span>${esc(x.callerNumber)}</span></div><div class="row-meta hide-mobile">${esc(x.lastProduct||"بدون خرید")}</div><div class="row-meta hide-mobile">${dateTime(x.eventTimeUtc)}</div><span class="mini-rank">${esc(x.customerRank)}</span></div>`).join(""):`<div class="empty-list">هنوز تماسی ثبت نشده است.</div>`;document.querySelectorAll("[data-history]").forEach(el=>el.onclick=()=>openHistory(JSON.parse(decodeURIComponent(el.dataset.history))))}catch(e){$("historyRows").innerHTML=`<div class="empty-list">خطا در دریافت تاریخچه</div>`;console.error(e)}}
async function loadOutcomes(){try{const r=await fetch(`/api/agent/outcomes?extensions=${encodeURIComponent(extensionCsv)}&take=15`,{cache:"no-store"});if(!r.ok)throw new Error(await r.text());const rows=await r.json();$("outcomeRows").innerHTML=rows.length?rows.map(x=>`<div class="outcome-row"><span class="outcome-tag ${esc(x.outcome)}">${esc(outcomeLabel(x.outcome))}</span><div class="row-main"><b>${esc(x.callerNumber)}</b><span>${esc(x.note||"بدون یادداشت")}</span></div><span class="row-meta">${dateTime(x.createdAtUtc)}</span></div>`).join(""):`<div class="empty-list">هنوز نتیجه‌ای ثبت نشده است.</div>`}catch(e){$("outcomeRows").innerHTML=`<div class="empty-list">خطا در دریافت نتایج</div>`;console.error(e)}}
async function loadAll(){await Promise.all([loadStats(),loadHistory(),loadOutcomes()])}

function selectOutcome(value){selectedOutcome=value;document.querySelectorAll("[data-outcome]").forEach(b=>b.classList.toggle("selected",b.dataset.outcome===value));$("saveOutcome").disabled=!currentCard;if(value==="FOLLOW_UP"&&!$("followUpAt").value){const d=new Date(Date.now()+86400000);d.setMinutes(d.getMinutes()-d.getTimezoneOffset());$("followUpAt").value=d.toISOString().slice(0,16)}}
async function saveOutcome(){if(!currentCard||!selectedOutcome)return;$("saveOutcome").disabled=true;$("saveState").textContent="در حال ثبت...";try{const payload={extension:currentCard.extension||primary,callerNumber:currentCard.callerNumber,outcome:selectedOutcome,note:$("note").value.trim()||null,followUpAt:$("followUpAt").value?new Date($("followUpAt").value).toISOString():null,linkedId:currentCard.linkedId||null};const r=await fetch("/api/agent/outcomes",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});if(!r.ok)throw new Error(await r.text());$("saveState").textContent="ثبت شد";toast(`نتیجه «${outcomeLabel(selectedOutcome)}» ثبت شد`);$("note").value="";$("followUpAt").value="";selectedOutcome="";document.querySelectorAll("[data-outcome]").forEach(b=>b.classList.remove("selected"));await loadAll()}catch(e){$("saveState").textContent="ثبت ناموفق";toast("ثبت نتیجه انجام نشد");console.error(e)}finally{$("saveOutcome").disabled=!selectedOutcome||!currentCard;setTimeout(()=>$("saveState").textContent="",2500)}}

function openDrawer(title,html){$("drawerTitle").textContent=title;$("drawerContent").innerHTML=html;$("drawerBackdrop").classList.remove("hidden");$("drawer").classList.remove("hidden")}
function closeDrawer(){$("drawerBackdrop").classList.add("hidden");$("drawer").classList.add("hidden")}
const detailList=items=>`<div class="detail-list">${items.map(([a,b])=>`<div><span>${esc(a)}</span><b>${esc(b??"—")}</b></div>`).join("")}</div>`;
function openCurrentDetail(type){if(!currentCard)return;const c=currentCard;if(type==="purchase")openDrawer("جزئیات خرید",detailList([["آخرین کالا",c.lastProduct||"ثبت نشده"],["تاریخ آخرین فاکتور",c.lastInvoiceDate||"ثبت نشده"],["فاصله تا امروز",c.lastInvoiceDaysAgo==null?"نامشخص":`${fa(c.lastInvoiceDaysAgo)} روز`],["مبلغ آخرین فاکتور",c.lastInvoiceAmount?money(c.lastInvoiceAmount):"ثبت نشده"],["فروش ۳۰ روز",money(c.sales30Days)],["تعداد فاکتور",fa(c.invoiceCount30Days)]]));if(type==="activity")openDrawer("جزئیات فعالیت",detailList([["تعداد تماس ۳۰ روز",fa(c.callsLast30Days)],["آخرین تماس",relativeDate(c.lastCallAt)],["دمای مشتری",tempLabel(c.temperature)],["رتبه مشتری",c.customerRank],["دلیل رتبه",c.customerRankReason]]));if(type==="identity")openDrawer("هویت مشتری",detailList([["نام",c.customerName||"ثبت نشده"],["شرکت",c.companyName||"ثبت نشده"],["شماره",c.callerNumber],["مسئول دیدار",c.ownerName||"ثبت نشده"],["کد دیدار",c.didarContactCode||"ثبت نشده"],["کد حسابداری",c.accountingCustomerCode||"ثبت نشده"],["منبع شناسایی",identityLabel(c.identitySource)]]))}
function openHistory(x){openDrawer("تماس ثبت‌شده",detailList([["نام",x.customerName||x.companyName||"مشتری جدید"],["شماره",x.callerNumber],["داخلی",x.extension],["زمان تماس",dateTime(x.eventTimeUtc)],["آخرین کالا",x.lastProduct||"ثبت نشده"],["آخرین مبلغ",x.lastInvoiceAmount?money(x.lastInvoiceAmount):"ثبت نشده"],["فروش ۳۰ روز",money(x.sales30Days)],["رتبه",x.customerRank],["وضعیت",tempLabel(x.temperature)]]))}

document.querySelectorAll("[data-outcome]").forEach(b=>b.onclick=()=>selectOutcome(b.dataset.outcome));
document.querySelectorAll("[data-open]").forEach(b=>b.onclick=()=>openCurrentDetail(b.dataset.open));
$("saveOutcome").onclick=saveOutcome;$("closePopup").onclick=()=>$("popup").classList.add("hidden");$("closeDrawer").onclick=closeDrawer;$("drawerBackdrop").onclick=closeDrawer;$("refreshHistory").onclick=loadAll;
$("copyPhone").onclick=async()=>{if(!currentCard)return;await navigator.clipboard.writeText(currentCard.callerNumber);toast("شماره کپی شد")};
$("rankButton").onclick=()=>currentCard&&openDrawer("منطق رتبه مشتری",detailList([["رتبه",currentCard.customerRank],["دلیل",currentCard.customerRankReason],["توضیح","رتبه بر اساس فروش و تعداد فاکتورهای واردشده از حسابداری محاسبه می‌شود؛ این امتیاز حدس هوش مصنوعی نیست."]]));
document.addEventListener("keydown",e=>{const typing=["INPUT","TEXTAREA"].includes(document.activeElement.tagName);if(e.key==="Escape"){$("popup").classList.add("hidden");closeDrawer();return}if(e.key==="/"&&!typing){e.preventDefault();$("note").focus();return}if(typing&&e.ctrlKey&&e.key==="Enter"){saveOutcome();return}if(typing)return;const map={"1":"FOLLOW_UP","2":"QUOTED","3":"ORDER","4":"NO_NEED"};if(map[e.key]){e.preventDefault();selectOutcome(map[e.key]);saveOutcome()}});
if("Notification"in window&&Notification.permission==="default")Notification.requestPermission();
pollCurrent();loadAll();setInterval(pollCurrent,3000);setInterval(loadStats,15000);setInterval(()=>Promise.all([loadHistory(),loadOutcomes()]),30000);
