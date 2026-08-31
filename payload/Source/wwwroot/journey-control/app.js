"use strict";
const $ = (id) => document.getElementById(id);
const faNumber = new Intl.NumberFormat("fa-IR");
const faDate = new Intl.DateTimeFormat("fa-IR-u-ca-persian", { dateStyle: "medium", timeStyle: "short" });
const escapeHtml = (value) => String(value ?? "").replace(/[&<>'"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[ch]));

async function load() {
  $("message").classList.remove("hidden");
  $("exceptionsTable").classList.add("hidden");
  try {
    const response = await fetch("/api/journey-control/exceptions?take=300", { credentials: "same-origin" });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `خطای سرویس (${response.status})`);
    $("exceptionCount").textContent = faNumber.format(body.length);
    if (!body.length) { $("message").textContent = "هیچ مورد عبورکرده از SLA وجود ندارد."; return; }
    $("exceptionsBody").innerHTML = body.map((row) => `<tr>
      <td class="customer"><strong>${escapeHtml(row.customerDisplayName)}</strong><small>${escapeHtml(row.primaryPhone || "شماره ثبت نشده")}</small></td>
      <td>${escapeHtml(row.ownerSellerKey)}</td><td>${escapeHtml(row.title)}</td>
      <td>${faDate.format(new Date(row.dueAtUtc))}</td><td class="late">${faNumber.format(row.overdueMinutes)} دقیقه</td>
      <td><span class="status">${escapeHtml(row.opportunityStage || row.leadStatus || row.workType)}</span></td></tr>`).join("");
    $("message").classList.add("hidden");
    $("exceptionsTable").classList.remove("hidden");
  } catch (error) {
    $("message").textContent = error.message;
    $("exceptionCount").textContent = "—";
  }
}

$("refresh").addEventListener("click", load);
load();
