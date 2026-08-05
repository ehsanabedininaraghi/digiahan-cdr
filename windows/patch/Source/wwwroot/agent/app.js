const $=id=>document.getElementById(id);
const params=new URLSearchParams(location.search);
const extension=params.get('extension')||location.pathname.split('/').filter(Boolean).pop()||'201';
let lastSequence=0;
const history=[];

$('agentTitle').textContent=`داخلی ${extension}`;
document.title=`پنل تماس داخلی ${extension}`;

const fa=n=>new Intl.NumberFormat('fa-IR').format(Number(n||0));
const money=n=>{
  n=Number(n||0);
  if(n>=1e12)return `${fa((n/1e12).toFixed(2))} تریلیون ریال`;
  if(n>=1e9)return `${fa((n/1e9).toFixed(2))} میلیارد ریال`;
  if(n>=1e6)return `${fa((n/1e6).toFixed(1))} میلیون ریال`;
  return `${fa(n)} ریال`;
};
const daysAgo=v=>{
  if(!v)return '—';
  const d=new Date(v),days=Math.max(0,Math.floor((Date.now()-d.getTime())/86400000));
  return days===0?'امروز':`${fa(days)} روز پیش`;
};
const temp=v=>v==='HOT'?'🔥 داغ':v==='WARM'?'🟡 گرم':'🔵 سرد';

function render(env){
  const c=env.card;
  $('empty').classList.add('hidden');
  $('card').classList.remove('hidden');
  $('status').textContent='تماس ورودی';
  $('customerName').textContent=c.customerName||'مشتری جدید';
  $('companyName').textContent=c.companyName||'شرکت ثبت نشده';
  $('rank').textContent=c.customerRank;
  $('phone').textContent=c.callerNumber;
  $('temperature').textContent=temp(c.temperature);
  $('lastCall').textContent=daysAgo(c.lastCallAt);
  $('calls30').textContent=fa(c.callsLast30Days);
  $('lastPurchase').textContent=c.lastInvoiceDate||'بدون خرید ثبت‌شده';
  $('lastAmount').textContent=c.lastInvoiceAmount?money(c.lastInvoiceAmount):'—';
  $('invoiceCount').textContent=fa(c.invoiceCount30Days);
  $('sales30').textContent=money(c.sales30Days);
  $('owner').textContent=c.ownerName||'—';
  $('product').textContent=c.lastProduct||'—';
  $('accountingCode').textContent=c.accountingCustomerCode||'—';
  $('suggestion').textContent=c.suggestedOpening||'—';

  $('popupName').textContent=c.customerName||c.companyName||'مشتری جدید';
  $('popupPhone').textContent=c.callerNumber;
  $('popupHint').textContent=c.suggestedOpening||'';
  $('popup').classList.remove('hidden');

  history.unshift(c);
  if(history.length>10)history.pop();
  $('historyRows').innerHTML=history.map(x=>`<div class="history-row"><b>${x.customerName||x.companyName||'مشتری جدید'}</b><span>${x.callerNumber}</span><span>${new Date(x.eventTimeUtc).toLocaleString('fa-IR')}</span></div>`).join('');

  if('Notification' in window&&Notification.permission==='granted'){
    new Notification(`تماس ورودی داخلی ${extension}`,{body:`${c.customerName||c.companyName||'مشتری جدید'}\n${c.callerNumber}`});
  }
}
$('closePopup').onclick=()=>$('popup').classList.add('hidden');
if('Notification' in window&&Notification.permission==='default')Notification.requestPermission();

async function poll(){
  try{
    const r=await fetch(`/api/agent/${encodeURIComponent(extension)}/current`,{cache:'no-store'});
    if(r.status===204)return;
    if(!r.ok)throw new Error(await r.text());
    const data=await r.json();
    if(data.sequence>lastSequence){
      lastSequence=data.sequence;
      render(data);
    }
  }catch(e){
    $('status').textContent='خطا در اتصال';
    console.error(e);
  }
}
poll();
setInterval(poll,1500);
