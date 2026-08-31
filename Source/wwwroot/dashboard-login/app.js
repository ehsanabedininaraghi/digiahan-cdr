const form=document.querySelector('#loginForm');
const password=document.querySelector('#password');
const error=document.querySelector('#error');
form.addEventListener('submit',async event=>{
  event.preventDefault();
  const button=form.querySelector('button');
  button.disabled=true;error.textContent='';
  try{
    const response=await fetch('/api/dashboard-auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:password.value})});
    const body=await response.json().catch(()=>({}));
    if(!response.ok)throw new Error(body.error||'ورود انجام نشد.');
    location.replace('/dashboard');
  }catch(ex){error.textContent=ex.message;password.select();button.disabled=false}
});
