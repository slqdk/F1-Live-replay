// Adminside: status, F1 TV-login, optagelser, arkiv-import og log.

const $ = (id) => document.getElementById(id);

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const mb = (b) => (b == null ? "–" : b > 1073741824 ? `${(b / 1073741824).toFixed(1)} GB` : `${(b / 1048576).toFixed(1)} MB`);
const local = (iso, opts) => (iso ? new Date(iso).toLocaleString("da-DK", opts || { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }) : "–");
const clock = (ms) => {
  const s = Math.max(0, Math.floor((ms || 0) / 1000));
  return `${Math.floor(s / 3600)}:${String(Math.floor((s % 3600) / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
};
function until(iso) {
  if (!iso) return "";
  const d = Date.parse(iso) - Date.now();
  if (d <= 0) return "udløbet";
  const h = Math.floor(d / 3600000);
  return h >= 48 ? `om ${Math.floor(h / 24)} dage` : h >= 1 ? `om ${h} t` : `om ${Math.floor(d / 60000)} min`;
}

const MODE = { auto: "auto (efter sæsonplan)", always: "altid forbundet", off: "fra" };

function kv(el, pairs) {
  el.innerHTML = pairs.filter(Boolean).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${v}</dd>`).join("");
}

async function api(path, body) {
  const res = await fetch(path, body === undefined ? { cache: "no-store" } : {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
  });
  let data = {};
  try { data = await res.json(); } catch { /* tom */ }
  if (!res.ok && data.ok === undefined) data = { ok: false, error: `HTTP ${res.status}` };
  return data;
}

function say(id, data) {
  const el = $(id);
  el.className = "msg " + (data.ok === false ? "bad" : "ok");
  el.textContent = data.ok === false ? data.error || "fejl" : data.message || "OK";
}

async function refresh() {
  const s = await api("api/status");
  if (s.ok === false) {
    kv($("feedKv"), [["Fejl", `<span class="bad">${esc(s.error)}</span>`]]);
    return;
  }
  const f = s.feed || {};
  kv($("feedKv"), [
    ["Status", `<span class="${f.connected ? "ok" : "warn"}">${esc(f.status)}</span>`],
    ["Tilstand", esc(MODE[s.config?.feedMode] || s.config?.feedMode) + (s.config?.feedMode !== s.config?.configuredFeedMode ? ` (i .env: ${esc(MODE[s.config?.configuredFeedMode] || s.config?.configuredFeedMode)})` : "")],
    ["Hvorfor", esc(f.reason || "–")],
    ["F1 TV", f.usingToken ? `<span class="ok">ja</span>${f.telemetry ? ", telemetri flyder" : ""}` : "nej"],
    ["F1-stream", esc(s.streaming || "–")],
    ["Session", esc(s.session ? `${s.session.headline} (${s.session.status || "?"})` : "–")],
    ["Webklienter", esc(s.clients)],
    ["Server", `${esc(s.version)}, oppe ${clock(s.uptimeS * 1000)}`],
  ]);

  const t = s.token || {};
  kv($("tokenKv"), [
    ["Token", t.hasToken ? `<span class="ok">gyldigt</span> (${esc(t.source)})` : `<span class="bad">intet gyldigt token</span>`],
    t.hasToken && ["Abonnement", esc(`${t.product || "F1 TV"}, ${t.subscription || "?"}`)],
    t.expiresUtc && ["Udløber", `${local(t.expiresUtc)} (${until(t.expiresUtc)})`],
    ["Auto-login", t.credentials ? `<span class="ok">e-mail/adgangskode sat</span>` : `<span class="warn">F1_USERNAME/F1_PASSWORD mangler i .env</span>`],
    t.lastAttemptUtc && ["Seneste login", local(t.lastAttemptUtc)],
    t.nextAttemptUtc && ["Næste forsøg", local(t.nextAttemptUtc)],
    t.lastError && ["Fejl", `<span class="bad">${esc(t.lastError)}</span>`],
    t.pendingReconnect && ["Bemærk", "nyt token tages i brug efter sessionen"],
  ]);

  const r = s.recording;
  kv($("recKv"), r ? [
    ["Fil", esc(r.file)],
    ["Session", esc(r.session)],
    ["Startet", local(r.startedUtc)],
    ["Størrelse", `${mb(r.bytes)}, ${esc(r.lines)} linjer`],
    ["Telemetri", r.telemetry ? `<span class="ok">ja</span>` : "nej"],
  ] : [["Status", "ingen optagelse i gang"]]);
  $("btnClose").disabled = !r;

  kv($("nextKv"), (s.next || []).map((n) => [local(n.startUtc), esc(`${n.meeting}, ${n.name}`)]));

  const m = s.media || {};
  kv($("mediaKv"), [
    ["Teamradio hentet", esc(m.radioDownloaded)],
    m.radioWaitingForToken > 0 && ["Venter på token", esc(m.radioWaitingForToken)],
    ["Arkiver i kø", esc(m.archivesPending)],
    ["Optagelser", `${esc(s.disk?.recordings)} filer, ${mb(s.disk?.recordingsBytes)}`],
    ["Fri plads", mb(s.disk?.freeBytes)],
  ]);
  $("jobTable").innerHTML = (s.jobs || []).length
    ? `<tr><th>Job</th><th>Mål</th><th>Status</th></tr>` + s.jobs.map((j) =>
        `<tr><td>${esc(j.kind)}</td><td>${esc(j.target)}</td><td class="${j.state === "fejl" ? "bad" : j.state === "færdig" ? "ok" : "warn"}">${esc(j.state)}${j.message ? `: ${esc(j.message)}` : ""}</td></tr>`).join("")
    : `<tr><td>Ingen jobs endnu.</td></tr>`;

  const topics = Object.entries(s.topics || {});
  $("topicTable").innerHTML = topics.length
    ? `<tr><th>Topic</th><th class="num">Beskeder</th><th>Seneste</th></tr>` + topics.map(([k, v]) =>
        `<tr><td>${esc(k)}</td><td class="num">${esc(v.count)}</td><td>${local(v.lastUtc, { hour: "2-digit", minute: "2-digit", second: "2-digit" })}</td></tr>`).join("")
    : `<tr><td>Ingen beskeder i denne forbindelse endnu.</td></tr>`;
}

