"use strict";

const $ = id => document.getElementById(id);
const state = { page: 1, pageSize: 50, calls: [], reviews: [], detailId: null, loading: false, demoMode: false, batchMode: false, sampleDetails: {}, hasNext: false };
const labels = {
  BUSINESS_CONVERSATION: "مکالمه کاری", QUEUE_ONLY: "فقط صدای صف", NEEDS_REVIEW: "نیازمند بررسی",
  NON_SPEECH_OR_UNSUPPORTED: "غیرگفتاری / پشتیبانی‌نشده", EMPTY_OR_LOW_SIGNAL: "خالی / سیگنال ضعیف",
  OPEN: "باز", CONFIRMED: "تأییدشده", CORRECTED: "اصلاح‌شده", REJECTED: "ردشده", DEFERRED: "موکول‌شده",
  COMPLETED: "تکمیل‌شده", READY: "آماده", LOCAL_PURGED: "پردازش کامل؛ فایل موقت حذف شده",
  TRANSCRIBING: "در حال تبدیل صدا به متن", ANALYZING: "در حال تحلیل", TRANSFERRING: "در حال انتقال",
  SOURCE_MISSING: "فایل مبدا پیدا نشد", VALIDATION_FAILED: "اعتبارسنجی ناموفق", FAILED: "ناموفق",
  TOPIC: "موضوع", BRAND: "برند", NON_PURCHASE_REASON: "دلیل احتمالی عدم خرید", BEHAVIOR_INDICATOR: "نشانه رفتاری",
  SELLER_STRENGTH: "نقطه قوت مکالمه", TRANSCRIPT_QUALITY: "کیفیت متن",
  RISK_SIGNAL: "نشانه حساس", SENSITIVE_RISK: "نشانه حساس", LOW_CONFIDENCE: "اطمینان پایین",
  ANGER_OR_ESCALATION: "تنش یا شکایت", INSULT_OR_PROFANITY: "توهین یا ناسزا",
  BRIBERY_OR_PERSONAL_PAYMENT: "پرداخت شخصی یا فساد احتمالی",
  REPETITION_OR_LOW_INFORMATION: "تکرار یا اطلاعات ناکافی",
  PRICE_TOO_HIGH: "قیمت بالا", NO_BUDGET_OR_LIQUIDITY: "کمبود بودجه یا نقدینگی",
  OUT_OF_STOCK: "نبود موجودی", DELIVERY_TIME: "زمان تحویل", PAYMENT_TERMS: "شرایط پرداخت",
  COMPETITOR_SELECTED: "انتخاب تأمین‌کننده دیگر", NOT_READY: "آماده نبودن مشتری",
  INBOUND: "ورودی", OUTBOUND: "خروجی", INTERNAL: "داخلی",
  NOT_AVAILABLE: "موجود نیست",
  HIGH: "بالا", MEDIUM: "متوسط", LOW: "کم"
};

