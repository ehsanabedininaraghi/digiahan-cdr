const $=id=>document.getElementById(id);
const fa=n=>new Intl.NumberFormat("fa-IR",{minimumIntegerDigits:2,useGrouping:false}).format(n);
let callInterval=null,callSeconds=0,liveStream=null;
let authenticated=false,readOnlyCustomer=false,currentCustomerKnown=false;
let currentCustomerPhone="",currentCallLinkedId=null,currentSeller=null,editingInteractionId=null,editingOccurredAtUtc=null;
let lastLiveEventKey="",lastEnrichedEventKey="",livePollBusy=false,searchTimer=null;
const esc=value=>String(value??"").replace(/[&<>"']/g,ch=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"})[ch]);
const newId=()=>{
  const bytes=new Uint8Array(16);
  if(globalThis.crypto?.getRandomValues)crypto.getRandomValues(bytes);else for(let i=0;i<16;i++)bytes[i]=Math.floor(Math.random()*256);
  bytes[6]=(bytes[6]&15)|64;bytes[8]=(bytes[8]&63)|128;
  const hex=[...bytes].map(x=>x.toString(16).padStart(2,"0")).join("");
  return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
};
const normalizePhone=value=>{
  const latin=String(value||"").replace(/[۰-۹]/g,d=>String("۰۱۲۳۴۵۶۷۸۹".indexOf(d))).replace(/[٠-٩]/g,d=>String("٠١٢٣٤٥٦٧٨٩".indexOf(d)));
  let digits=latin.replace(/\D/g,"");
  if(digits.startsWith("0098"))digits=`0${digits.slice(4)}`;
  else if(digits.startsWith("98")&&digits.length>=12)digits=`0${digits.slice(2)}`;
  else if(digits.length===10&&!digits.startsWith("0"))digits=`0${digits}`;
  return digits;
};

async function api(path,options={}){
  const response=await fetch(path,{...options,cache:"no-store",credentials:"same-origin",headers:{"Content-Type":"application/json",...(options.headers||{})}});
  if(response.status===401){authenticated=false;$("accessGate").classList.remove("hidden");throw new Error("UNAUTHORIZED")}
  if(!response.ok){let message=`خطای ${response.status}`;try{message=(await response.json()).error||message}catch{}throw new Error(message)}
  return response.status===204?null:response.json();
}

const toast=message=>{
  const el=$("toast");
  el.textContent=message;el.classList.remove("hidden");
  clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.classList.add("hidden"),2600);
};

function setToday(){
  const text=new Intl.DateTimeFormat("fa-IR",{weekday:"long",day:"numeric",month:"long"}).format(new Date());
  $("todayLabel").textContent=text;
}

function formatJalaliDate(date){
  const parts=new Intl.DateTimeFormat("fa-IR-u-ca-persian-nu-latn",{year:"numeric",month:"2-digit",day:"2-digit"}).formatToParts(date);
  const get=type=>parts.find(x=>x.type===type)?.value;
  return `${get("year")}/${get("month")}/${get("day")}`;
}

function jalaliToLocalDate(value){
  const latin=String(value||"").replace(/[۰-۹]/g,d=>"۰۱۲۳۴۵۶۷۸۹".indexOf(d)).replace(/[٠-٩]/g,d=>"٠١٢٣٤٥٦٧٨٩".indexOf(d));
  const match=latin.trim().match(/^(\d{4})[\/-](\d{1,2})[\/-](\d{1,2})$/);
  if(!match)return null;
  const month=Number(match[2]),day=Number(match[3]);
  if(month<1||month>12||day<1||day>31||(month>6&&day>30))return null;
  const target=`${match[1]}/${match[2].padStart(2,"0")}/${match[3].padStart(2,"0")}`;
  const start=new Date(Number(match[1])+621,1,20);
  for(let i=0;i<410;i++){
    const candidate=new Date(start);candidate.setDate(start.getDate()+i);
    if(formatJalaliDate(candidate)===target)return candidate;
  }
  return null;
}

function resetInteractionForm(){
  $("interactionForm").reset();
  editingInteractionId=null;editingOccurredAtUtc=null;
  $("saveInteraction").textContent="ثبت نتیجه مکالمه";
  updateConditionalFields();
}

