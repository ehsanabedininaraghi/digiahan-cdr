"use strict";

const $ = (id) => document.getElementById(id);
const state = { workspace: null, selected: null, searchTimer: null };
const faNumber = new Intl.NumberFormat("fa-IR");
const faDate = new Intl.DateTimeFormat("fa-IR-u-ca-persian", { dateStyle: "medium", timeStyle: "short" });

async function api(url, options = {}) {
  const response = await fetch(url, {
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options
  });
  if (response.status === 401) {
    showView("loginView");
    throw new Error("برای ادامه دوباره وارد شوید.");
  }
  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(body?.error || `خطای سرویس (${response.status})`);
  return body;
}

function showView(id) {
  ["loginView", "disabledView", "workspace"].forEach((view) => $(view).classList.toggle("hidden", view !== id));
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>'"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[ch]));
}

function dateText(value) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "—" : faDate.format(date);
}

function localInputValue(minutesFromNow = 0) {
  const date = new Date(Date.now() + minutesFromNow * 60000 - new Date().getTimezoneOffset() * 60000);
  return date.toISOString().slice(0, 16);
}

function isoValue(id, required = true) {
  const value = $(id).value;
  if (!value) {
    if (required) throw new Error("زمان اقدام بعدی را وارد کنید.");
    return null;
  }
  return new Date(value).toISOString();
}

function toast(message, error = false) {
  const element = $("toast");
  element.textContent = message;
  element.classList.toggle("error", error);
  element.classList.remove("hidden");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => element.classList.add("hidden"), 4200);
}

async function initialize() {
  try {
    const session = await api("/api/seller-v2/session");
    $("sellerName").textContent = session.displayName;
    const feature = await api("/api/seller-v3/status");
    if (!feature.enabled) {
      showView("disabledView");
      return;
    }
    showView("workspace");
    await loadWorkspace();
  } catch (error) {
    if (!$("loginView").classList.contains("hidden")) return;
    toast(error.message, true);
  }
}

async function loadWorkspace(silent = false) {
  try {
    const data = await api("/api/seller-v3/workspace?take=60");
    state.workspace = data;
    renderWorkspace(data);
    $("lastRefresh").textContent = `به‌روزرسانی ${dateText(data.generatedAtUtc)}`;
    if (!silent) toast("صف کار به‌روز شد.");
  } catch (error) {
    if (!silent) toast(error.message, true);
  }
}

function renderWorkspace(data) {
  $("sellerName").textContent = data.seller.displayName;
  $("statLeads").textContent = faNumber.format(data.stats.openLeads);
  $("statOpportunities").textContent = faNumber.format(data.stats.activeOpportunities);
  $("statDue").textContent = faNumber.format(data.stats.dueToday);
  $("statOverdue").textContent = faNumber.format(data.stats.overdue);
  $("queueCount").textContent = faNumber.format(data.workItems.length);
  renderWorkItems(data.workItems);
  renderLeads(data.leads);
  renderOpportunities(data.opportunities);
  if (state.selected) restoreSelection();
}

function renderWorkItems(rows) {
  const host = $("workQueue");
  if (!rows.length) {
    host.className = "work-list empty-state";
    host.textContent = "صف کار شما خالی است؛ هیچ مشتری عقب نمانده است.";
    return;
  }
  host.className = "work-list";
  host.innerHTML = rows.map((row) => `
    <div class="work-card" data-kind="work" data-id="${row.workItemId}">
      <span class="priority-bar priority-${row.priority}"></span>
      <div><h4>${escapeHtml(row.customerDisplayName)}</h4><p>${escapeHtml(row.title)}</p></div>
      <div class="work-meta"><time class="${row.isOverdue ? "overdue" : ""}">${dateText(row.dueAtUtc)}</time><span class="status-chip">${escapeHtml(workTypeLabel(row.workType))}</span></div>
    </div>`).join("");
}

function renderLeads(rows) {
  const host = $("leadList");
  if (!rows.length) { host.className = "compact-list empty-state"; host.textContent = "سرنخ بازی وجود ندارد."; return; }
  host.className = "compact-list";
  host.innerHTML = rows.map((row) => `
    <div class="compact-row" data-kind="lead" data-id="${row.leadId}">
      <div><h4>${escapeHtml(row.customerDisplayName)}</h4><p>${escapeHtml(row.productSummary || row.title)}</p></div>
      <aside><span class="status-chip">${escapeHtml(statusLabel(row.status))}</span><br>${dateText(row.nextActionAtUtc)}</aside>
    </div>`).join("");
}

