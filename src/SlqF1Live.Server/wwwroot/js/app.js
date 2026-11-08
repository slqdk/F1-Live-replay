// SLQ F1 Live — visning. Tre tilstande:
//   Live       SSE fra /events (samme kontrakt som live.html på f1.slq.dk)
//   Forsinket  den igangværende optagelse afspillet X sekunder bag live
//   Afspilning en optagelse afspillet i browseren (seek, hastighed, sync)
// URL: ?mode=live|delay|replay&file=<fil.f1rec>&t=<sek> — bogmærk fx en TV-visning.

import { deepMerge } from "./merge.js";
import { raceOrder, pitLossNow, predictPit, describePit, secondsOf } from "./pit.js";
import { RecordingPlayer } from "./player.js";
import { renderHeader, renderTower, renderRaceControl, renderRadio, renderRadioFilter, TrackMap, fmtClock, tla, teamColour } from "./render.js";
import { LapChart, lapSeries, lapRows, paceStats, seriesStyles, fmtLap, fmtSec, gapSeries, gapTrend, stintRanges, COMPOUND } from "./lapchart.js";

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);

const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : v; } catch { return d; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* privat vindue */ } },
};

const app = {
  mode: "live",
  liveState: {},
  liveSource: null,
  liveStatus: "forbinder…",
  player: new RecordingPlayer(),
  pub: null,
  clockOffsetMs: 0,
  map: new TrackMap($("map")),
  seenRadio: null,
  armedSync: false,
};

// ---------- Server-status (aktiv optagelse, næste session) ----------

async function refreshPublic() {
  try {
    const t0 = Date.now();
    const res = await fetch("api/public", { cache: "no-store" });
    if (!res.ok) return;
    app.pub = await res.json();
    const t1 = Date.now();
    if (app.pub.serverUtc) app.clockOffsetMs = Date.parse(app.pub.serverUtc) - (t0 + t1) / 2;
    app.player.clockOffsetMs = app.clockOffsetMs;
  } catch { /* offline — prøv igen */ }
}

// ---------- Live (SSE) ----------

function startLive() {
  stopLive();
  const es = new EventSource("events?exclude=CarData");
  app.liveSource = es;
  es.addEventListener("snapshot", (ev) => {
    try { app.liveState = JSON.parse(ev.data).state || {}; } catch { /* ignorér */ }
  });
  es.addEventListener("feed", (ev) => {
    try {
      const { topic, data } = JSON.parse(ev.data);
      if (topic) app.liveState[topic] = deepMerge(app.liveState[topic], data);
    } catch { /* ignorér */ }
  });
  es.addEventListener("status", (ev) => {
    try { app.liveStatus = JSON.parse(ev.data).status; } catch { /* ignorér */ }
  });
  es.onopen = () => { app.liveStatus = "forbundet til serveren"; };
  es.onerror = () => { app.liveStatus = "mistede forbindelsen — prøver igen"; };
}

function stopLive() {
  if (app.liveSource) { app.liveSource.close(); app.liveSource = null; }
}

// ---------- Tilstande ----------

async function setMode(mode, { file, t } = {}) {
  app.mode = mode;
  document.body.dataset.mode = mode;
  document.querySelectorAll(".modes button").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.mode === mode)));
  $("playbar").hidden = mode === "live";
  $("syncHint").hidden = true;
  updateUrl();

  if (mode === "live") {
    app.player.dispose();
    startLive();
    return;
  }
  stopLive();
  if (mode === "delay") {
    await refreshPublic();
    const rec = app.pub?.recording;
    if (!rec) {
      app.player.dispose();
      setLoadState("Ingen optagelse i gang. Forsinket live virker under en session; brug Afspilning for ældre sessioner.");
      $("recTitle").textContent = "";
      return;
    }
    await loadRecording(rec.file, { follow: true });
    app.player.setDelay(Number(store.get("f1delay", "30")) * 1000);
    app.player.play();
    return;
  }
  // replay
  if (file) await loadRecording(file, { follow: false, t });
  else if (!app.player.file || app.player.follow) {
    app.player.dispose();
    $("recTitle").textContent = "Ingen optagelse valgt";
    openRecordings();
  }
}

function setLoadState(text) { $("loadState").textContent = text; }

async function loadRecording(file, { follow, t } = {}) {
  setLoadState("Indlæser…");
  $("recTitle").textContent = file.replace(/\.f1rec$/, "").replace(/_/g, " ");
  try {
    await app.player.load(file, {
      profile: "map",
      follow,
      onProgress: (got, total) => setLoadState(total ? `Indlæser ${Math.round((got / total) * 100)} %` : `Indlæser ${(got / 1048576).toFixed(1)} MB`),
    });
    app.player.clockOffsetMs = app.clockOffsetMs;
    const h = app.player.header;
    if (h?.session) $("recTitle").textContent = h.session;
    setLoadState(follow ? "" : `${fmtClock(app.player.durationMs)} optaget`);
    if (t) app.player.seek(Number(t) * 1000);
    app.seenRadio = null;
    buildOutline(app.player);
    updateUrl();
  } catch (e) {
    setLoadState(`Kunne ikke indlæse optagelsen (${e.message}).`);
  }
}