function openDrawer(source="interaction",preserveForm=false,allowReadOnly=false){
  if(readOnlyCustomer&&!allowReadOnly){toast("پرونده انتخاب‌شده از جست‌وجو فقط برای مشاهده است");return}
  const phone=normalizePhone(currentCustomerPhone);
  if(phone.length<7){toast("ابتدا یک تماس واقعی یا مشتری را انتخاب کنید");return}
  if(!preserveForm)resetInteractionForm();
  const customer=$("customerName").textContent||$("contactName").textContent||"مشتری";
  $("drawerEyebrow").textContent=source==="call"?"تماس پایان یافت · ثبت نتیجه":source==="edit"?"اصلاح تعامل ثبت‌شده":"ثبت تعامل جدید";
  $("interactionDrawerTitle").textContent=`نتیجه مکالمه با ${customer}`;
  $("interactionPhone").textContent=phone;
  $("drawerBackdrop").classList.remove("hidden");
  $("interactionDrawer").classList.remove("hidden");
  document.body.style.overflow="hidden";
  $("interactionDrawer").querySelector("form").scrollTop=0;
}

function closeDrawer(){
  $("interactionDrawer").classList.add("hidden");
  if($("customerDrawer").classList.contains("hidden"))$("drawerBackdrop").classList.add("hidden");
  document.body.style.overflow="";
}

async function openCustomerDrawer(){
  if(!currentCustomerPhone||currentCustomerPhone==="—"){toast("ابتدا یک مشتری یا تماس را انتخاب کنید");return}
  let profile={identityId:null,displayName:null,companyName:null,ownerName:null,phones:[currentCustomerPhone],source:"NEW"};
  if(location.protocol!=="file:") profile=await api(`/api/seller-v2/customers/profile?phone=${encodeURIComponent(currentCustomerPhone)}`);
  $("customerDrawer").dataset.exists=profile.identityId?"true":"false";
  $("customerDrawerTitle").textContent=profile.identityId?"اصلاح مشتری":"ثبت مشتری جدید";
  $("masterDisplayName").value=profile.displayName||$("contactName").textContent.replace("نام ثبت نشده","");
  $("masterCompanyName").value=profile.companyName||($("customerName").textContent==="مشتری جدید"?"":$("customerName").textContent);
  $("masterOwnerName").value=profile.ownerName||"";
  $("masterPhones").value=(profile.phones?.length?profile.phones:[currentCustomerPhone]).join("\n");
  $("archiveCustomer").classList.toggle("hidden",!profile.identityId);
  $("drawerBackdrop").classList.remove("hidden");$("customerDrawer").classList.remove("hidden");
  document.body.style.overflow="hidden";
  $("masterDisplayName").focus();
}

function closeCustomerDrawer(){
  $("customerDrawer").classList.add("hidden");
  if($("interactionDrawer").classList.contains("hidden"))$("drawerBackdrop").classList.add("hidden");
  document.body.style.overflow="";
}

async function saveCustomer(){
  const phones=$("masterPhones").value.split(/[\n,،]+/).map(x=>x.trim()).filter(Boolean);
  const payload={displayName:$("masterDisplayName").value.trim()||null,companyName:$("masterCompanyName").value.trim()||null,
    ownerName:$("masterOwnerName").value.trim()||null,phones};
  const exists=$("customerDrawer").dataset.exists==="true";
  $("saveCustomer").disabled=true;$("saveCustomer").textContent="در حال ذخیره…";
  try{
    const result=await api(exists?`/api/seller-v2/customers/by-phone/${encodeURIComponent(currentCustomerPhone)}`:"/api/seller-v2/customers",
      {method:exists?"PUT":"POST",body:JSON.stringify(payload)});
    currentCustomerPhone=result.primaryPhone;currentCustomerKnown=true;closeCustomerDrawer();
    await loadWorkspace(currentCustomerPhone,readOnlyCustomer);toast(exists?"اطلاعات مشتری اصلاح شد":"مشتری در دفترچه یکپارچه ثبت شد");
  }catch(error){toast(error.message)}
  finally{$("saveCustomer").disabled=false;$("saveCustomer").textContent="ذخیره مشتری"}
}

async function archiveCustomer(){
  if(!confirm("مشتری آرشیو شود؟ سوابق تماس و خرید پاک نمی‌شود."))return;
  try{await api(`/api/seller-v2/customers/by-phone/${encodeURIComponent(currentCustomerPhone)}`,{method:"DELETE"});closeCustomerDrawer();toast("مشتری با حفظ سوابق آرشیو شد");await loadWorkspace()}
  catch(error){toast(error.message)}
}