function token() { return sessionStorage.getItem("digiahanApiToken") || ""; }
function reviewer() { return localStorage.getItem("digiahanAiReviewer") || ""; }
function headers(json = false) {
  const result = {};
  if (token()) result["X-Api-Token"] = token();
  if (json) result["Content-Type"] = "application/json";
  return result;
}
async function api(url, options = {}) {
  const response = await fetch(url, { ...options, headers: { ...headers(Boolean(options.body)), ...(options.headers || {}) } });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    if (response.status === 401) throw new Error("دسترسی رد شد؛ توکن API را بررسی کنید.");
    throw new Error(body.error || `خطای سرویس (${response.status})`);
  }
  return body;
}
function esc(value) {
  return String(value ?? "").replace(/[&<>'"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[ch]);
}
function tr(value) { return labels[String(value ?? "").toUpperCase()] || value || "نامشخص"; }
function fa(value) { return Number(value || 0).toLocaleString("fa-IR"); }
function percent(value) { return value == null ? "—" : `${fa(Math.round(Number(value) * 100))}٪`; }
function date(value) {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? esc(value) : parsed.toLocaleString("fa-IR", { dateStyle: "short", timeStyle: "short" });
}
function clock(seconds) {
  if (seconds == null || Number.isNaN(Number(seconds))) return "—";
  const total = Math.max(0, Math.floor(Number(seconds)));
  return `${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`;
}
function badge(value, extra = "") {
  const key = String(value || "").toLowerCase();
  const kind = key.includes("business") ? "business" : key.includes("review") || key === "open" ? "review" : key;
  return `<span class="badge ${esc(kind)} ${esc(extra)}">${esc(tr(value))}</span>`;
}
function showNotice(message, isError = false) {
  const node = $("notice");
  node.textContent = message;
  node.className = `notice${isError ? " error" : ""}`;
}
function clearNotice() { $("notice").className = "notice hidden"; }

function queryCalls() {
  const query = new URLSearchParams({ page: state.page, pageSize: state.pageSize });
  if ($("search").value.trim()) query.set("search", $("search").value.trim());
  if ($("audioClass").value) query.set("audioClass", $("audioClass").value);
  if ($("callReviewStatus").value) query.set("reviewStatus", $("callReviewStatus").value);
  return `/api/ai/calls?${query}`;
}
async function refreshAll() {
  if (state.loading) return;
  state.loading = true;
  clearNotice();
  $("loadingCalls").textContent = "در حال دریافت…";
  try {
    if (new URLSearchParams(location.search).get("source") !== "api") {
      const batchResponse = await fetch(`/ai/batch-data.json?v=voice2`, { cache: "no-store" });
      if (batchResponse.ok) { await loadBatchData(null, batchResponse); return; }
    }
    const queueStatus = $("queueStatus").value;
    const requests = [api(queryCalls()), api(`/api/ai/reviews?status=${encodeURIComponent(queueStatus)}&take=200`), api("/api/ai/status")];
    if (queueStatus !== "OPEN") requests.push(api("/api/ai/reviews?status=OPEN&take=500"));
    const [calls, reviews, status, openReviews] = await Promise.all(requests);
    state.demoMode = false;
    state.batchMode = false;
    state.sampleDetails = {};
    state.calls = Array.isArray(calls) ? calls : [];
    state.hasNext = state.calls.length >= state.pageSize;
    state.reviews = Array.isArray(reviews) ? reviews : [];
    renderCalls();
    renderReviews();
    renderStatus(status, queueStatus === "OPEN" ? state.reviews : (openReviews || []));
    renderCoachingOverview(null);
    if (state.detailId) await loadDetail(state.detailId, false);
  } catch (error) {
    try { await loadBatchData(error); }
    catch { state.calls=[];state.reviews=[];state.hasNext=false;renderCalls();renderReviews();showNotice(error.message, true); }
  } finally {
    state.loading = false;
    $("loadingCalls").textContent = "";
  }
}

async function loadBatchData(cause, suppliedResponse = null) {
  let response = suppliedResponse || await fetch(`/ai/batch-data.json?v=voice2`, { cache: "no-store" });
  if (!response.ok && new URLSearchParams(location.search).get("demo") === "1") response = await fetch(`/ai/sample-data.json?v=voice2`, { cache: "no-store" });
  if (!response.ok) throw cause || new Error("داده batch در دسترس نیست.");
  const sample = await response.json();
  const search = $("search").value.trim().toLocaleLowerCase("fa");
  const audioClass = $("audioClass").value;
  const reviewStatus = $("callReviewStatus").value;
  const queueStatus = $("queueStatus").value;
  state.demoMode = true;
  state.batchMode = sample.dataMode === "MANUAL_BATCH";
  const allReviews = sample.reviews || [];
  state.sampleDetails = Object.fromEntries((sample.calls || []).map(call => [call.logicalCallId, {
    call,
    transcriptText: "متن کامل در خروجی محلی نگهداری می‌شود و برای حفاظت از اطلاعات تماس در حالت نمایشی شبکه منتشر نشده است.",
    segmentsJson: null,
    recording: call.sampleRecording || null,
    facts: call.sampleFacts || [],
    reviewItems: allReviews.filter(item => item.logicalCallId === call.logicalCallId),
    coaching: call.sampleCoaching || null
  }]));
  const filteredCalls = (sample.calls || []).filter(call =>
    (!search || JSON.stringify(call).toLocaleLowerCase("fa").includes(search)) &&
    (!audioClass || call.audioClass === audioClass) &&
    (!reviewStatus || (call.reviewStatuses || []).includes(reviewStatus)));
  const pageStart = (state.page - 1) * state.pageSize;
  state.calls = filteredCalls.slice(pageStart, pageStart + state.pageSize);
  state.hasNext = pageStart + state.pageSize < filteredCalls.length;
  state.reviews = allReviews.filter(item => queueStatus === "ALL" || item.reviewStatus === queueStatus);
  const metrics = sample.metrics || {};
  const analysisCount = Number(metrics.analysisCount || (sample.calls || []).length);
  const audioFileCount = Number(metrics.audioFileCount || 5);
  const durationMinutes = Number(metrics.totalDurationSeconds || 414) / 60;
  const totalMegabytes = Number(metrics.totalBytes || 6627000) / 1048576;
  $("sampleDataCount").textContent = `${fa(analysisCount)} تحلیل`;
  $("sampleDataHint").textContent = `${fa(audioFileCount)} فایل · ${fa(durationMinutes.toFixed(1))} دقیقه · ${fa(totalMegabytes.toFixed(1))} MB`;
  renderCalls();
  renderReviews();
  renderStatus(sample.status || {}, allReviews.filter(item => item.reviewStatus === "OPEN"));
  renderCoachingOverview(sample.coaching || null);
  const transcribedCount = Number(metrics.transcribedNewCount || 0);
  const readyCount = Number(metrics.coachingReadyCount || 0);
  showNotice(`داده batch محلی فعال است: ${fa(audioFileCount)} ویس، ${fa(transcribedCount)} متن اولیه و ${fa(readyCount)} مکالمه آماده راهنمایی فروش. اتصال مستقیم فعلاً لازم نیست.`);
  if (state.detailId && state.sampleDetails[state.detailId]) renderDetail(state.sampleDetails[state.detailId]);
}

function renderStatus(status, openReviews) {
  $("loadedCount").textContent = fa(state.calls.length);
  $("businessCount").textContent = fa(state.calls.filter(call => call.audioClass === "BUSINESS_CONVERSATION").length);
  $("openReviewCount").textContent = fa(openReviews.length);
  const ingestion = status?.recordingIngestion || {};
  if (state.demoMode) {
    $("ingestionState").textContent = state.batchMode ? "دستی / دوره‌ای" : "حالت نمونه";
    $("ingestionHint").textContent = state.batchMode ? "ویس‌های پوشه محلی خوانده شده‌اند" : "انتقال خودکار از ایزابل هنوز فعال نیست";
    return;
  }
  $("ingestionState").textContent = ingestion.enabled ? "فعال" : "غیرفعال";
  $("ingestionHint").textContent = ingestion.enabled
    ? (ingestion.credentialsConfigured ? "آماده دریافت فایل‌های امروز" : "اطلاعات اتصال کامل نیست")
    : (ingestion.configurationFilePresent ? "تنظیمات موجود است ولی دریافت خاموش است" : "فایل تنظیم اتصال Issabel نصب نشده است");
}
function renderCalls() {
  const rows = $("callRows");
  if (!state.calls.length) {
    rows.innerHTML = `<tr><td colspan="8"><div class="empty-state"><b>تماسی پیدا نشد</b><span>فیلترها یا وضعیت اجرای پردازش را بررسی کنید.</span></div></td></tr>`;
  } else {
    rows.innerHTML = state.calls.map(call => `<tr data-call-id="${call.logicalCallId}">
      <td>${date(call.startedAt)}</td><td><b>${esc(call.callKey)}</b><br><span class="muted">${fa(call.legCount)} leg</span></td>
      <td>${badge(call.audioClass || call.runStatus)}</td><td>${esc(call.internalExtension || "—")}</td><td>${percent(call.confidence)}</td>
      <td>${call.coachingScore == null ? "—" : `${fa(call.coachingScore)} از ۱۰۰`}</td><td>${call.openReviewCount ? `<span class="badge review">${fa(call.openReviewCount)}</span>` : "۰"}</td>
      <td><button class="secondary details" data-call-id="${call.logicalCallId}">جزئیات</button></td></tr>`).join("");
  }
  $("pageLabel").textContent = `صفحه ${fa(state.page)}`;
  $("prevPage").disabled = state.page <= 1;
  $("nextPage").disabled = !state.hasNext;
  rows.querySelectorAll("tr[data-call-id],button[data-call-id]").forEach(node => node.addEventListener("click", event => {
    event.stopPropagation();
    loadDetail(Number(node.dataset.callId));
  }));
}

async function loadDetail(id, announce = true) {
  state.detailId = id;
  if (announce) {
    $("emptyDetail").classList.add("hidden");
    $("detailContent").classList.remove("hidden");
    $("detailTitle").textContent = "در حال دریافت جزئیات…";
  }
  if (state.demoMode && state.sampleDetails[id]) {
    renderDetail(state.sampleDetails[id]);
    return;
  }
  try {
    const detail = await api(`/api/ai/calls/${id}`);
    renderDetail(detail);
  } catch (error) {
    showNotice(error.message, true);
  }
}
function renderDetail(detail) {
  const call = detail.call || {};
  $("detailTitle").textContent = call.callKey || `تماس ${call.logicalCallId}`;
  $("detailMeta").textContent = `${date(call.startedAt)} · داخلی ${call.internalExtension || "نامشخص"} · ${tr(call.direction)}`;
  $("detailSummary").textContent = call.summary || "جمع‌بندی ثبت نشده است.";
  renderRecording(detail.recording);
  renderTranscript(detail.segmentsJson, detail.transcriptText);
  renderFacts(detail.facts || []);
  renderCoaching(detail.coaching || call.sampleCoaching || null);
  $("detailReviews").innerHTML = renderReviewItems(detail.reviewItems || [], true);
  bindReviewActions($("detailReviews"));
}
function renderRecording(recording) {
  const box = $("recordingStatus");
  if (!recording) {
    box.innerHTML = `<b>دارایی ضبط ثبت نشده</b><span class="muted">این تماس ممکن است ضبط نداشته باشد یا هنوز کشف نشده باشد.</span>`;
    return;
  }
  const status = recording.processingStatus || "نامشخص";
  box.innerHTML = `<div>${badge(status)} <b>${esc(recording.originalFileName || "فایل ضبط")}</b></div>
    <span class="muted">حجم: ${recording.fileSizeBytes == null ? "—" : `${fa(recording.fileSizeBytes)} بایت`} · تکمیل: ${date(recording.completedAtUtc)}</span>
    ${recording.lastError ? `<span class="badge error">خطای پردازش ثبت شده؛ جزئیات فنی فقط در لاگ سرور است</span>` : ""}`;
}
function parseSegments(raw) {
  if (!raw) return [];
  try { const value = typeof raw === "string" ? JSON.parse(raw) : raw; return Array.isArray(value) ? value : []; }
  catch { return []; }
}
function renderTranscript(rawSegments, fullText) {
  const segments = parseSegments(rawSegments);
  if (segments.length) {
    $("transcript").innerHTML = segments.map(segment => `<div class="segment"><span class="time">${clock(segment.start ?? segment.startSeconds)}</span><span>${esc(segment.text || "")}</span></div>`).join("");
  } else {
    $("transcript").innerHTML = `<div class="full-text">${esc(fullText || "متنی ثبت نشده است.")}</div>`;
  }
}
function renderFacts(facts) {
  $("facts").innerHTML = facts.length ? facts.map(fact => `<article class="fact"><b>${esc(tr(fact.factType))}</b><span>${esc(fact.normalizedValue || fact.rawValue || "—")}${fact.unit ? ` ${esc(fact.unit)}` : ""}</span><small>${clock(fact.startSeconds)} · اطمینان ${percent(fact.confidence)} · ${esc(tr(fact.reviewStatus))}</small></article>`).join("") : `<span class="muted">داده ساختاری استخراج نشده است.</span>`;
}

function renderCoaching(coaching) {
  const box = $("coachingDetail");
  if (!coaching || coaching.status === "NOT_APPLICABLE") { box.innerHTML = `<div class="empty-coaching">این تماس برای راهنمایی فروش قابل استفاده نیست.</div>`; return; }
  const quality = coaching.quality || {}; const strengths = coaching.strengths || []; const actions = coaching.actions || [];
  box.innerHTML = `<div class="coach-score"><span>کیفیت متن: ${esc(tr(quality.status))}</span><strong>${coaching.score == null ? "—" : `${fa(coaching.score)} / ۱۰۰`}</strong><small>امتیاز پوشش اجزای مکالمه، نه نمره قطعی فروشنده</small></div>
    <div class="coach-columns"><div><h4>نقاط مثبت دیده‌شده</h4>${strengths.length ? strengths.map(item => `<p class="coach-good">✓ ${esc(item.label)}</p>`).join("") : `<p class="muted">نشانه قابل اتکایی در متن اولیه پیدا نشد.</p>`}</div>
    <div><h4>اقدام پیشنهادی</h4>${actions.length ? actions.map(item => `<article class="coach-action"><b>${esc(item.title)}</b><span>${esc(item.detail)}</span></article>`).join("") : `<p class="muted">اقدام فوری ثبت نشده است.</p>`}</div></div>`;
}

function renderCoachingOverview(coaching) {
  const section = $("coachingOverview"); if (!coaching) { section.classList.add("hidden"); return; } section.classList.remove("hidden");
  $("coachReadyCount").textContent = fa(coaching.readyConversationCount); $("transcriptReviewCount").textContent = fa(coaching.transcriptReviewCount); $("nonPurchaseFindingCount").textContent = fa(coaching.nonPurchaseFindingCount); $("sensitiveFindingCount").textContent = fa(coaching.sensitiveReviewCount);
  const renderRanked = items => items?.length ? items.map(item => `<li><span>${esc(item.label || item.title)}</span><b>${fa(item.count)}</b></li>`).join("") : `<li class="muted">مورد قابل اتکایی پیدا نشد.</li>`;
  $("topNonPurchaseReasons").innerHTML = renderRanked(coaching.topNonPurchaseReasons); $("topTopics").innerHTML = renderRanked(coaching.topTopics);
  $("priorityActions").innerHTML = coaching.priorityActions?.length ? coaching.priorityActions.map(item => `<article class="coach-action"><b>${esc(item.title)} <small>${fa(item.count)} تماس</small></b><span>${esc(item.detail)}</span></article>`).join("") : `<p class="muted">اقدامی ثبت نشده است.</p>`;
  $("coachingNotice").textContent = coaching.notice || "";
}

function renderReviewItems(items, compact = false) {
  if (!items.length) return `<div class="empty-state"><b>موردی وجود ندارد</b><span>صف این وضعیت خالی است.</span></div>`;
  return items.map(item => `<article class="review-item ${String(item.priority).toLowerCase()}">
    <div class="review-top"><div>${badge(item.reviewStatus)} ${badge(item.priority)}</div><b>${esc(tr(item.category))}</b></div>
    <p>${esc(item.rawText || item.resolution || "شاهد متنی ثبت نشده است.")}</p>
    ${item.recommendation ? `<p class="recommendation"><b>پیشنهاد:</b> ${esc(item.recommendation)}</p>` : ""}
    <div class="review-meta"><span>علت: ${esc(tr(item.reasonCode))}</span><span>زمان: ${clock(item.startSeconds)} تا ${clock(item.endSeconds)}</span>${compact ? "" : `<span>تماس: ${fa(item.logicalCallId)}</span>`}</div>
    ${item.resolution ? `<p class="muted">تصمیم ثبت‌شده: ${esc(item.resolution)}</p>` : ""}
    ${!state.demoMode && (item.reviewStatus === "OPEN" || item.reviewStatus === "DEFERRED") ? `<div class="review-actions">
      <button data-review-id="${item.reviewItemId}" data-action="CONFIRMED">تأیید</button>
      <button class="secondary" data-review-id="${item.reviewItemId}" data-action="CORRECTED">اصلاح</button>
      <button class="reject" data-review-id="${item.reviewItemId}" data-action="REJECTED">رد</button>
      <button class="defer" data-review-id="${item.reviewItemId}" data-action="DEFERRED">بعداً</button>
      ${compact ? "" : `<button class="secondary" data-open-call="${item.logicalCallId}">بازکردن تماس</button>`}
    </div>` : ""}
  </article>`).join("");
}
function renderReviews() {
  $("reviewRows").innerHTML = renderReviewItems(state.reviews);
  bindReviewActions($("reviewRows"));
}
function bindReviewActions(root) {
  root.querySelectorAll("button[data-review-id]").forEach(button => button.addEventListener("click", () => resolveReview(Number(button.dataset.reviewId), button.dataset.action)));
  root.querySelectorAll("button[data-open-call]").forEach(button => button.addEventListener("click", () => {
    loadDetail(Number(button.dataset.openCall));
    $("detailPanel").scrollIntoView({ behavior: "smooth", block: "start" });
  }));
}
async function resolveReview(id, status) {
  if (state.demoMode) {
    showNotice("ثبت تصمیم در حالت نمونه غیرفعال است؛ داده نمایشی به دیتابیس متصل نیست.");
    return;
  }
  let resolution = "";
  if (status === "CORRECTED") {
    resolution = window.prompt("متن صحیح یا توضیح اصلاح را وارد کنید:", "") || "";
    if (!resolution.trim()) return;
  } else if (status === "REJECTED") {
    resolution = window.prompt("دلیل رد این تشخیص (اختیاری):", "") || "تشخیص ماشینی رد شد";
  } else if (status === "CONFIRMED") {
    resolution = "توسط بازبین انسانی تأیید شد";
  } else {
    resolution = "برای بررسی بعدی موکول شد";
  }
  try {
    await api(`/api/ai/reviews/${id}/resolve`, { method: "POST", body: JSON.stringify({ status, resolution, resolvedBy: reviewer() || "dashboard-reviewer" }) });
    showNotice("تصمیم بازبینی ثبت شد.");
    await refreshAll();
  } catch (error) { showNotice(error.message, true); }
}

function saveAccess() {
  const enteredToken = $("apiToken").value.trim();
  if (enteredToken) sessionStorage.setItem("digiahanApiToken", enteredToken); else sessionStorage.removeItem("digiahanApiToken");
  localStorage.setItem("digiahanAiReviewer", $("reviewer").value.trim());
  state.page = 1;
  refreshAll();
}
let searchTimer;
$("saveAccess").addEventListener("click", saveAccess);
$("refresh").addEventListener("click", () => { state.page = 1; refreshAll(); });
$("audioClass").addEventListener("change", () => { state.page = 1; refreshAll(); });
$("callReviewStatus").addEventListener("change", () => { state.page = 1; refreshAll(); });
$("queueStatus").addEventListener("change", refreshAll);
$("search").addEventListener("input", () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => { state.page = 1; refreshAll(); }, 450); });
$("prevPage").addEventListener("click", () => { if (state.page > 1) { state.page--; refreshAll(); } });
$("nextPage").addEventListener("click", () => { state.page++; refreshAll(); });
$("closeDetail").addEventListener("click", () => { state.detailId = null; $("detailContent").classList.add("hidden"); $("emptyDetail").classList.remove("hidden"); });

$("apiToken").value = token();
$("reviewer").value = reviewer();
refreshAll();
