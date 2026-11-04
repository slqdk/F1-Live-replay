// SLQ F1 Live — visning. Tre tilstande:
//   Live       SSE fra /events (samme kontrakt som live.html på f1.slq.dk)
//   Forsinket  den igangværende optagelse afspillet X sekunder bag live
//   Afspilning en optagelse afspillet i browseren (seek, hastighed, sync)
// URL: ?mode=live|delay|replay&file=<fil.f1rec>&t=<sek> — bogmærk fx en TV-visning.

import { deepMerge } from "./merge.js";
import { RecordingPlayer } from "./player.js";
import { renderHeader, renderTower, renderRaceControl, renderRadio, TrackMap, fmtClock } from "./render.js";

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
  lastPositionRef: null,
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
    app.lastPositionRef = null;
    app.seenRadio = null;
    buildOutline(app.player);
    updateUrl();
  } catch (e) {
    setLoadState(`Kunne ikke indlæse optagelsen (${e.message}).`);
  }
}

/** Banekortets omrids med det samme: stikprøver af optagelsens Position-linjer. */
function buildOutline(player) {
  const si = player.state.SessionInfo;
  app.map.setCircuit(si?.Meeting?.Circuit?.Key ?? si?.Meeting?.Key ?? null);
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
    if ($("radioAuto").checked && playing && last?.Path && mediaStem()) {
      const src = `media/${encodeURIComponent(mediaStem())}/${last.Path.split("/").map(encodeURIComponent).join("/")}`;
      playRadio(src, null);
    }
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
  const circuitKey = state.SessionInfo?.Meeting?.Circuit?.Key ?? state.SessionInfo?.Meeting?.Key ?? null;
  app.map.setCircuit(circuitKey);
  if (state.Position && state.Position !== app.lastPositionRef) {
    app.lastPositionRef = state.Position;
    app.map.update(state.Position, now);
  }
  app.map.draw(state, now);

  if (now - lastBigRender < 160) return;
  lastBigRender = now;

  const modeLabel = app.mode === "delay" ? `forsinket ${Math.round(p.delayMs / 1000)} s` : app.mode === "replay" && p.file ? "afspilning" : "";
  renderHeader(state, clock, modeLabel);
  renderTower(state);
  renderRaceControl(state);
  const caps = renderRadio(state, mediaStem(), audio, playRadio);
  autoRadio(state, caps);

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
    if (feed) parts.push(`F1: ${feed.status}${feed.usingToken ? " (F1 TV)" : ""}`);
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
requestAnimationFrame(frame);