async function refreshRecordings() {
  const res = await fetch("recordings", { cache: "no-store" });
  const recs = res.ok ? await res.json() : [];
  if (!recs.length) {
    $("recTable").innerHTML = `<tr><td>Ingen optagelser endnu.</td></tr>`;
    return;
  }
  $("recTable").innerHTML = `<tr><th>Session</th><th>Start</th><th class="num">Varighed</th><th class="num">Størrelse</th><th>Kilde</th><th></th></tr>` +
    recs.map((r) => `<tr>
      <td>${esc(r.session || r.file)}${r.live ? ' <span class="bad">optager</span>' : ""}<br><small style="color:var(--muted)">${esc(r.file)}</small></td>
      <td>${local(r.started || r.modified)}</td>
      <td class="num">${clock(r.durationMs)}</td>
      <td class="num">${mb(r.sizeBytes)}</td>
      <td>${esc(r.source || "live")}</td>
      <td style="white-space:nowrap">
        <a class="pb-btn" href="./?mode=replay&file=${encodeURIComponent(r.file)}">Afspil</a>
        <a class="pb-btn" href="recordings/${encodeURIComponent(r.file)}" download>Hent</a>
        ${r.path ? `<button class="pb-btn" data-fetch="${esc(r.file)}">Hent arkiv</button>` : ""}
        ${r.media ? `<button class="pb-btn" data-convert="${esc(r.file)}">Byg fra arkiv</button>` : ""}
      </td></tr>`).join("");
  $("recTable").querySelectorAll("[data-fetch]").forEach((b) => b.addEventListener("click", async () =>
    say("recTableMsg", await api("api/archive/fetch", { file: b.dataset.fetch }))));
  $("recTable").querySelectorAll("[data-convert]").forEach((b) => b.addEventListener("click", async () =>
    say("recTableMsg", await api("api/archive/convert", { file: b.dataset.convert }))));
}

let logSeq = 0;
async function refreshLog() {
  const d = await api(`api/log?since=${logSeq}`);
  if (!d.lines?.length) return;
  const el = $("log");
  const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 20;
  for (const l of d.lines) {
    const t = new Date(l.utc).toLocaleTimeString("da-DK", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    el.append(`${t}  ${l.text}\n`);
  }
  logSeq = d.last;
  if (el.textContent.length > 400000) el.textContent = el.textContent.slice(-300000);
  if (atBottom) el.scrollTop = el.scrollHeight;
}

// ---------- Handlinger ----------

document.querySelectorAll("[data-feed]").forEach((b) => b.addEventListener("click", async () => {
  say("feedMsg", await api("api/feed", { action: b.dataset.feed }));
  refresh();
}));
$("btnLogin").addEventListener("click", async () => {
  $("tokenMsg").textContent = "Logger ind…";
  say("tokenMsg", await api("api/login", {}));
  refresh();
});
$("btnToken").addEventListener("click", async () => {
  const value = $("tokenInput").value.trim();
  if (!value) return;
  const d = await api("api/token", { value });
  say("tokenMsg", d);
  if (d.ok !== false) $("tokenInput").value = "";
  refresh();
});
$("btnClose").addEventListener("click", async () => {
  if (!confirm("Afslut den igangværende optagelse? En ny startes først ved næste session.")) return;
  say("recMsg", await api("api/feed", { action: "close-recording" }));
  refresh();
  refreshRecordings();
});
$("btnSchedule").addEventListener("click", async () => { await api("api/schedule/refresh", {}); refresh(); });

const yearSel = $("year");
for (let y = new Date().getFullYear(); y >= 2018; y--) yearSel.add(new Option(String(y), String(y)));
$("btnSeason").addEventListener("click", async () => {
  $("season").innerHTML = "";
  $("seasonMsg").textContent = "Henter sæsonindeks…";
  const d = await api(`api/archive/season?year=${yearSel.value}`);
  if (d.ok === false) { say("seasonMsg", d); return; }
  $("seasonMsg").textContent = "";
  $("season").innerHTML = (d.meetings || []).map((m) =>
    `<div class="season-meeting">${esc(m.name)}</div><table>` +
    (m.sessions || []).map((s) => `<tr><td>${esc(s.name)}</td><td>${local(s.startDate)}</td><td style="text-align:right">` +
      (s.recorded ? `<span class="ok">optaget</span>` : s.path
        ? `<button class="pb-btn" data-import="${esc(s.path)}" data-meeting="${esc(m.name)}" data-name="${esc(s.name)}" data-start="${esc(s.startDate)}">Importér</button>`
        : "") + `</td></tr>`).join("") + `</table>`).join("");
  $("season").querySelectorAll("[data-import]").forEach((b) => b.addEventListener("click", async () => {
    b.disabled = true;
    say("seasonMsg", await api("api/archive/import", {
      path: b.dataset.import, meeting: b.dataset.meeting, name: b.dataset.name, startDate: b.dataset.start,
    }));
    refresh();
  }));
});

await refresh();
await refreshRecordings();
await refreshLog();
setInterval(refresh, 5000);
setInterval(refreshRecordings, 30000);
setInterval(refreshLog, 3000);