/** Banen (nøgle + år) til banekortet. */
function circuitOf(si) {
  return { key: si?.Meeting?.Circuit?.Key ?? si?.Meeting?.Key ?? null, year: Number(String(si?.Path || si?.StartDate || "").slice(0, 4)) || null };
}

/** Banekortets omrids med det samme: stikprøver af optagelsens Position-linjer
 *  (reserve, hvis serveren ikke har banens rigtige omrids). */
function buildOutline(player) {
  const c = circuitOf(player.state.SessionInfo);
  app.map.setCircuit(c.key, c.year);
  const pos = player.entries.filter((e) => e.topic === "Position");
  const step = Math.max(1, Math.ceil(pos.length / 700));
  for (let i = 0; i < pos.length; i += step) {
    try {
      const samples = JSON.parse(pos[i].line).data?.Position;
      if (Array.isArray(samples)) for (const s of samples) app.map.addPoints(s.Entries);
    } catch { /* spring over */ }
  }
  app.map.save();
}

function updateUrl() {
  const p = new URLSearchParams();
  if (app.mode !== "live") p.set("mode", app.mode);
  if (app.mode === "replay" && app.player.file) p.set("file", app.player.file);
  const key = params.get("key");
  if (key) p.set("key", key);
  const qs = p.toString();
  history.replaceState(null, "", qs ? `?${qs}` : location.pathname);
}

// ---------- Optagelsesliste ----------

async function openRecordings() {
  const dlg = $("recDialog");
  const list = $("recList");
  list.innerHTML = `<p class="empty">Henter optagelser…</p>`;
  if (!dlg.open) dlg.showModal();
  try {
    const res = await fetch("recordings", { cache: "no-store" });
    const recs = await res.json();
    if (!recs.length) {
      list.innerHTML = `<p class="empty">Ingen optagelser endnu. Serveren optager automatisk hver session, når den kører.</p>`;
      return;
    }
    const groups = new Map();
    for (const r of recs) {
      const g = r.meeting || (r.session || r.file).split(" — ")[0] || "Andre";
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g).push(r);
    }
    list.innerHTML = "";
    for (const [g, items] of groups) {
      const sec = document.createElement("section");
      sec.className = "rec-group";
      sec.innerHTML = `<h3>${escapeHtml(g)}</h3>`;
      for (const r of items.sort((a, b) => String(a.started || a.modified).localeCompare(String(b.started || b.modified)))) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = "rec-item";
        const when = r.started ? new Date(r.started).toLocaleString("da-DK", { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }) : "";
        const name = r.sessionName || r.session || r.file;
        b.innerHTML =
          `<span class="name">${escapeHtml(name)}${r.source === "archive" ? " (arkiv)" : ""}</span>` +
          `<span class="meta">${r.live ? '<span class="live">Optager nu</span>' : escapeHtml(when)}</span>` +
          `<span class="meta">${fmtClock(r.durationMs)}, ${(r.sizeBytes / 1048576).toFixed(0)} MB</span>`;
        b.addEventListener("click", async () => {
          dlg.close();
          if (app.mode !== "replay") await setMode("replay", { file: r.file });
          else await loadRecording(r.file, { follow: false });
        });
        sec.appendChild(b);
      }
      list.appendChild(sec);
    }
  } catch (e) {
    list.innerHTML = `<p class="empty">Kunne ikke hente listen (${escapeHtml(e.message)}).</p>`;
  }
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// ---------- Teamradio ----------

const audio = $("audio");
let playingBtn = null;

function playRadio(src, btn) {
  if (!src) return;
  if (playingBtn) playingBtn.classList.remove("playing");
  if (audio.dataset.src === src && !audio.paused) { audio.pause(); return; }
  audio.src = src;
  audio.dataset.src = src;
  audio.play().catch(() => {});
  playingBtn = btn || null;
  if (btn) btn.classList.add("playing");
}
audio.addEventListener("ended", () => { if (playingBtn) playingBtn.classList.remove("playing"); playingBtn = null; });

function mediaStem() {
  if (app.mode === "live") return app.pub?.recording?.stem || null;
  const f = app.player.file;
  return f ? f.replace(/\.f1rec$/, "").replace(/_arkiv$/, "") : null;
}

function autoRadio(state, caps) {
  const last = caps[caps.length - 1];
  const key = last ? `${last.Utc}|${last.Path}` : "";
  if (app.seenRadio === null) { app.seenRadio = key; return; }
  if (key && key !== app.seenRadio) {
    app.seenRadio = key;
    const playing = app.mode === "live" || app.player.playing;
    if ($("radioAuto").checked && playing && last?.Path && mediaStem() && !radio.hidden.has(tla(state, last.RacingNumber))) {
      const src = `media/${encodeURIComponent(mediaStem())}/${last.Path.split("/").map(encodeURIComponent).join("/")}`;
      playRadio(src, null);
    }
  }
}