function updateConditionalFields(){
  const outcome=document.querySelector('input[name="outcome"]:checked')?.value;
  $("followUpFields").classList.toggle("hidden",outcome!=="FOLLOW_UP");
  $("lossFields").classList.toggle("hidden",outcome!=="LOST");
  $("nonSalesFields").classList.toggle("hidden",outcome!=="NON_SALES");
  $("salesDetails").classList.toggle("hidden",!outcome||outcome==="NON_SALES");
  if(outcome==="FOLLOW_UP"&&!$("followDate").value){
    const date=new Date();date.setDate(date.getDate()+1);$("followDate").value=formatJalaliDate(date);
    $("followTime").value="10:30";
  }
}

function startCall(){
  if(callInterval)return;
  callSeconds=0;
  $("activeCallCard").classList.remove("hidden");
  $("simulateCall").disabled=true;$("simulateCall").innerHTML="<span>●</span> تماس فعال";
  callInterval=setInterval(()=>{
    callSeconds++;
    $("callTimer").textContent=`${fa(Math.floor(callSeconds/60))}:${fa(callSeconds%60)}`;
  },1000);
  window.scrollTo({top:document.querySelector(".content-grid").offsetTop-100,behavior:"smooth"});
  toast(`تماس ${$("customerName").textContent||currentCustomerPhone||"آزمایشی"} متصل شد`);
}

function endCall(){
  if(lastLiveEventKey)localStorage.setItem("sellerV2DismissedCall",lastLiveEventKey);
  clearInterval(callInterval);callInterval=null;
  $("activeCallCard").classList.add("hidden");
  $("simulateCall").disabled=false;$("simulateCall").innerHTML="<span>☎</span> شبیه‌سازی تماس";
  openDrawer("call");
}

function showLiveCall(card,publishedAtUtc,serverNowUtc){
  if(!card)return;
  const key=card.linkedId||`${card.extension}|${card.callerNumber}|${card.eventTimeUtc}`;
  const isNew=key!==lastLiveEventKey;
  lastLiveEventKey=key;
  if(localStorage.getItem("sellerV2DismissedCall")===key)return;
  const serverNow=new Date(serverNowUtc||new Date()).getTime();
  const published=new Date(publishedAtUtc||card.eventTimeUtc).getTime();
  const age=Math.max(0,serverNow-published);
  if(!Number.isFinite(age)||age>10*60*1000)return;
  currentCustomerPhone=card.callerNumber;currentCallLinkedId=card.linkedId;currentCustomerKnown=Boolean(card.isKnownCustomer);
  $("customerName").textContent=card.companyName||card.customerName||"مشتری جدید";
  $("contactName").textContent=card.customerName||"نام ثبت نشده";$("customerPhone").textContent=card.callerNumber;
  $("customerState").textContent=card.isKnownCustomer?"مشتری فعال":"مشتری جدید";
  if(card.identitySource&&card.identitySource!=="PENDING"&&lastEnrichedEventKey!==key){
    lastEnrichedEventKey=key;loadWorkspace(card.callerNumber).catch(console.error);
  }
  if(!isNew)return;
  clearInterval(callInterval);callInterval=null;
  $("activeCallCard").classList.remove("hidden");
  $("liveCallIdentity").textContent=`${card.customerName||card.companyName||"مشتری جدید"} · ${card.callerNumber}`;
  $("simulateCall").disabled=true;$("simulateCall").innerHTML="<span>●</span> تماس واقعی";
  callSeconds=Math.max(0,Math.floor(age/1000));
  if(!callInterval)callInterval=setInterval(()=>{callSeconds++;$("callTimer").textContent=`${fa(Math.floor(callSeconds/60))}:${fa(callSeconds%60)}`},1000);
  toast(`تماس واقعی ${card.customerName||card.companyName||card.callerNumber} وارد شد`);
}

async function pollLiveCall(){
  if(!authenticated||livePollBusy||location.protocol==="file:")return;
  livePollBusy=true;
  try{const data=await api("/api/seller-v2/current-call");if(data)showLiveCall(data.card,data.publishedAtUtc,data.serverNowUtc)}catch(error){if(error.message!=="UNAUTHORIZED")console.warn(error)}
  finally{livePollBusy=false}
}

function startLiveEvents(){
  if(!authenticated||location.protocol==="file:"||typeof EventSource==="undefined")return;
  if(liveStream)liveStream.close();
  liveStream=new EventSource("/api/seller-v2/live-events",{withCredentials:true});
  liveStream.addEventListener("call",event=>{
    try{const data=JSON.parse(event.data);showLiveCall(data.card,data.publishedAtUtc,data.serverNowUtc)}catch(error){console.warn(error)}
  });
  liveStream.onerror=()=>{if(authenticated)setTimeout(pollLiveCall,500)};
}