function renderOpportunities(rows) {
  const host = $("opportunityList");
  if (!rows.length) { host.className = "compact-list empty-state"; host.textContent = "فرصت فعالی وجود ندارد."; return; }
  host.className = "compact-list";
  host.innerHTML = rows.map((row) => `
    <div class="compact-row" data-kind="opportunity" data-id="${row.opportunityId}">
      <div><h4>${escapeHtml(row.customerDisplayName)}</h4><p>${escapeHtml(row.productSummary || row.title)}</p></div>
      <aside><span class="status-chip">${escapeHtml(stageLabel(row.stage))}</span><br>${dateText(row.nextActionAtUtc)}</aside>
    </div>`).join("");
}

function selectItem(kind, id) {
  const list = kind === "work" ? state.workspace?.workItems : kind === "lead" ? state.workspace?.leads : state.workspace?.opportunities;
  const key = kind === "work" ? "workItemId" : kind === "lead" ? "leadId" : "opportunityId";
  const item = list?.find((row) => Number(row[key]) === Number(id));
  if (!item) return;
  state.selected = { kind, id: Number(id) };
  document.querySelectorAll("[data-kind][data-id]").forEach((node) => node.classList.toggle("selected", node.dataset.kind === kind && Number(node.dataset.id) === Number(id)));
  $("focusEmpty").classList.add("hidden");
  $("focusContent").classList.remove("hidden");
  $("focusCustomer").textContent = item.customerDisplayName;
  $("focusAvatar").textContent = item.customerDisplayName?.trim()?.[0] || "م";
  $("focusPhone").textContent = item.primaryPhone || "شماره ثبت نشده";
  $("focusPhone").href = item.primaryPhone ? `tel:${item.primaryPhone}` : "#";
  $("focusTitle").textContent = item.title;
  $("focusStatus").textContent = kind === "work" ? workTypeLabel(item.workType) : kind === "lead" ? statusLabel(item.status) : stageLabel(item.stage);
  $("focusDue").textContent = dateText(kind === "work" ? item.dueAtUtc : item.nextActionAtUtc);
  $("focusActions").innerHTML = kind === "work"
    ? `<button class="primary" data-action="complete" data-id="${item.workItemId}">ثبت نتیجه اقدام</button>`
    : kind === "lead"
      ? `<button class="primary" data-action="qualify" data-id="${item.leadId}">تبدیل به فرصت فروش</button>`
      : `<button class="primary" data-action="stage" data-id="${item.opportunityId}">تغییر مرحله و اقدام بعدی</button>`;
}

function restoreSelection() {
  selectItem(state.selected.kind, state.selected.id);
  const exists = document.querySelector(`[data-kind="${state.selected.kind}"][data-id="${state.selected.id}"]`);
  if (!exists) clearFocus();
}

function clearFocus() {
  state.selected = null;
  $("focusEmpty").classList.remove("hidden");
  $("focusContent").classList.add("hidden");
}

function statusLabel(value) { return ({ OPEN: "باز", QUALIFIED: "تأییدشده", DISQUALIFIED: "ردشده", CONVERTED: "تبدیل‌شده", CLOSED: "بسته" })[value] || value; }
function stageLabel(value) { return ({ DISCOVERY: "شناخت نیاز", NEEDS_CONFIRMED: "نیاز تأیید شد", PRICE_GIVEN: "قیمت داده شد", QUOTE_SENT: "پیش‌فاکتور", DECISION: "انتظار تصمیم", NEGOTIATION: "مذاکره", ON_HOLD: "توقف موقت", WON: "فروش قطعی", LOST: "از دست رفته" })[value] || value; }
function workTypeLabel(value) { return ({ LEAD_NEXT_ACTION: "اقدام سرنخ", OPPORTUNITY_NEXT_ACTION: "اقدام فرصت", NEXT_ACTION_REVIEW: "تعیین اقدام بعدی" })[value] || "پیگیری"; }

async function login(event) {
  event.preventDefault();
  $("loginError").textContent = "";
  try {
    await api("/api/seller-v2/login", { method: "POST", body: JSON.stringify({ username: $("username").value, password: $("password").value }) });
    $("password").value = "";
    await initialize();
  } catch (error) { $("loginError").textContent = error.message; }
}

