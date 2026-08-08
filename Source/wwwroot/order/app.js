const token=location.pathname.split('/').filter(Boolean).pop();
const esc=value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#039;');
const fa=n=>new Intl.NumberFormat('fa-IR',{maximumFractionDigits:2}).format(n||0);
fetch(`/api/public/orders/${encodeURIComponent(token)}`,{cache:'no-store'})
  .then(async response=>{if(!response.ok)throw new Error();return response.json()})
  .then(order=>{$('loading').classList.add('hidden');$('content').classList.remove('hidden');$('voucher').textContent=order.deliveryVoucherNumber||'—';$('date').textContent=order.purchaseDate||'—';$('summary').textContent=order.productSummary||'—';$('products').innerHTML=(order.products||[]).length?order.products.map(x=>`<div class="product"><b>${esc(x.product)}</b><span>${x.quantity==null?'':fa(x.quantity)}</span></div>`).join(''):'<div class="product">جزئیات اقلام ثبت نشده است.</div>'})
  .catch(()=>{$('loading').classList.add('hidden');$('error').classList.remove('hidden')});
function $(id){return document.getElementById(id)}