async function saveInteraction(){
  const outcome=document.querySelector('input[name="outcome"]:checked')?.value;
  const phone=normalizePhone(currentCustomerPhone);
  if(phone.length<7){toast("شماره مشتری معتبر نیست؛ یک تماس یا مشتری واقعی را انتخاب کنید");return}
  if(!outcome){toast("نتیجه مکالمه را انتخاب کنید");return}
  if(outcome==="FOLLOW_UP"&&!$("followDate").value){toast("تاریخ پیگیری را مشخص کنید");return}
  if(outcome==="LOST"&&!document.querySelector('input[name="loss"]:checked')){toast("دلیل عدم خرید را انتخاب کنید");return}
  const labels={ORDER:"سفارش ثبت شد",DECIDING:"در حال تصمیم‌گیری",FOLLOW_UP:"پیگیری ثبت شد",LOST:"عدم خرید ثبت شد",NON_SALES:"تماس غیر فروش ثبت شد"};
  if(location.protocol!=="file:"){
    const date=jalaliToLocalDate($("followDate").value),time=$("followTime").value;
    if(outcome==="FOLLOW_UP"&&!date){toast("تاریخ شمسی را به شکل ۱۴۰۵/۰۵/۲۰ وارد کنید");return}
    if(outcome==="FOLLOW_UP"&&(!time||!$("followSubject").value.trim())){toast("ساعت و دلیل پیگیری را مشخص کنید");return}
    const nonSalesReason=document.querySelector('input[name="nonSalesReason"]:checked')?.value;
    let note=$("interactionNote").value.trim();
    if(outcome==="NON_SALES"&&nonSalesReason)note=`[نوع تماس: ${nonSalesReason}]${note?` ${note}`:""}`;
    const payload={idempotencyKey:newId(),customerPhone:phone,callLinkedId:currentCallLinkedId,occurredAtUtc:editingOccurredAtUtc,
      productName:outcome!=="NON_SALES"?$("product").value||null:null,productSize:outcome!=="NON_SALES"?$("size").value.trim()||null:null,productBrand:outcome!=="NON_SALES"?$("brand").value.trim()||null:null,
      quantity:outcome!=="NON_SALES"&&$("quantity").value!==""?Number($("quantity").value):null,quantityUnit:outcome!=="NON_SALES"?$("quantityUnit").value||null:null,
      actions:outcome!=="NON_SALES"?[...document.querySelectorAll("[data-action]:checked")].map(item=>item.dataset.action):[],outcome,
      lossReason:outcome==="LOST"?document.querySelector('input[name="loss"]:checked')?.value:null,
      followUpAtUtc:outcome==="FOLLOW_UP"?new Date(date.getFullYear(),date.getMonth(),date.getDate(),Number(time.split(":")[0]),Number(time.split(":")[1])).toISOString():null,
      followUpSubject:outcome==="FOLLOW_UP"?$("followSubject").value.trim():null,note:note||null};
    $("saveInteraction").disabled=true;$("saveInteraction").textContent="در حال ثبت…";
    const endpoint=editingInteractionId?`/api/seller-v2/interactions/${editingInteractionId}`:"/api/seller-v2/interactions";
    try{await api(endpoint,{method:editingInteractionId?"PUT":"POST",body:JSON.stringify(payload)})}
    catch(error){toast(error.message==="UNAUTHORIZED"?"دسترسی منقضی شده است":error.message);return}
    finally{$("saveInteraction").disabled=false;$("saveInteraction").textContent="ثبت نتیجه مکالمه"}
  }
  closeDrawer();toast(`${labels[outcome]}؛ اطلاعات با موفقیت ذخیره شد`);
  if(location.protocol!=="file:")await loadWorkspace(phone);
}