async function logout() {
  try { await api("/api/seller-v2/logout", { method: "POST", body: "{}" }); } catch (_) { /* session may already be gone */ }
  state.workspace = null;
  clearFocus();
  showView("loginView");
}

function openLeadDialog() {
  $("leadForm").reset();
  $("leadIdentityId").value = "";
  $("selectedCustomer").classList.add("hidden");
  $("customerResults").textContent = "حداقل یک حرف یا عدد وارد کنید.";
  $("leadNextAt").value = localInputValue(1440);
  $("leadError").textContent = "";
  $("leadDialog").showModal();
  setTimeout(() => $("customerSearch").focus(), 50);
}

async function searchCustomers() {
  const query = $("customerSearch").value.trim();
  if (!query) { $("customerResults").textContent = "حداقل یک حرف یا عدد وارد کنید."; return; }
  $("customerResults").textContent = "در حال جست‌وجو…";
  try {
    const rows = await api(`/api/seller-v2/customers/search?q=${encodeURIComponent(query)}&take=12`);
    $("customerResults").innerHTML = rows.length ? rows.map((row) => `
      <div class="search-row" data-customer-id="${row.identityId}" data-customer-name="${escapeHtml(row.displayName)}" data-customer-phone="${escapeHtml(row.phone)}">
        <strong>${escapeHtml(row.displayName)}</strong><span>${escapeHtml(row.phone)}</span>
      </div>`).join("") : "مشتری پیدا نشد؛ ابتدا مشتری را در میزکار فعلی ثبت کنید.";
  } catch (error) { $("customerResults").textContent = error.message; }
}

function chooseCustomer(target) {
  $("leadIdentityId").value = target.dataset.customerId;
  const text = `${target.dataset.customerName} — ${target.dataset.customerPhone}`;
  $("selectedCustomer").textContent = text;
  $("selectedCustomer").classList.remove("hidden");
  $("customerResults").innerHTML = "";
  if (!$("leadTitle").value) $("leadTitle").value = `پیگیری خرید ${target.dataset.customerName}`;
}

async function saveLead() {
  $("leadError").textContent = "";
  try {
    if (!$("leadIdentityId").value) throw new Error("یک مشتری را از نتیجه جست‌وجو انتخاب کنید.");
    const payload = {
      idempotencyKey: crypto.randomUUID(), identityId: Number($("leadIdentityId").value),
      title: $("leadTitle").value, productSummary: $("leadProduct").value || null,
      priority: Number($("leadPriority").value), nextActionType: $("leadAction").value,
      nextActionAtUtc: isoValue("leadNextAt"), note: $("leadNote").value || null
    };
    await api("/api/seller-v3/leads", { method: "POST", body: JSON.stringify(payload) });
    $("leadDialog").close();
    toast("سرنخ و اقدام بعدی ثبت شد.");
    await loadWorkspace(true);
  } catch (error) { $("leadError").textContent = error.message; }
}

function openQualifyDialog(id) {
  const lead = state.workspace?.leads.find((row) => Number(row.leadId) === Number(id));
  if (!lead) return;
  $("qualifyLeadId").value = id; $("qualifyTitle").value = lead.title;
  $("qualifyProduct").value = lead.productSummary || ""; $("qualifyQuantity").value = "";
  $("qualifyUnit").value = ""; $("qualifyAmount").value = ""; $("qualifyNextAt").value = localInputValue(1440);
  $("qualifyCloseAt").value = localInputValue(10080); $("qualifyNote").value = ""; $("qualifyError").textContent = "";
  $("qualifyDialog").showModal();
}

async function saveOpportunity() {
  $("qualifyError").textContent = "";
  try {
    const numberOrNull = (id) => $(id).value === "" ? null : Number($(id).value);
    const payload = {
      idempotencyKey: crypto.randomUUID(), title: $("qualifyTitle").value,
      productSummary: $("qualifyProduct").value || null, quantity: numberOrNull("qualifyQuantity"),
      quantityUnit: $("qualifyUnit").value || null, estimatedAmount: numberOrNull("qualifyAmount"),
      nextActionType: $("qualifyAction").value, nextActionAtUtc: isoValue("qualifyNextAt"),
      expectedCloseAtUtc: isoValue("qualifyCloseAt", false), note: $("qualifyNote").value || null
    };
    await api(`/api/seller-v3/leads/${$("qualifyLeadId").value}/qualify`, { method: "POST", body: JSON.stringify(payload) });
    $("qualifyDialog").close(); toast("فرصت فروش ایجاد شد."); await loadWorkspace(true);
  } catch (error) { $("qualifyError").textContent = error.message; }
}

