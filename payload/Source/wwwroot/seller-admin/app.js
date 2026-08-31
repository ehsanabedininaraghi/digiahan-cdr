const $=id=>document.getElementById(id);
let users=[];
let currentCredential="";

function esc(value){return String(value??"").replace(/[&<>"']/g,ch=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"})[ch])}
function splitValues(value){return String(value||"").split(/[،,]/).map(x=>x.trim()).filter(Boolean)}
function fa(value){return Number(value||0).toLocaleString("fa-IR")}
function formatDate(value){return value?new Intl.DateTimeFormat("fa-IR-u-ca-persian",{dateStyle:"medium",timeStyle:"short",timeZone:"Asia/Tehran"}).format(new Date(value)):"هنوز وارد نشده"}
function toast(message){$("toast").textContent=message;$("toast").classList.add("show");setTimeout(()=>$("toast").classList.remove("show"),2800)}

async function api(url,options={}){
  const response=await fetch(url,{credentials:"same-origin",headers:{"Content-Type":"application/json",...(options.headers||{})},...options});
  if(response.status===401){location.href="/dashboard-login";throw new Error("ورود مدیریت منقضی شده است.")}
  const body=response.status===204?null:await response.json().catch(()=>null);
  if(!response.ok)throw new Error(body?.error||`خطای HTTP ${response.status}`);
  return body;
}

function render(){
  const query=$("search").value.trim().toLowerCase();
  const rows=users.filter(x=>!query||[x.displayName,x.username,x.sellerKey,...x.extensions].some(v=>String(v).toLowerCase().includes(query)));
  $("totalUsers").textContent=fa(users.length);
  $("activeUsers").textContent=fa(users.filter(x=>x.isActive).length);
  $("inactiveUsers").textContent=fa(users.filter(x=>!x.isActive).length);
  $("onlineUsers").textContent=fa(users.reduce((sum,x)=>sum+x.activeSessions,0));
  $("userRows").innerHTML=rows.length?rows.map(x=>`<tr>
    <td><strong>${esc(x.displayName)}</strong><small dir="ltr">${esc(x.sellerKey)}</small></td>
    <td><b dir="ltr">${esc(x.username)}</b></td>
    <td><div class="chips">${x.extensions.map(v=>`<span class="chip" dir="ltr">${esc(v)}</span>`).join("")}</div></td>
    <td><div class="chips">${x.productGroups.length?x.productGroups.map(v=>`<span class="chip product">${esc(v)}</span>`).join(""):"—"}</div></td>
    <td><span>${formatDate(x.lastLoginAtUtc)}</span>${x.activeSessions?`<small> · ${fa(x.activeSessions)} نشست فعال</small>`:""}</td>
    <td><span class="status ${x.isActive?"":"off"}">${x.isActive?"فعال":"غیرفعال"}</span></td>
    <td><div class="row-actions"><button data-edit="${x.id}">ویرایش</button><button data-reset="${x.id}">رمز تازه</button><button class="${x.isActive?"danger":""}" data-toggle="${x.id}">${x.isActive?"غیرفعال":"فعال"}</button></div></td>
  </tr>`).join(""):'<tr><td colspan="7" class="empty">کاربری با این مشخصات پیدا نشد.</td></tr>';
}

async function load(){users=await api("/api/seller-admin/users");render()}

function openForm(user=null){
  $("userForm").reset();$("formError").textContent="";$("userId").value=user?.id||"";
  $("formHint").textContent=user?"ویرایش دسترسی":"کاربر جدید";
  $("formTitle").textContent=user?`ویرایش ${user.displayName}`:"ساخت کاربر فروش";
  $("displayName").value=user?.displayName||"";$("username").value=user?.username||"";
  $("sellerKey").value=user?.sellerKey||"";$("extensions").value=user?.extensions.join(", ")||"";
  $("productGroups").value=user?.productGroups.join("، ")||"";$("isActive").checked=user?.isActive??true;
  $("password").required=!user;$("passwordLabel").textContent=user?"رمز تازه (اختیاری)":"رمز اولیه";
  $("passwordHelp").textContent=user?"اگر خالی بماند، رمز فعلی تغییر نمی‌کند.":"رمز پس از ذخیره قابل بازیابی نیست.";
  $("modal").classList.remove("hidden");setTimeout(()=>$("displayName").focus(),30);
}
function closeForm(){$("modal").classList.add("hidden")}

