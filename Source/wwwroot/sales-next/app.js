const tasks=[
  {id:1,customer:'شرکت فولاد آریا',action:'ارسال قیمت تیرآهن ۱۸ و تماس برای دریافت تصمیم',phone:'۰۹۱۲ ۶۶۶ ۷۰۳۰۹',time:'امروز، ۱۰:۳۰',kind:'today'},
  {id:2,customer:'مجتبی صادقی',action:'تماس پیگیری پس از ارسال پیش‌فاکتور',phone:'۰۹۱۲ ۴۸۱ ۳۲۱۰',time:'امروز، ۱۳:۰۰',kind:'today'},
  {id:3,customer:'بنگاه آهن‌گستر',action:'دریافت نتیجهٔ استعلام موجودی',phone:'۰۹۱۲ ۸۷۲ ۱۱۵۶',time:'امروز، ۱۵:۰۰',kind:'today'},
  {id:4,customer:'مهدی اکبری',action:'تماس عقب‌افتاده؛ مشتری منتظر اعلام قیمت است',phone:'۰۹۱۲ ۵۰۲ ۶۹۹۱',time:'دیروز، ۱۶:۰۰',kind:'overdue'},
  {id:5,customer:'شرکت سازه‌گستر',action:'تعیین تکلیف درخواست ورق؛ اقدام قبلی ثبت نشده',phone:'۰۹۱۲ ۷۰۴ ۲۰۰۸',time:'دیروز، ۱۴:۳۰',kind:'overdue'}
];
const list=document.querySelector('#taskList'),toast=document.querySelector('#toast');let current='all';
function faCount(n){return new Intl.NumberFormat('fa-IR').format(n)}
function showToast(message){toast.textContent=message;toast.style.display='block';setTimeout(()=>toast.style.display='none',2800)}
function render(){const visible=tasks.filter(x=>current==='all'||x.kind===current);list.innerHTML=visible.map(x=>`<article class="task ${x.kind}"><i class="priority"></i><div><h3>${x.customer}</h3><p>${x.action}</p><small dir="ltr">${x.phone}</small></div><div class="task-meta"><span class="due">${x.time}</span><button class="done" data-id="${x.id}">انجام شد</button></div></article>`).join('')||'<p class="muted">کاری در این فیلتر باقی نمانده است.</p>';document.querySelector('#dueCount').textContent=faCount(tasks.length);document.querySelector('#todayCount').textContent=faCount(tasks.length);document.querySelectorAll('.done').forEach(button=>button.onclick=()=>{const id=Number(button.dataset.id);const index=tasks.findIndex(x=>x.id===id);const customer=tasks[index].customer;tasks.splice(index,1);render();showToast(`اقدام «${customer}» در این نمونه انجام‌شده علامت خورد.`)})}
document.querySelectorAll('.filter').forEach(button=>button.onclick=()=>{current=button.dataset.filter;document.querySelectorAll('.filter').forEach(x=>x.classList.toggle('active',x===button));render()});document.querySelectorAll('.resolve').forEach(button=>button.onclick=()=>showToast('در نسخهٔ واقعی، اینجا انتخاب اقدام، زمان و مسئول باز می‌شود.'));render();