function openStageDialog(id) {
  const opportunity = state.workspace?.opportunities.find((row) => Number(row.opportunityId) === Number(id));
  if (!opportunity) return;
  $("stageOpportunityId").value = id; $("stageAction").value = opportunity.nextActionType || "FOLLOW_UP";
  $("stageNextAt").value = localInputValue(1440); $("stageLostReason").value = ""; $("stageNote").value = "";
  $("stageError").textContent = ""; $("stageDialog").showModal(); updateLostReason();
}

function updateLostReason() {
  const closed = ["WON", "LOST"].includes($("stageValue").value);
  $("stageNextAt").closest("label").classList.toggle("hidden", closed);
  $("stageAction").closest("label").classList.toggle("hidden", closed);
  $("lostReasonLabel").classList.toggle("hidden", $("stageValue").value !== "LOST");
}

async function saveStage() {
  $("stageError").textContent = "";
  try {
    const stage = $("stageValue").value;
    const payload = {
      idempotencyKey: crypto.randomUUID(), stage,
      nextActionType: ["WON", "LOST"].includes(stage) ? "CLOSED" : $("stageAction").value,
      nextActionAtUtc: ["WON", "LOST"].includes(stage) ? null : isoValue("stageNextAt"),
      lostReason: stage === "LOST" ? $("stageLostReason").value : null,
      note: $("stageNote").value || null
    };
    await api(`/api/seller-v3/opportunities/${$("stageOpportunityId").value}/stage`, { method: "POST", body: JSON.stringify(payload) });
    $("stageDialog").close(); toast("مرحله و اقدام بعدی ثبت شد."); await loadWorkspace(true);
  } catch (error) { $("stageError").textContent = error.message; }
}

function openCompleteDialog(id) {
  $("completeWorkItemId").value = id; $("completeOutcome").value = "DONE"; $("completeNote").value = "";
  $("completeError").textContent = ""; $("completeDialog").showModal();
}

async function completeWorkItem() {
  $("completeError").textContent = "";
  try {
    const payload = { idempotencyKey: crypto.randomUUID(), outcome: $("completeOutcome").value, note: $("completeNote").value || null };
    await api(`/api/seller-v3/work-items/${$("completeWorkItemId").value}/complete`, { method: "POST", body: JSON.stringify(payload) });
    $("completeDialog").close(); toast("نتیجه ثبت شد و اقدام بعدی کنترل شد."); clearFocus(); await loadWorkspace(true);
  } catch (error) { $("completeError").textContent = error.message; }
}

document.addEventListener("click", (event) => {
  const item = event.target.closest("[data-kind][data-id]"); if (item) selectItem(item.dataset.kind, item.dataset.id);
  const action = event.target.closest("[data-action]");
  if (action?.dataset.action === "complete") openCompleteDialog(action.dataset.id);
  if (action?.dataset.action === "qualify") openQualifyDialog(action.dataset.id);
  if (action?.dataset.action === "stage") openStageDialog(action.dataset.id);
  const close = event.target.closest("[data-close]"); if (close) close.closest("dialog").close();
  const customer = event.target.closest("[data-customer-id]"); if (customer) chooseCustomer(customer);
});

$("loginForm").addEventListener("submit", login);
$("logoutButton").addEventListener("click", logout); $("disabledLogout").addEventListener("click", logout);
$("refreshButton").addEventListener("click", () => loadWorkspace()); $("newLeadButton").addEventListener("click", openLeadDialog);
$("saveLeadButton").addEventListener("click", saveLead); $("saveOpportunityButton").addEventListener("click", saveOpportunity);
$("saveStageButton").addEventListener("click", saveStage); $("completeButton").addEventListener("click", completeWorkItem);
$("stageValue").addEventListener("change", updateLostReason);
$("customerSearch").addEventListener("input", () => { clearTimeout(state.searchTimer); state.searchTimer = setTimeout(searchCustomers, 350); });
document.addEventListener("visibilitychange", () => { if (!document.hidden && state.workspace) loadWorkspace(true); });
setInterval(() => { if (!document.hidden && state.workspace) loadWorkspace(true); }, 60000);

initialize();