function formatDate(value){return new Intl.DateTimeFormat("fa-IR-u-ca-persian",{month:"short",day:"numeric",hour:"2-digit",minute:"2-digit",hour12:false,timeZone:"Asia/Tehran"}).format(new Date(value))}
function relativeDate(value){if(!value)return"—";const days=Math.floor((Date.now()-new Date(value).getTime())/86400000);return days<=0?"امروز":`${days.toLocaleString("fa-IR")} روز پیش`}
function money(value){return new Intl.NumberFormat("fa-IR",{maximumFractionDigits:0}).format(Number(value)||0)}
function renderBalance(value,creditLimit){
  if(value==null){$("snapshotBalance").textContent="—";$("snapshotBalanceStatus").textContent="اطلاعات مانده موجود نیست";return}
  const amount=Number(value);
  $("snapshotBalance").textContent=`${money(Math.abs(amount))} ریال`;
  const state=amount>0?"بدهکار":amount<0?"بستانکار":"تسویه";
  $("snapshotBalanceStatus").textContent=creditLimit==null?state:`${state} · سقف اعتبار ${money(creditLimit)} ریال`;
}
function outcomeLabel(value){return({ORDER:"سفارش ثبت شد",DECIDING:"در حال تصمیم‌گیری",FOLLOW_UP:"نیاز به پیگیری",LOST:"خرید انجام نشد",NON_SALES:"تماس غیر فروش"})[value]||value}

async function editInteraction(id){
  try{
    const item=await api(`/api/seller-v2/interactions/${id}`);
    currentCustomerPhone=item.customerPhone;currentCallLinkedId=item.callLinkedId||null;
    editingInteractionId=item.id;editingOccurredAtUtc=item.occurredAtUtc;
    $("interactionForm").reset();
    document.querySelector(`input[name="outcome"][value="${item.outcome}"]`)?.click();
    $("product").value=item.productName||"";$("size").value=item.productSize||"";$("brand").value=item.productBrand||"";
    $("quantity").value=item.quantity??"";$("quantityUnit").value=item.quantityUnit||"";
    (item.actions||[]).forEach(action=>{const input=document.querySelector(`[data-action="${action}"]`);if(input)input.checked=true});
    if(item.lossReason){const input=document.querySelector(`input[name="loss"][value="${item.lossReason}"]`);if(input)input.checked=true}
    if(item.followUpAtUtc){const due=new Date(item.followUpAtUtc);$("followDate").value=formatJalaliDate(due);$("followTime").value=new Intl.DateTimeFormat("en-GB",{hour:"2-digit",minute:"2-digit",hour12:false,timeZone:"Asia/Tehran"}).format(due)}
    $("followSubject").value=item.followUpSubject||"";
    let note=item.note||"";const reason=note.match(/^\[نوع تماس: ([^\]]+)\]\s*/);
    if(reason){const input=[...document.querySelectorAll('input[name="nonSalesReason"]')].find(x=>x.value===reason[1]);if(input)input.checked=true;note=note.slice(reason[0].length)}
    $("interactionNote").value=note;
    updateConditionalFields();openDrawer("edit",true,true);$("saveInteraction").textContent="ذخیره اصلاحات";
  }catch(error){toast(error.message)}
}