// ---------- Teamradio: tekst og kørerfilter ----------
// Serveren skriver klippenes tekst i <stem>/TeamRadio/transcripts.json, efterhånden
// som de bliver transskriberet. Kun klip der er med i (den afspillede) tilstand
// vises, så hele filen afslører intet om resten af sessionen.

const radio = {
  hidden: new Set(store.get("f1radioHidden", "").split(",").filter(Boolean)),
  texts: {}, textsVersion: 0, stem: null, fetchedAt: 0, busy: false,
};

function setHidden(next) {
  radio.hidden = next;
  store.set("f1radioHidden", [...next].join(","));
}

function updateFilterLabel(drivers) {
  const total = drivers.length;
  const shown = drivers.filter((d) => !radio.hidden.has(d.tla)).length;
  $("radioFilterBtn").textContent = !total || shown === total ? "Kørere: alle" : `Kørere: ${shown} af ${total}`;
}

async function pollTexts(stem, caps, clockMs) {
  if (!stem || !app.pub?.transcribe?.enabled) return;
  if (radio.stem !== stem) { radio.stem = stem; radio.texts = {}; radio.textsVersion++; radio.fetchedAt = 0; }
  // Vent kun på tekst til nye klip (en mp3 der aldrig kom, skal ikke holde os i gang)
  const fresh = (c) => app.mode === "replay" || clockMs - Date.parse(c.Utc) < 3 * 60 * 1000;
  const waiting = caps.slice(-40).some((c) => c.Path && !radio.texts[c.Path] && fresh(c));
  if (radio.fetchedAt && !waiting) return;
  const every = app.mode === "replay" ? 30000 : 4000;
  if (radio.busy || Date.now() - radio.fetchedAt < every) return;
  radio.busy = true;
  radio.fetchedAt = Date.now();
  try {
    const res = await fetch(`media/${encodeURIComponent(stem)}/TeamRadio/transcripts.json`, { cache: "no-store" });
    if (res.ok) {
      const data = await res.json();
      if (radio.stem === stem) { radio.texts = data.clips || {}; radio.textsVersion++; }
    }
  } catch { /* næste forsøg */ } finally { radio.busy = false; }
}

$("radioFilterBtn").addEventListener("click", () => {
  const open = $("radioFilter").hidden;
  $("radioFilter").hidden = !open;
  $("radioFilterBtn").setAttribute("aria-expanded", String(open));
});
$("radioFilter").addEventListener("click", (e) => {
  const act = e.target.closest("[data-rf]")?.dataset.rf;
  if (!act) return;
  const state = app.mode === "live" ? app.liveState : app.player.state;
  const all = Object.values(state.DriverList || {}).filter((d) => d?.RacingNumber).map((d) => tla(state, d.RacingNumber));
  setHidden(act === "none" ? new Set(all) : new Set());
});
function toggleDriver(code) {
  const next = new Set(radio.hidden);
  if (next.has(code)) next.delete(code); else next.add(code);
  setHidden(next);
}

// ---------- Pitprognose (kun i løb) ----------
// Klik på en kører i tårnet: hvor kommer han ud, hvis han pitter nu?

const isRaceSession = (state) => state.SessionInfo?.Type === "Race";

function togglePit(num) {
  const state = app.mode === "live" ? app.liveState : app.player.state;
  if (!isRaceSession(state)) return;
  app.pitPick = app.pitPick === num ? null : num;
  lastBigRender = 0; // tegn med det samme
}

$("tower").addEventListener("click", (e) => {
  if (e.target.closest(".row.ghost")) { togglePit(app.pitPick); return; } // luk igen
  const row = e.target.closest(".row[data-num]");
  if (row) (cmp.on ? toggleCmpPick : togglePit)(row.dataset.num);
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && app.pitPick) { app.pitPick = null; lastBigRender = 0; }
});
$("tower").addEventListener("keydown", (e) => {
  if (e.key !== "Enter" && e.key !== " ") return;
  const row = e.target.closest(".row[data-num]");
  if (row) { e.preventDefault(); (cmp.on ? toggleCmpPick : togglePit)(row.dataset.num); }
});

/** Typisk omgangstid lige nu (median af sidste omgang) — til kortets spøgelse. */
function medianLap(state) {
  const t = Object.values(state.TimingData?.Lines || {})
    .map((l) => { const v = String(l?.LastLapTime?.Value || ""); const m = /^(\d+):(\d+\.\d+)$/.exec(v); return m ? Number(m[1]) * 60 + Number(m[2]) : secondsOf(v); })
    .filter((v) => v && v > 30 && v < 300)
    .sort((a, b) => a - b);
  return t.length ? t[t.length >> 1] : 90;
}

