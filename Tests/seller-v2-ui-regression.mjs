import fs from "node:fs";
import path from "node:path";

const root=path.resolve(import.meta.dirname,"..");
const read=relative=>fs.readFileSync(path.join(root,relative),"utf8");
const html=read("Source/wwwroot/seller-v2/index.html");
const app=read("Source/wwwroot/seller-v2/app.js");
const dashboardCss=read("Source/wwwroot/dashboard/style.css");

const assert=(condition,message)=>{if(!condition)throw new Error(message)};
for(const forbidden of ["شرکت ساختمانی آریا سازه","علی احمدی","حسنا مظاهری","value=\"20\"","checked><span><b>قیمت اعلام شد"])
  assert(!html.includes(forbidden),`Demo/default value returned: ${forbidden}`);
assert(!html.match(/name="outcome"[^>]*checked/),"An interaction outcome must not be preselected.");
assert(app.includes('new EventSource("/api/seller-v2/live-events"'),"Live seller event stream is missing.");
assert(app.includes('method:editingInteractionId?"PUT":"POST"'),"Interaction edit path is missing.");
assert(app.includes("normalizePhone(currentCustomerPhone)"),"Client phone normalization is missing.");
assert(app.includes('data-owner="${x.isMine?"mine":x.eventType==="INVOICE"?"system":"others"}"'),"Timeline owner classification is missing.");
assert(app.includes('data-order="${x.outcome==="ORDER"}"'),"Timeline order classification is missing.");
assert(app.includes('activeTimelineFilter=button.dataset.filter||"all";applyTimelineFilter()'),"Timeline filters are not wired to rendered rows.");
assert(dashboardCss.includes("pointer-events:none"),"Dashboard refresh indicator blocks user interaction.");
assert(!dashboardCss.includes(".report-progress{position:fixed;inset:0"),"Blocking full-screen dashboard overlay returned.");
const htmlIds=new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(match=>match[1]));
const referencedIds=new Set([...app.matchAll(/\$\("([^"]+)"\)/g)].map(match=>match[1]));
for(const id of referencedIds)assert(htmlIds.has(id),`Seller script references a missing element: #${id}`);

const pages=fs.readdirSync(path.join(root,"Source/wwwroot"),{withFileTypes:true})
  .filter(entry=>entry.isDirectory()&&fs.existsSync(path.join(root,"Source/wwwroot",entry.name,"index.html")));
for(const page of pages){
  const content=read(`Source/wwwroot/${page.name}/index.html`);
  assert(content.includes("/version.js?"),`Version badge script is missing from ${page.name}.`);
}
const journeyHtml=read("Source/wwwroot/seller-v3/index.html");
const journeyApp=read("Source/wwwroot/seller-v3/app.js");
assert(!journeyHtml.includes('/dashboard'),"Seller v3 must not expose a management dashboard link.");
assert(journeyApp.includes('/api/seller-v3/workspace'),"Seller v3 workspace API is not wired.");
assert(journeyApp.includes('crypto.randomUUID()'),"Seller v3 mutations must use idempotency keys.");
const journeyIds=new Set([...journeyHtml.matchAll(/\bid="([^"]+)"/g)].map(match=>match[1]));
const journeyReferencedIds=new Set([...journeyApp.matchAll(/\$\("([^"]+)"\)/g)].map(match=>match[1]));
for(const id of journeyReferencedIds)assert(journeyIds.has(id),`Seller v3 script references a missing element: #${id}`);
console.log(`v4.4.0 seller UI regression passed (${pages.length} pages checked).`);