async function loadWorkspace(phone="",lookupOnly=false){
  const data=await api(`/api/seller-v2/workspace${phone?`?phone=${encodeURIComponent(phone)}&readOnly=${lookupOnly?"true":"false"}`:""}`);
  const seller=data.seller,stats=data.stats,card=data.customer;currentSeller=seller;
  authenticated=true;readOnlyCustomer=Boolean(data.readOnlyCustomer);
  $("sidebarSellerName").textContent=seller.displayName;$("welcomeName").textContent=seller.displayName.split(" ")[0];
  $("sidebarSellerExtension").textContent=`کارشناس فروش · داخلی ${seller.extensions.join(" و ")}`;
  $("sidebarAvatar").textContent=seller.displayName.split(/\s+/).slice(0,2).map(x=>x[0]||"").join("");
  $("statOverdue").textContent=stats.overdue;$("statDue").textContent=stats.dueToday;$("statConversations").textContent=stats.conversations;
  $("statPriced").textContent=stats.priced;$("statOrders").textContent=stats.orders;$("statLost").textContent=`${stats.lost} عدم خرید`;
  $("missingResults").textContent=`${stats.missingResults} نتیجه ناقص`;
  $("qualityPercent").textContent=`${stats.qualityPercent.toLocaleString("fa-IR")}٪`;
  $("qualityBar").style.width=`${stats.qualityPercent}%`;
  $("qualityCompleted").textContent=Math.max(0,stats.conversations-stats.missingResults).toLocaleString("fa-IR");
  $("qualityMissing").textContent=stats.missingResults.toLocaleString("fa-IR");
  $("qualityMissingRow").classList.toggle("done",stats.missingResults===0);
  $("qualityMissingRow").classList.toggle("warning",stats.missingResults>0);
  $("navTaskCount").textContent=(stats.dueToday+stats.overdue).toLocaleString("fa-IR");
  $("readOnlyNotice").classList.toggle("hidden",!readOnlyCustomer);
  $("newInteraction").disabled=readOnlyCustomer;
  $("newInteraction").title=readOnlyCustomer?"نتیجه جست‌وجو فقط برای مشاهده است":"ثبت تعامل";
  if(card){
    currentCustomerPhone=normalizePhone(card.callerNumber);currentCallLinkedId=card.linkedId;currentCustomerKnown=Boolean(card.isKnownCustomer);
    $("customerName").textContent=card.companyName||card.customerName||"مشتری جدید";$("contactName").textContent=card.customerName||"نام ثبت نشده";
    $("customerPhone").textContent=card.callerNumber;$("customerState").textContent=card.isKnownCustomer?"مشتری فعال":"مشتری جدید";
    $("customerOwner").textContent=card.ownerName||"ثبت نشده";$("identitySource").textContent=card.identitySource||"نامشخص";
    $("mainInterest").textContent=card.lastProduct||seller.productGroups.join("، ")||"ثبت نشده";$("customerCode").textContent=card.accountingCustomerCode||"ثبت نشده";
    $("snapshotLastCall").textContent=relativeDate(card.lastCallAt);$("snapshotCallCount").textContent=`${(card.callsLast30Days||0).toLocaleString("fa-IR")} تماس در ۳۰ روز`;
    $("snapshotLastPurchase").textContent=card.lastInvoiceDaysAgo==null?"—":card.lastInvoiceDaysAgo===0?"امروز":`${card.lastInvoiceDaysAgo.toLocaleString("fa-IR")} روز پیش`;
    $("snapshotLastProduct").textContent=card.lastProduct||"بدون خرید ثبت‌شده";$("snapshotInvoices").textContent=(card.invoiceCount30Days||0).toLocaleString("fa-IR");
    $("snapshotSales").textContent=money(card.sales30Days);
    renderBalance(card.accountBalance,card.creditLimit);
    const avatar=(card.customerName||card.companyName||"؟").split(/\s+/).slice(0,2).map(x=>x[0]||"").join("");$("customerAvatar").textContent=avatar||"؟";
  }else{
    currentCustomerPhone="";currentCallLinkedId=null;currentCustomerKnown=false;
    $("customerName").textContent="آماده دریافت تماس";$("contactName").textContent="تماس جدید به‌صورت خودکار نمایش داده می‌شود";$("customerPhone").textContent="—";
    $("customerState").textContent="بدون تماس فعال";$("customerOwner").textContent="—";$("identitySource").textContent="—";$("mainInterest").textContent="—";$("customerCode").textContent="—";
    $("snapshotLastCall").textContent="—";$("snapshotCallCount").textContent="بدون سابقه";$("snapshotLastPurchase").textContent="—";$("snapshotLastProduct").textContent="بدون خرید ثبت‌شده";$("snapshotInvoices").textContent="۰";$("snapshotSales").textContent="۰";$("customerAvatar").textContent="؟";
    renderBalance(null,null);
  }
  if(data.followUps.length){
    $("followUpList").innerHTML=data.followUps.slice(0,5).map((x,index)=>`<article class="task ${new Date(x.dueAtUtc)<new Date()?"overdue":""}"><i></i><div><b>${esc(x.customerDisplayName)}</b><span>${esc(x.subject)}</span><small>${formatDate(x.dueAtUtc)}</small></div><button data-follow-id="${x.id}">›</button></article>`).join("");
    $("completeTask").dataset.followId=data.followUps[0].id;
    const next=data.followUps[0],due=new Date(next.dueAtUtc);$("nextActionCard").classList.remove("hidden");$("nextSubject").textContent=next.subject;$("nextCustomer").textContent=next.customerDisplayName;
    $("nextDay").textContent=new Intl.DateTimeFormat("fa-IR-u-ca-persian",{month:"short",day:"numeric",timeZone:"Asia/Tehran"}).format(due);$("nextTime").textContent=new Intl.DateTimeFormat("fa-IR",{hour:"2-digit",minute:"2-digit",hour12:false,timeZone:"Asia/Tehran"}).format(due);$("nextStatus").textContent=due<new Date()?"عقب‌افتاده":"برنامه‌ریزی‌شده";
  }else{$("followUpList").innerHTML='<div class="empty-list">پیگیری بازی ندارید.</div>';$("nextActionCard").classList.add("hidden")}
  $("timeline").innerHTML=data.timeline.length?`<div class="day-label"><span>سابقه ثبت‌شده</span></div>`+data.timeline.map(x=>`<article class="timeline-item ${x.isMine?"mine":"others"}" data-kind="${x.outcome==="ORDER"?"orders":x.isMine?"mine":"others"}"><div class="timeline-icon ${x.eventType==="CALL"?"quote":x.outcome==="LOST"?"lost":x.outcome==="ORDER"?"order":"follow"}">${x.eventType==="CALL"?"☎":x.outcome==="ORDER"?"▣":x.outcome==="LOST"?"×":"↗"}</div><div class="timeline-content"><header><div><b>${esc(x.title||outcomeLabel(x.outcome)||"تماس تلفنی")}</b><span>${esc(x.sellerDisplayName)}</span></div><time>${formatDate(x.eventAtUtc)}</time></header><p>${esc(x.description||"بدون توضیح")}</p><div class="tag-row">${x.productName?`<span>${esc(x.productName)} ${esc(x.productSize||"")}</span>`:""}${x.lossReason?`<span class="red-tag">${esc(x.lossReason)}</span>`:""}${x.eventType==="INTERACTION"&&x.isMine?`<button type="button" class="edit-interaction" data-edit-interaction="${x.id}">اصلاح نتیجه</button>`:""}</div></div></article>`).join(""):'<div class="empty-list">هنوز سابقه‌ای برای این مشتری ثبت نشده است.</div>';
  $("accessGate").classList.add("hidden");
  pollLiveCall();startLiveEvents();
}