function pitInfo(state) {
  const session = state.SessionInfo?.Path || "";
  if (session !== app.pitSession) { app.pitSession = session; app.pitPick = null; } // ny session: nulstil
  if (!app.pitPick || !isRaceSession(state)) return null;
  const order = raceOrder(state);
  const loss = pitLossNow(state, app.map.circuitRaw, app.pitPick);
  const p = predictPit(order, app.pitPick, loss.seconds);
  if (!p) { app.pitPick = null; return null; }
  return {
    num: p.num,
    afterNum: p.inPit || loss.kind === "red" ? p.num : p.ahead?.num ?? null,
    ...describePit(p, loss, (n) => tla(state, n)),
    ghost: p.inPit || loss.kind === "red" ? { num: p.num } : {
      num: p.num, aheadNum: p.ahead?.num ?? null, behindNum: p.behind?.num ?? null,
      toAhead: p.ahead?.by ?? 0, toBehind: p.behind?.by ?? 0, lapTime: medianLap(state),
    },
  };
}

// ---------- Sammenlign omgangstider ----------
// "Sammenlign" i topbjælken: klik på to kørere i tårnet → deres omgangstider
// som graf. Omgangene kommer fra serveren (/api/laps, ud fra optagelsen); i
// forsinket visning og afspilning vises kun omgange op til det sted, man er.

const cmp = { on: false, picks: [null, null], last: 1, data: null, inflight: "", error: "", sig: "", view: "laps", timer: null };
try {
  const saved = JSON.parse(store.get("f1cmp", "{}"));
  cmp.on = saved.on === true;
  if (["laps", "gap", "table"].includes(saved.view)) cmp.view = saved.view;
  if (Array.isArray(saved.picks)) cmp.picks = [0, 1].map((i) => (/^\d+$/.test(saved.picks[i]) ? saved.picks[i] : null));
} catch { /* ugyldig gemt værdi */ }
const lapChart = new LapChart($("cmpChart"), $("cmpTip"));

const picked = () => cmp.picks.filter(Boolean);
const saveCmp = () => store.set("f1cmp", JSON.stringify({ on: cmp.on, picks: cmp.picks, view: cmp.view }));
const lapFile = () => (app.mode === "live" ? app.pub?.recording?.file || null : app.player.file);
const lapKey = () => `${lapFile()}|${picked().join(",")}`;

function setCompare(on) {
  cmp.on = on;
  document.body.classList.toggle("comparing", on);
  $("cmpBtn").setAttribute("aria-pressed", String(on));
  clearInterval(cmp.timer);
  cmp.timer = on ? setInterval(fetchLaps, 10000) : null;
  if (on) { app.pitPick = null; fetchLaps(); }
  cmp.sig = "";
  saveCmp();
  lastBigRender = 0;
}

/** To faste pladser (A fuld streg, B stiplet), så en kører beholder sin
 *  stil, når den anden skiftes. Er begge optaget, erstattes den ældste. */
function toggleCmpPick(num) {
  const i = cmp.picks.indexOf(num);
  if (i >= 0) cmp.picks[i] = null;
  else {
    const free = cmp.picks.indexOf(null);
    const slot = free >= 0 ? free : 1 - cmp.last;
    cmp.picks[slot] = num;
    cmp.last = slot;
    // Telefon: grafen står under tårnet — vis den, når nummer to er valgt
    if (picked().length === 2 && matchMedia("(max-width: 960px)").matches) {
      requestAnimationFrame(() => $("cmpPanel").scrollIntoView({ behavior: "smooth", block: "nearest" }));
    }
  }
  saveCmp();
  cmp.sig = "";
  fetchLaps();
  lastBigRender = 0;
}

