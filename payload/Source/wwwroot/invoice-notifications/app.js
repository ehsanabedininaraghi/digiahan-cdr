const $=id=>document.getElementById(id);
const fa=n=>new Intl.NumberFormat('fa-IR').format(n||0);
const esc=value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#039;');
const labels={READY:'آماده ارسال',NEEDS_PHONE:'بدون موبایل',NEEDS_IDENTITY:'بدون هویت',PREPARED:'متن آماده',CANCELLED:'لغوشده'};
const classes={READY:'ready',NEEDS_PHONE:'needs',NEEDS_IDENTITY:'needs',PREPARED:'prepared',CANCELLED:'sent'};
let data=[];

function headers(){const token=$('apiToken').value.trim();return token?{'X-Api-Token':token}:{}}
async function api(url,options={}){const response=await fetch(url,{cache:'no-store',...options,headers:{...headers(),...(options.headers||{})}});const body=await response.json().catch(()=>({}));if(!response.ok)throw new Error(body.error||`HTTP ${response.status}`);return body}
function toast(message){$('toast').textContent=message;$('toast').style.display='block';setTimeout(()=>$('toast').style.display='none',4000)}

async function load(){
  try{
    data=await api(`/api/invoice-notifications?status=${encodeURIComponent($('status').value)}&take=300`);
    $('summary').textContent=`فقط امروز و دیروز: ${fa(data.length)} ردیف؛ ${fa(data.filter(x=>x.status==='READY').length)} آماده ارسال`;
    $('rows').innerHTML=data.length?data.map(rowHtml).join(''):'<tr><td colspan="9">فاکتور دارای شرح حواله برای امروز یا دیروز یافت نشد.</td></tr>';
  }catch(error){$('rows').innerHTML='<tr><td colspan="9">دسترسی برقرار نشد. در اتصال شبکه، توکن مدیریت را وارد و ذخیره کنید.</td></tr>';toast(error.message)}
}

function rowHtml(x){
  const phones=x.availablePhones||[];
  const options=phones.map(p=>`<option value="${esc(p)}" ${p===x.primaryPhone?'selected':''}>${esc(p)}</option>`).join('');
  const phoneControl=x.status==='NEEDS_IDENTITY'?'—':`<div class="phone-select"><select onchange="setPrimary(${x.id},this.value)">${options||'<option value="">بدون موبایل</option>'}</select><button onclick="newPhone(${x.id})">جدید</button></div>`;
  const selectable=['READY','PREPARED'].includes(x.status);
  const checked=selectable?`<input class="pick" type="checkbox" aria-label="ثبت ارسال" onchange="markSent(${x.id},this)">`:'—';
  return `<tr><td>${checked}</td><td>${esc(x.invoiceNumber||'—')}</td><td>${esc(x.factorDate||'—')}</td><td><b>${esc(x.customerName||'بدون نام')}</b></td><td>${esc(x.productSummary||'—')}</td><td><b>${esc(x.deliveryVoucherNumber)}</b></td><td>${phoneControl}</td><td><span class="badge ${classes[x.status]||'sent'}">${labels[x.status]||esc(x.status)}</span></td><td><div class="row-actions"><button onclick="prepareOne(${x.id})" ${selectable?'':'disabled'}>آماده‌سازی پیام</button></div></td></tr>`;
}

async function setPrimary(id,phone){if(!phone)return;try{await api(`/api/invoice-notifications/${id}/primary-mobile`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({phone,actor:$('actor').value})});toast('شماره اصلی ثبت شد.');await load()}catch(error){toast(error.message);await load()}}
async function newPhone(id){const phone=prompt('شماره موبایل جدید را وارد کنید (09xxxxxxxxx):');if(phone)await setPrimary(id,phone)}
async function prepare(ids){try{const result=await api('/api/sms-operator/prepare',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({notificationIds:ids,actor:$('actor').value})});showPrepared(result);await load()}catch(error){toast(error.message)}}
async function prepareOne(id){await prepare([id])}

function showPrepared(items){
  $('preparedPanel').classList.remove('hidden');
  $('preparedRows').innerHTML=items.map(x=>`<article class="message-card"><h3>${esc(x.phone)}</h3><textarea id="message-${x.notificationId}" readonly>${esc(x.smsText)}</textarea><div class="actions"><button onclick="copyMessage(${x.notificationId})">کپی متن</button><button class="secondary" onclick="markSent(${x.notificationId})">ارسال شد</button></div></article>`).join('');
  $('preparedPanel').scrollIntoView({behavior:'smooth'});
}
async function copyMessage(id){await navigator.clipboard.writeText($(`message-${id}`).value);toast('متن پیامک کپی شد.')}
async function markSent(id,input){
  if(input && !input.checked)return;
  if(input && !confirm('ارسال پیامک انجام شده است؟ با تأیید، این ردیف از لیست امروز و دیروز حذف می‌شود.')){input.checked=false;return;}
  try{await api(`/api/sms-operator/${id}/manual-sent`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({actor:$('actor').value,note:'ارسال دستی توسط مدیر'})});toast('ارسال ثبت شد و ردیف از لیست حذف شد.');await load()}catch(error){if(input)input.checked=false;toast(error.message)}
}

$('saveToken').onclick=()=>{sessionStorage.setItem('digiahan-api-token',$('apiToken').value.trim());toast('توکن فقط در همین مرورگر و همین نشست ذخیره شد.');load()};
$('refresh').onclick=load;$('status').onchange=load;
$('discover').onclick=async()=>{try{const x=await api('/api/invoice-notifications/discover',{method:'POST'});toast(`بررسی شد: ${fa(x.scanned)}، آماده: ${fa(x.ready)}`);await load()}catch(error){toast(error.message)}};
$('apiToken').value=sessionStorage.getItem('digiahan-api-token')||'';
window.setPrimary=setPrimary;window.newPhone=newPhone;window.prepareOne=prepareOne;window.copyMessage=copyMessage;window.markSent=markSent;
load();