function generatePassword(){
  const chars="ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
  const random=new Uint32Array(14);crypto.getRandomValues(random);
  $("password").value=[...random].map(x=>chars[x%chars.length]).join("");
}

function payload(password){return{
  displayName:$("displayName").value.trim(),username:$("username").value.trim(),sellerKey:$("sellerKey").value.trim(),
  password:password||null,isActive:$("isActive").checked,extensions:splitValues($("extensions").value),productGroups:splitValues($("productGroups").value)
}}

function showCredentials(user,password){
  currentCredential=`آدرس ورود: ${location.origin}/seller-v2/\nنام: ${user.displayName}\nنام کاربری: ${user.username}\nرمز عبور: ${password}`;
  $("credentialText").textContent=currentCredential;$("credentialModal").classList.remove("hidden");
}

$("userForm").addEventListener("submit",async event=>{
  event.preventDefault();$("formError").textContent="";$("saveUser").disabled=true;
  const id=Number($("userId").value)||null,password=$("password").value;
  try{
    const user=await api(id?`/api/seller-admin/users/${id}`:"/api/seller-admin/users",{method:id?"PUT":"POST",body:JSON.stringify(payload(password))});
    closeForm();await load();toast(id?"اطلاعات کاربر به‌روز شد.":"کاربر فروش ساخته شد.");
    if(password)showCredentials(user,password);
  }catch(error){$("formError").textContent=error.message}
  finally{$("saveUser").disabled=false}
});

$("userRows").addEventListener("click",async event=>{
  const edit=event.target.closest("[data-edit]"),reset=event.target.closest("[data-reset]"),toggle=event.target.closest("[data-toggle]");
  if(edit){openForm(users.find(x=>x.id===Number(edit.dataset.edit)));return}
  if(reset){
    const user=users.find(x=>x.id===Number(reset.dataset.reset));if(!user)return;
    const password=prompt(`رمز تازه برای ${user.displayName} (حداقل ۸ کاراکتر):`);if(password===null)return;
    try{await api(`/api/seller-admin/users/${user.id}/reset-password`,{method:"POST",body:JSON.stringify({newPassword:password})});await load();showCredentials(user,password);toast("رمز عوض شد و نشست‌های قبلی بسته شدند.")}catch(error){toast(error.message)}
    return;
  }
  if(toggle){
    const user=users.find(x=>x.id===Number(toggle.dataset.toggle));if(!user)return;
    if(user.isActive&&!confirm(`کاربر «${user.displayName}» غیرفعال و از سیستم خارج شود؟`))return;
    try{await api(`/api/seller-admin/users/${user.id}`,{method:"PUT",body:JSON.stringify({...user,password:null,isActive:!user.isActive})});await load();toast(user.isActive?"کاربر غیرفعال شد.":"کاربر فعال شد.")}catch(error){toast(error.message)}
  }
});

$("newUser").addEventListener("click",()=>openForm());
$("generatePassword").addEventListener("click",generatePassword);
$("search").addEventListener("input",render);
document.querySelectorAll("[data-close]").forEach(x=>x.addEventListener("click",closeForm));
document.querySelectorAll("[data-credential-close]").forEach(x=>x.addEventListener("click",()=>$("credentialModal").classList.add("hidden")));
$("copyCredentials").addEventListener("click",async()=>{try{await navigator.clipboard.writeText(currentCredential);toast("اطلاعات ورود کپی شد.")}catch{toast("کپی خودکار ممکن نیست؛ متن را دستی کپی کنید.")}});
document.addEventListener("keydown",event=>{if(event.key==="Escape"){closeForm();$("credentialModal").classList.add("hidden")}});

load().catch(error=>{$("userRows").innerHTML=`<tr><td colspan="7" class="empty">${esc(error.message)}</td></tr>`});