async function fetchLaps() {
  const file = lapFile(), key = lapKey();
  if (!cmp.on || !file || !picked().length || cmp.inflight === key) return;
  cmp.inflight = key;
  try {
    const res = await fetch(`api/laps?file=${encodeURIComponent(file)}&drivers=${picked().join(",")}`, { cache: "no-store" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const j = await res.json();
    if (lapFile() === file) { cmp.data = { key, file, drivers: j.drivers || {} }; cmp.error = ""; }
  } catch {
    cmp.error = "Kunne ikke hente omgangstiderne. Prøver igen om lidt.";
  } finally {
    if (cmp.inflight === key) cmp.inflight = "";
    cmp.sig = "";
  }
}

$("cmpBtn").addEventListener("click", () => setCompare(!cmp.on));
function setCmpView(view) {
  cmp.view = view;
  document.querySelectorAll("[data-cmpview]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.cmpview === view)));
  saveCmp();
  cmp.sig = "";
  lastBigRender = 0;
}
document.querySelectorAll("[data-cmpview]").forEach((b) => b.addEventListener("click", () => setCmpView(b.dataset.cmpview)));
$("cmpLegend").addEventListener("click", (e) => {
  const chip = e.target.closest("[data-num]");
  if (chip) toggleCmpPick(chip.dataset.num);
});

const escHtml = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const swatch = (st) => `<svg class="sw" width="22" height="8" aria-hidden="true"><line x1="1" x2="21" y1="4" y2="4" stroke="${st.colour}"${st.dash ? ' stroke-dasharray="5 3"' : ""}/></svg>`;
const flagNote = (l) => (l?.flags ? ` <small>${[...l.flags].map((f) => ({ P: "pit", S: "SC", V: "VSC", R: "rødt" })[f]).join(" ")}</small>` : "");
/** "NOR 0,154 s" — hvem der er hurtigst og med hvor meget (a, b i sekunder). */
const faster = (a, b, tA, tB, dec = 3) => (a == null || b == null ? "" : Math.abs(a - b) < 0.0005 ? "lige" : `${escHtml(a < b ? tA : tB)} ${fmtSec(a - b, dec)}`);

/** Stationære stoptider (s) for en kører fra PitStopSeries. */
function stopTimes(state, num) {
  const out = [];
  const walk = (v) => {
    if (!v || typeof v !== "object") return;
    if (String(v.RacingNumber) === num && Number.isFinite(Number(v.PitStopTime)) && Number(v.PitStopTime) > 0) out.push(Number(v.PitStopTime));
    for (const k of Object.keys(v)) walk(v[k]);
  };
  walk(state.PitStopSeries);
  return out;
}

/** Ekstra rækker i løb: afstand + tendens, dæk nu, pitstop, fartfælde. */
function raceRows(state, A, B, tr) {
  let html = "";
  const line = (n) => state.TimingData?.Lines?.[n] || {};
  if (B) {
    const gaps = gapSeries(A.series, B.series);
    const cur = gaps[gaps.length - 1];
    if (cur) {
      const [lead, trail] = cur.gap >= 0 ? [A.tla, B.tla] : [B.tla, A.tla];
      let txt = `${escHtml(trail)} ${fmtSec(cur.gap, cur.approx ? 1 : 3)} bag ${escHtml(lead)} <small>omg. ${cur.lap}</small>`;
      const trend = gapTrend(gaps, 5);
      if (trend) {
        const closing = -trend.slope * Math.sign(cur.gap || 1); // > 0: bagerste henter
        if (closing > 0.05) {
          txt += ` · ${escHtml(trail)} henter ${fmtSec(closing, 2)}/omg.`;
          const left = Number(state.LapCount?.TotalLaps) - Number(state.LapCount?.CurrentLap);
          const n = Math.ceil(Math.abs(cur.gap) / closing);
          if (Number.isFinite(left) && left > 0 && n <= left) txt += ` <small>(når ham om ca. ${n} omg.)</small>`;
        } else if (closing < -0.05) txt += ` · ${escHtml(lead)} trækker fra med ${fmtSec(-closing, 2)}/omg.`;
        else txt += " · stabilt";
      }
      html += `<tr><th scope="row" title="Afstand efter seneste fælles omgang; tendens over de sidste grønne omgange">Afstand</th><td colspan="3" class="wide">${txt}</td></tr>`;
    }
  }
  const tyre = (x) => {
    const st = x.stints[x.stints.length - 1];
    const cur = Object.values(state.TimingAppData?.Lines?.[x.num]?.Stints || {}).pop();
    if (!st) return "–";
    const age = Number(cur?.TotalLaps) || st.laps;
    return `<span class="tyre-dot" style="background:${st.colour}"></span>${st.letter} <small>${age} omg.</small>`;
  };
  html += tr("Dæk nu", "Dækblanding og omgange på dækket", tyre(A), B ? tyre(B) : "", "");
  const stops = (x) => {
    const n = Number(line(x.num).NumberOfPitStops) || 0;
    const t = stopTimes(state, x.num);
    return `${n}${t.length ? ` <small>${t.map((v) => v.toFixed(1).replace(".", ",")).join(" · ")} s</small>` : ""}`;
  };
  html += tr("Stop", "Antal pitstop og stoptider (stillestående)", stops(A), B ? stops(B) : "", "");
  // Topfart: højeste af F1's fartmålinger (I1, I2, mållinje, fartfælde) på seneste omgang
  const top = (x) => Math.max(0, ...Object.values(line(x.num).Speeds || {}).map((v) => Number(v?.Value) || 0)) || null;
  const sa = top(A), sb = B ? top(B) : null;
  if (sa || sb) {
    const diff = sa && sb && sa !== sb ? `${escHtml(sa > sb ? A.tla : B.tla)} +${Math.abs(sa - sb)} km/t` : "";
    html += tr("Topfart", "Højeste af F1's fartmålinger (I1, I2, mållinje, fartfælde) på seneste omgang",
      sa ? `${sa} <small>km/t</small>` : "–", sb ? `${sb} <small>km/t</small>` : "–", diff);
  }
  return html;
}

function renderCompare(state) {
  if (!cmp.on) return;
  const file = lapFile();
  if (file && picked().length && cmp.data?.key !== lapKey() && cmp.inflight !== lapKey()) fetchLaps();
  const cutoff = app.mode === "live" ? Infinity : app.player.positionMs;
  const data = cmp.data?.file === file ? cmp.data.drivers : null;
  const chartEl = $("cmpChart");
  const styles = seriesStyles(cmp.picks[0] ? teamColour(state, cmp.picks[0]) : null, cmp.picks[1] ? teamColour(state, cmp.picks[1]) : null);
  // Valgte pladser: [{num, tla, series, style}] — A før B; en plads kan være tom
  const sel = cmp.picks.map((num, i) => num && {
    num, tla: tla(state, num), series: data ? lapSeries(data[num], cutoff) : [],
    style: i ? styles.b || seriesStyles(teamColour(state, num), teamColour(state, num)).b : styles.a,
  });
  const used = sel.filter(Boolean);
  for (const x of used) x.stints = stintRanges(state.TimingAppData?.Lines?.[x.num]?.Stints);
  const lines = state.TimingData?.Lines || {};
  const sig = [file, cmp.picks.join(","), ...used.map((x) => `${x.tla}:${x.series.length}:${x.series.at(-1)?.s}:${x.series.at(-1)?.g}:` +
      `${x.stints.map((q) => q.letter + q.laps).join("")}:${lines[x.num]?.NumberOfPitStops}:${Object.values(lines[x.num]?.Speeds || {}).map((v) => v?.Value).join(",")}`),
    chartEl.clientWidth, chartEl.clientHeight, cmp.view, cmp.error].join("|");
  if (sig === cmp.sig) return;
  cmp.sig = sig;

  $("cmpLegend").innerHTML = used
    .map((x) => `<button type="button" class="cmp-chip" data-num="${x.num}" title="Fjern ${escHtml(x.tla)}">${swatch(x.style)}${escHtml(x.tla)}<span class="x" aria-hidden="true">×</span><span class="sr">fjern</span></button>`).join("");

  let hint = "";
  if (!file) hint = app.mode === "replay" ? "Vælg en optagelse for at se omgangstider." : "Ingen optagelse at hente omgange fra lige nu.";
  else if (!used.length) hint = "Klik på to kørere i tårnet for at sammenligne deres omgangstider.";
  else if (cmp.error) hint = cmp.error;
  else if (!data) hint = "Henter omgangstider…";
  else if (!used.some((x) => x.series.length)) hint = "Ingen omgangstider endnu.";
  else if (used.length === 1) hint = `${used[0].tla} er valgt. Klik på en kører mere${cmp.view === "gap" ? " for at se afstanden mellem dem" : ""}.`;
  $("cmpHint").textContent = hint;
  $("cmpHint").hidden = !hint;

  const part = (x) => x && { tla: x.tla, series: x.series, stints: x.stints, ...x.style };
  const table = cmp.view === "table";
  chartEl.hidden = table;
  $("cmpTable").hidden = !table;
  if (!table) lapChart.render({ kind: cmp.view, a: part(sel[0]), b: part(sel[1]) });

  // Nøgletal: seneste fælles omgang, snit af de sidste fælles grønne omgange, bedste
  const [A, B] = used; // A = første valgte plads, B = anden (hvis to)
  const stats = $("cmpStats");
  if (!A || !used.some((x) => x.series.length)) stats.innerHTML = "";
  else {
    const p = paceStats(A.series, B ? B.series : null, 5);
    const rows = lapRows(A.series, B ? B.series : []).filter((r) => r.a?.s != null && (!B || r.b?.s != null));
    const last = rows[rows.length - 1];
    const cell = (l) => (l?.s != null ? `${fmtLap(l.s)}${flagNote(l)}` : "–");
    const range = p.laps.length ? `omg. ${p.laps[0]}${p.laps.length > 1 ? `–${p.laps[p.laps.length - 1]}` : ""}` : "";
    const head = `<thead><tr><th></th><th scope="col">${swatch(A.style)}${escHtml(A.tla)}</th>${B ? `<th scope="col">${swatch(B.style)}${escHtml(B.tla)}</th><th scope="col">Hurtigst</th>` : ""}</tr></thead>`;
    const tr = (label, title, a, b, fa) => `<tr><th scope="row"${title ? ` title="${escHtml(title)}"` : ""}>${label}</th><td>${a}</td>${B ? `<td>${b}</td><td>${fa}</td>` : ""}</tr>`;
    stats.innerHTML = head + "<tbody>" +
      (last ? tr(`Seneste <small>omg. ${last.lap}</small>`, "", cell(last.a), cell(last.b), faster(last.a?.s, last.b?.s, A.tla, B?.tla)) : "") +
      (p.laps.length ? tr(`Snit <small>${p.laps.length} omg.</small>`, `Gennemsnit af ${range}: de seneste omgange uden pit, safety car og VSC${B ? ", som begge har kørt" : ""}`,
        fmtLap(p.avgA), fmtLap(p.avgB), faster(p.avgA, p.avgB, A.tla, B?.tla)) : "") +
      tr("Bedste", "Hurtigste omgang (ind- og udomgange tæller ikke)",
        p.bestA ? `${fmtLap(p.bestA.s)} <small class="sub">omg. ${p.bestA.lap}</small>` : "–",
        p.bestB ? `${fmtLap(p.bestB.s)} <small class="sub">omg. ${p.bestB.lap}</small>` : "–",
        faster(p.bestA?.s, p.bestB?.s, A.tla, B?.tla)) +
      raceRows(state, A, B, tr) +
      "</tbody>";
  }

  // Tabel: alle omgange, nyeste først
  if (table) {
    const rows = A ? lapRows(A.series, B ? B.series : []).reverse() : [];
    $("cmpTable").innerHTML = !rows.length ? "" :
      `<table class="cmp-laps"><thead><tr><th scope="col">Omg.</th><th scope="col">${escHtml(A.tla)}</th>${B ? `<th scope="col">${escHtml(B.tla)}</th><th scope="col">Hurtigst</th>` : ""}</tr></thead><tbody>` +
      rows.map((r) => `<tr><th scope="row">${r.lap}</th><td>${r.a?.s != null ? fmtLap(r.a.s) : "–"}${flagNote(r.a)}</td>` +
        (B ? `<td>${r.b?.s != null ? fmtLap(r.b.s) : "–"}${flagNote(r.b)}</td><td>${faster(r.a?.s, r.b?.s, A.tla, B.tla)}</td>` : "") + "</tr>").join("") +
      "</tbody></table>";
  }
}

// ---------- Kontroller ----------

document.querySelectorAll(".modes button").forEach((b) => b.addEventListener("click", () => setMode(b.dataset.mode)));
$("btnRecordings").addEventListener("click", openRecordings);
$("btnPlay").addEventListener("click", togglePlay);
document.querySelectorAll("[data-nudge]").forEach((b) => b.addEventListener("click", () => app.player.nudge(Number(b.dataset.nudge))));
document.querySelectorAll("[data-delay]").forEach((b) => b.addEventListener("click", () => changeDelay(Number(b.dataset.delay))));
$("speed").addEventListener("change", (e) => { app.player.speed = Number(e.target.value); });
$("radioAuto").checked = store.get("f1radioAuto", "0") === "1";
$("radioAuto").addEventListener("change", (e) => store.set("f1radioAuto", e.target.checked ? "1" : "0"));

let sliding = false;
$("slider").addEventListener("input", (e) => {
  sliding = true;
  const max = app.player.follow ? app.player.liveHeadMs : app.player.durationMs;
  const ms = (Number(e.target.value) / 1000) * max;
  if (app.player.follow) app.player.setDelay(app.player.liveHeadMs - ms);
  else app.player.seek(ms);
});
$("slider").addEventListener("change", () => { sliding = false; });

function togglePlay() {
  const p = app.player;
  if (!p.file) return;
  $("syncHint").hidden = true;
  if (p.playing) p.pause();
  else if (p.follow) p.resumeFollow();
  else p.play();
}

function changeDelay(deltaS) {
  const p = app.player;
  if (!p.follow) return;
  p.setDelay(p.delayMs + deltaS * 1000);
  store.set("f1delay", String(Math.round(p.delayMs / 1000)));
}

// Afspilning: spring til sessionens start og vent på at brugeren trykker ▶ ved start på TV.
$("btnSync").addEventListener("click", () => {
  const p = app.player;
  if (!p.file) return;
  if (p.startT === null) {
    showHint("Optagelsen har ingen start-markering (SessionStatus «Started»). Brug slideren.");
    return;
  }
  p.pause();
  p.seek(p.startT);
  const race = (p.state.SessionInfo?.Type || "") === "Race";
  showHint(race ? "Klar ved start. Tryk ▶ når lyset slukker på TV'et." : "Klar ved start. Tryk ▶ når pit exit bliver grøn på TV'et.");
});

// Forsinket live: tryk når sessionen starter på TV → forsinkelsen beregnes ud fra feedets start.
$("btnMatch").addEventListener("click", () => {
  const p = app.player;
  if (!p.follow || !p.file) return;
  if (p.startT === null) {
    showHint("Sessionen er ikke startet i feedet endnu. Tryk igen, når den starter på TV'et.");
    return;
  }
  const delay = p.liveHeadMs - p.startT;
  p.setDelay(delay);
  if (!p.playing) p.play();
  store.set("f1delay", String(Math.round(p.delayMs / 1000)));
  showHint(`Forsinkelse sat til ${Math.round(delay / 1000)} s.`);
});

function showHint(text) {
  const h = $("syncHint");
  h.textContent = text;
  h.hidden = false;
}

document.addEventListener("keydown", (e) => {
  if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement || $("recDialog").open) return;
  if (app.mode === "live") return;
  if (e.code === "Space") { e.preventDefault(); togglePlay(); }
  else if (e.code === "ArrowLeft") { e.preventDefault(); app.mode === "delay" ? changeDelay(e.shiftKey ? 10 : 1) : app.player.nudge(e.shiftKey ? -10 : -1); }
  else if (e.code === "ArrowRight") { e.preventDefault(); app.mode === "delay" ? changeDelay(e.shiftKey ? -10 : -1) : app.player.nudge(e.shiftKey ? 10 : 1); }
});

// ---------- Tegneløkke ----------

let lastFrame = 0;
let lastBigRender = 0;

function frame(now) {
  requestAnimationFrame(frame);
  const p = app.player;
  if (app.mode !== "live") p.tick(now);

  const state = app.mode === "live" ? app.liveState : p.state;
  const clock = app.mode === "live" ? Date.now() + app.clockOffsetMs : p.clockUtcMs();

  // Banekort: hver frame (interpolation); resten ~6 gange i sekundet
  const circuit = circuitOf(state.SessionInfo);
  app.map.setCircuit(circuit.key, circuit.year);
  // Hver frame: deepMerge opdaterer Position-objektet på stedet (samme reference),
  // så update() selv ser på den seneste stikprøves tidsstempel
  if (state.Position) app.map.update(state.Position, now);
  app.map.draw(state, now);

  if (now - lastBigRender < 160) return;
  lastBigRender = now;

  const modeLabel = app.mode === "delay" ? `forsinket ${Math.round(p.delayMs / 1000)} s` : app.mode === "replay" && p.file ? "afspilning" : "";
  renderHeader(state, clock, modeLabel);
  const pit = cmp.on ? null : pitInfo(state);
  renderTower(state, pit, cmp.on ? cmp.picks : null);
  app.map.ghost = pit?.ghost || null;
  app.map.marked = cmp.on ? cmp.picks : null;
  renderCompare(state);
  renderRaceControl(state);
  updateFilterLabel(renderRadioFilter(state, radio.hidden, toggleDriver));
  const caps = renderRadio(state, mediaStem(), playRadio, {
    texts: radio.texts, textsVersion: radio.textsVersion,
    waiting: !!app.pub?.transcribe?.enabled && app.mode !== "replay",
    hidden: radio.hidden, clockMs: clock,
  });
  autoRadio(state, caps);
  pollTexts(mediaStem(), caps, clock);

  if (!state.SessionInfo && app.mode === "live" && app.pub?.next?.length) {
    const n = app.pub.next[0];
    const when = new Date(n.startUtc).toLocaleString("da-DK", { weekday: "long", day: "numeric", month: "long", hour: "2-digit", minute: "2-digit" });
    $("sessionName").textContent = `Næste: ${n.meeting}, ${n.name}, ${when}`;
  }

  if (app.mode !== "live") {
    const max = p.follow ? p.liveHeadMs : p.durationMs;
    if (!sliding && max > 0) $("slider").value = String(Math.round((p.positionMs / max) * 1000));
    $("posLabel").textContent = p.follow
      ? `${fmtClock(p.positionMs)} (live ${fmtClock(max)})`
      : `${fmtClock(p.positionMs)} / ${fmtClock(max)}`;
    $("delayOut").textContent = `${Math.round(p.delayMs / 1000)} s`;
    $("btnPlay").textContent = p.playing ? "❚❚" : "▶";
    $("btnPlay").setAttribute("aria-label", p.playing ? "Pause" : "Afspil");
  }

  $("status").textContent = statusLine();
  void lastFrame;
}

function statusLine() {
  const pub = app.pub;
  if (app.mode === "live") {
    const feed = pub?.feed;
    const parts = [`Live: ${app.liveStatus}`];
    if (feed) parts.push(`F1: ${feed.status}${feed.usingToken && !feed.status.includes("F1 TV") ? " (F1 TV)" : ""}`);
    if (pub?.recording) parts.push(`optager ${pub.recording.session}`);
    return parts.join(". ");
  }
  const p = app.player;
  if (!p.file) return app.mode === "delay" ? "Forsinket live venter på en aktiv optagelse." : "Vælg en optagelse.";
  return `${app.mode === "delay" ? "Forsinket live" : "Afspilning"}: ${p.header?.session || p.file}${p.playing ? "" : " (pause)"}, ${p.speed}×`;
}

// ---------- Start ----------

await refreshPublic();
setInterval(refreshPublic, 15000);
const startMode = ["live", "delay", "replay"].includes(params.get("mode")) ? params.get("mode") : "live";
await setMode(startMode, { file: params.get("file") || undefined, t: params.get("t") || undefined });
setCmpView(cmp.view);
setCompare(cmp.on);
requestAnimationFrame(frame);