async function searchCustomers(query){
  query=String(query||"").trim();
  if(query.length<1){$("searchResults").classList.add("hidden");$("searchResults").innerHTML="";return}
  const rows=await api(`/api/seller-v2/customers/search?q=${encodeURIComponent(query)}&take=20`);
  $("searchResults").innerHTML=rows.length?rows.map(x=>`<button type="button" data-customer-phone="${esc(x.phone)}"><span><b>${esc(x.displayName)}</b>${x.companyName&&x.companyName!==x.displayName?`<small>${esc(x.companyName)}</small>`:""}</span><em dir="ltr">${esc(x.mobilePhones||x.phone)}</em></button>`).join(""):'<div class="search-empty">مشتری‌ای پیدا نشد</div>';
  $("searchResults").classList.remove("hidden");
}

async function showMissingResults(){
  const rows=await api("/api/seller-v2/missing-results?take=30");
  if(!rows.length){toast("همه مکالمه‌های امروز نتیجه دارند");return}
  $("searchResults").innerHTML=`<div class="search-heading">مکالمه‌های بدون نتیجه</div>`+rows.map(x=>`<button type="button" data-missing-phone="${esc(x.customerPhone)}" data-linked-id="${esc(x.linkedId||"")}"><span><b>${esc(x.customerDisplayName)}</b><small>${formatDate(x.eventAtUtc)}</small></span><em dir="ltr">${esc(x.customerPhone)}</em></button>`).join("");
  $("searchResults").classList.remove("hidden");
  $("globalSearch").focus();
}

$("searchResults").addEventListener("click",async event=>{
  const button=event.target.closest("button");if(!button)return;
  const searchPhone=button.dataset.customerPhone;
  const missingPhone=button.dataset.missingPhone;
  $("searchResults").classList.add("hidden");
  if(searchPhone){$("globalSearch").value=button.querySelector("b")?.textContent||searchPhone;await loadWorkspace(searchPhone,true);return}
  if(missingPhone){
    await loadWorkspace(missingPhone);
    readOnlyCustomer=false;currentCallLinkedId=button.dataset.linkedId||null;
    $("readOnlyNotice").classList.add("hidden");$("newInteraction").disabled=false;
    openDrawer("call");
  }
});

