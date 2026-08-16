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
assert(dashboardCss.includes("pointer-events:none"),"Dashboard refresh indicator blocks user interaction.");
assert(!dashboardCss.includes(".report-progress{position:fixed;inset:0"),"Blocking full-screen dashboard overlay returned.");
const htmlIds=new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(match=>match[1]));
const referencedIds=new Set([...app.matchAll(/\$\("([^"]+)"\)/g)].map(match=>match[1]));
for(const id of referencedIds)assert(htmlIds.has(id),`Seller script references a missing element: #${id}`);

const pages=fs.readdirSync(path.join(root,"Source/wwwroot"),{withFileTypes:true})
  .filter(entry=>entry.isDirectory()&&fs.existsSync(path.join(root,"Source/wwwroot",entry.name,"index.html")));
for(const page of pages){
  const content=read(`Source/wwwroot/${page.name}/index.html`);
  assert(content.includes("/version.js?v=4312"),`Version badge script is missing from ${page.name}.`);
}
console.log(`v4.3.12 seller UI regression passed (${pages.length} pages checked).`);
