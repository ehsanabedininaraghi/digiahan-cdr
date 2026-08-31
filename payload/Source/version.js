(function(){
  "use strict";
  const fallback="4.4.4";
  const ensureChip=version=>{
    let targets=[...document.querySelectorAll("[data-app-version],#versionBadge,#footerVersion")];
    if(!targets.length){
      const chip=document.createElement("span");
      chip.dataset.appVersion="";chip.className="global-app-version";
      chip.style.cssText="position:fixed;top:10px;left:10px;z-index:10000;padding:5px 9px;border-radius:999px;background:#13243dcc;color:#fff;font:11px Tahoma,Segoe UI,sans-serif;direction:ltr;box-shadow:0 4px 16px #0002";
      document.body.appendChild(chip);targets=[chip];
    }
    targets.forEach(node=>node.textContent=`v${version}`);
  };
  ensureChip(fallback);
  fetch("/api/version",{cache:"no-store"}).then(response=>response.ok?response.json():null).then(data=>{if(data?.version)ensureChip(data.version)}).catch(()=>{});
})();