document.querySelectorAll(".segmented-control button").forEach(button=>button.addEventListener("click",()=>{
  document.querySelectorAll(".segmented-control button").forEach(x=>x.classList.remove("active"));button.classList.add("active");
  document.querySelectorAll(".timeline-item").forEach(item=>item.classList.toggle("hidden",button.dataset.filter!=="all"&&item.dataset.kind!==button.dataset.filter));
}));
document.querySelectorAll('input[name="outcome"]').forEach(input=>input.addEventListener("change",updateConditionalFields));
$("timeline").addEventListener("click",event=>{const button=event.target.closest("[data-edit-interaction]");if(button)editInteraction(Number(button.dataset.editInteraction))});
document.querySelectorAll(".nav-item").forEach(button=>button.addEventListener("click",()=>{
  document.querySelectorAll(".nav-item").forEach(x=>x.classList.remove("active"));button.classList.add("active");
  if(button.dataset.view==="tasks")document.querySelector(".task-panel").scrollIntoView({behavior:"smooth",block:"start"});
  else if(button.dataset.view==="customers"){$("globalSearch").focus();toast("نام یا شماره مشتری را جست‌وجو کنید")}
  else if(button.dataset.view==="reports")document.querySelector(".daily-overview").scrollIntoView({behavior:"smooth",block:"start"});
  else document.querySelector("main").scrollIntoView({behavior:"smooth",block:"start"});
  document.querySelector(".sidebar").classList.remove("open");
}));

$("newInteraction").addEventListener("click",()=>openDrawer());
$("manageCustomer").addEventListener("click",()=>openCustomerDrawer().catch(error=>toast(error.message)));
$("closeCustomerDrawer").addEventListener("click",closeCustomerDrawer);
$("saveCustomer").addEventListener("click",saveCustomer);
$("archiveCustomer").addEventListener("click",archiveCustomer);
$("closeDrawer").addEventListener("click",closeDrawer);
$("drawerBackdrop").addEventListener("click",()=>{closeDrawer();closeCustomerDrawer()});
$("saveInteraction").addEventListener("click",saveInteraction);
$("simulateCall").addEventListener("click",startCall);
$("endCall").addEventListener("click",endCall);
$("menuButton").addEventListener("click",()=>document.querySelector(".sidebar").classList.toggle("open"));
$("copyPhone").addEventListener("click",async()=>{try{await navigator.clipboard.writeText(currentCustomerPhone);toast("شماره مشتری کپی شد")}catch{toast("امکان کپی شماره وجود ندارد")}});
$("completeTask").addEventListener("click",async event=>{
  const id=event.currentTarget.dataset.followId;
  if(location.protocol!=="file:"&&id){
    try{await api(`/api/seller-v2/follow-ups/${id}/complete`,{method:"POST",body:JSON.stringify({idempotencyKey:newId()})})}
    catch(error){toast(error.message);return}
  }
  event.currentTarget.textContent="✓ تکمیل شد";event.currentTarget.disabled=true;toast("پیگیری با موفقیت انجام شد");
});
$("loadMore").addEventListener("click",event=>{event.currentTarget.textContent="تمام سوابق نمایش داده شده است";event.currentTarget.disabled=true});
$("globalSearch").addEventListener("input",event=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>searchCustomers(event.currentTarget.value).catch(error=>toast(error.message)),180)});
$("globalSearch").addEventListener("keydown",event=>{if(event.key==="Escape")$("searchResults").classList.add("hidden")});
document.addEventListener("keydown",event=>{
  if(event.key==="Escape"){closeDrawer();closeCustomerDrawer()}
  if(event.key==="/"&&document.activeElement.tagName!=="INPUT"&&document.activeElement.tagName!=="TEXTAREA"){event.preventDefault();$("globalSearch").focus()}
});

$("accessForm").addEventListener("submit",async event=>{
  event.preventDefault();$("accessError").textContent="";
  try{
    await api("/api/seller-v2/login",{method:"POST",body:JSON.stringify({username:$("accessUsername").value.trim(),password:$("accessPassword").value})});
    authenticated=true;$("accessPassword").value="";await loadWorkspace();
  }
  catch(error){$("accessError").textContent=error.message==="UNAUTHORIZED"?"نام کاربری یا رمز عبور صحیح نیست.":error.message}
});

$("logoutButton").addEventListener("click",async()=>{
  try{await api("/api/seller-v2/logout",{method:"POST"})}catch{}
  if(liveStream)liveStream.close();
  authenticated=false;location.reload();
});
$("completeMissing").addEventListener("click",()=>showMissingResults().catch(error=>toast(error.message)));

setToday();resetInteractionForm();
if(location.protocol!=="file:"){
  $("simulateCall").classList.add("hidden");
  api("/api/seller-v2/session").then(()=>{authenticated=true;return loadWorkspace()}).catch(error=>{if(error.message!=="UNAUTHORIZED")toast("ارتباط با سرویس برقرار نشد")});
}else $("accessGate").classList.add("hidden");
setInterval(pollLiveCall,10000);
window.addEventListener("focus",pollLiveCall);
document.addEventListener("visibilitychange",()=>{if(!document.hidden)pollLiveCall()});
