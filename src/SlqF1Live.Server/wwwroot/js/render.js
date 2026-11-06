// Visning: tårn, topbjælke, race control, teamradio, vejr og banekort.
// Alle funktioner tager (state, clockUtcMs) — samme kode til live og afspilning.

import { listOf } from "./merge.js";

const $ = (id) => document.getElementById(id);

const TRACK = {
  "1": { flag: "green", text: "Grønt flag" },
  "2": { flag: "yellow", text: "Gult flag" },
  "3": { flag: "yellow", text: "Gult flag" },
  "4": { flag: "sc", text: "Safety car" },
  "5": { flag: "red", text: "Rødt flag" },
  "6": { flag: "vsc", text: "Virtuel safety car" },
  "7": { flag: "vscend", text: "VSC slutter" },
};

const COMPOUND = { SOFT: "S", MEDIUM: "M", HARD: "H", INTERMEDIATE: "I", WET: "W" };

export function str(v) {
  if (v === null || v === undefined) return "";
  return typeof v === "object" ? "" : String(v);
}

function truthy(v) {
  return v === true || v === "true" || v === 1 || v === "1";
}

function parseHms(s) {
  const m = /^(\d+):(\d\d):(\d\d)/.exec(s || "");
  return m ? (Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3])) * 1000 : null;
}

export function fmtClock(ms) {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return "–:––:––";
  ms = Math.max(0, ms);
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}`;
}

function fmtLocalTime(iso, withSeconds = false) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleTimeString("da-DK", { hour: "2-digit", minute: "2-digit", second: withSeconds ? "2-digit" : undefined });
}

export function teamColour(state, num) {
  const c = state.DriverList?.[num]?.TeamColour;
  return c && /^[0-9a-f]{6}$/i.test(c) ? `#${c}` : "#5d6f87";
}

export function tla(state, num) {
  const d = state.DriverList?.[num];
  return d?.Tla || d?.LastName?.slice(0, 3)?.toUpperCase() || `#${num}`;
}

// ---------- Topbjælke ----------

let lastFlag = null;

export function renderHeader(state, clockUtcMs, modeLabel) {
  const si = state.SessionInfo;
  $("meeting").textContent = si?.Meeting?.Name || "SLQ F1 Live";
  const circuit = si?.Meeting?.Circuit?.ShortName;
  const parts = [si?.Name, circuit].filter(Boolean);
  $("sessionName").textContent = parts.length ? parts.join(", ") + (modeLabel ? `  (${modeLabel})` : "") : modeLabel || "Venter på data";

  // Sessionsur: ExtrapolatedClock tæller ned fra Utc når Extrapolating
  const ec = state.ExtrapolatedClock;
  let remaining = parseHms(ec?.Remaining);
  if (remaining !== null && truthy(ec?.Extrapolating) && ec?.Utc) {
    remaining -= clockUtcMs - Date.parse(ec.Utc);
  }
  $("clock").textContent = remaining === null ? "–:––:––" : fmtClock(remaining);

  // Omgange kun i løb/sprint — i træning og kvalifikation er LapCount en rest fra forrige løb
  const lc = state.SessionInfo?.Type === "Race" ? state.LapCount : null;
  $("lapcount").textContent = lc?.CurrentLap ? `Omgang ${lc.CurrentLap}${lc.TotalLaps ? ` / ${lc.TotalLaps}` : ""}` : "";

  // Banestatus + flagbånd
  const sessionStatus = state.SessionStatus?.Status || si?.SessionStatus;
  let flag = "none", text = si ? "Ikke startet" : "Ingen session";
  if (sessionStatus === "Finished" || sessionStatus === "Finalised" || sessionStatus === "Ends") {
    flag = "chequered"; text = sessionStatus === "Finished" ? "Målflag" : "Afsluttet";
  } else if (sessionStatus === "Aborted") {
    flag = "red"; text = "Afbrudt";
  } else if (sessionStatus === "Started" || state.TrackStatus) {
    const ts = TRACK[str(state.TrackStatus?.Status)];
    if (ts && (sessionStatus === "Started" || ts.flag !== "green")) { flag = ts.flag; text = ts.text; }
  }
  const pill = $("trackstatus");
  pill.dataset.flag = flag;
  pill.textContent = text;
  const band = $("flagband");
  if (band.dataset.flag !== flag) {
    band.dataset.flag = flag;
    if (lastFlag !== null) {
      band.classList.remove("pulse");
      void band.offsetWidth;
      band.classList.add("pulse");
    }
    lastFlag = flag;
  }

  // Vejr
  const w = state.WeatherData;
  const el = $("weather");
  if (w && (w.AirTemp || w.TrackTemp)) {
    el.hidden = false;
    const rain = truthy(w.Rainfall);
    const dir = Number(w.WindDirection) || 0;
    el.innerHTML =
      `<span>Luft <b>${num1(w.AirTemp)}°</b></span>` +
      `<span>Bane <b>${num1(w.TrackTemp)}°</b></span>` +
      `<span class="${rain ? "rain" : ""}">${rain ? "<b>Regn</b>" : "Tørt"}</span>` +
      `<span>Vind <b>${num1(w.WindSpeed)} m/s</b> <span class="wind-arrow" style="transform:rotate(${dir + 180}deg)" aria-label="fra ${dir}°">↑</span></span>`;
  } else el.hidden = true;
}

function num1(v) {
  const n = Number(v);
  return Number.isFinite(n) ? n.toLocaleString("da-DK", { maximumFractionDigits: 1 }) : "–";
}

// ---------- Timing-tårn ----------

const rows = new Map();
const lastPos = new Map();
const moveUntil = new Map();

function segClass(status) {
  switch (Number(status)) {
    case 2048: return "s-y";
    case 2049: return "s-g";
    case 2051: return "s-p";
    case 2064: return "s-pit";
    default: return "";
  }
}

function timeClass(obj) {
  if (!obj) return "";
  if (truthy(obj.OverallFastest)) return "t-ob";
  if (truthy(obj.PersonalFastest)) return "t-pb";
  return "";
}

function setText(el, text, cls) {
  if (el._t !== text) { el.textContent = text; el._t = text; }
  if (cls !== undefined && el._c !== cls) { el.className = cls; el._c = cls; }
}

function makeRow(num) {
  const row = document.createElement("div");
  row.className = "row";
  row.setAttribute("role", "row");
  row.innerHTML =
    `<span class="c-pos"></span>` +
    `<span class="c-drv"><i class="bar"></i><span class="tla"></span><span class="num"></span></span>` +
    `<span class="c-gap"></span><span class="c-int"></span><span class="c-last"></span><span class="c-best"></span>` +
    `<span class="c-sec"><span class="val"></span><span class="segs"></span></span>`.repeat(3) +
    `<span class="c-tyre"><span class="tyre"></span><span class="tyre-laps"></span></span>` +
    `<span class="c-pit"></span>`;
  const q = (s) => row.querySelector(s);
  const secs = row.querySelectorAll(".c-sec");
  row._el = {
    pos: q(".c-pos"), bar: q(".bar"), tla: q(".tla"), num: q(".num"),
    gap: q(".c-gap"), int: q(".c-int"), last: q(".c-last"), best: q(".c-best"),
    secs: [...secs].map((s) => ({ val: s.querySelector(".val"), segs: s.querySelector(".segs") })),
    tyre: q(".tyre"), tyreLaps: q(".tyre-laps"), pit: q(".c-pit"),
  };
  row.dataset.num = num;
  return row;
}

export function renderTower(state) {
  const tower = $("tower");
  const lines = state.TimingData?.Lines || {};
  const app = state.TimingAppData?.Lines || {};
  const type = state.SessionInfo?.Type || "";
  const isRace = type === "Race" || /sprint$/i.test(state.SessionInfo?.Name || "") && type !== "Qualifying";
  const part = Number(state.TimingData?.SessionPart) || 0;

  $("hGap").textContent = isRace ? "Gab" : "Til P1";
  $("hInt").textContent = isRace ? "Int." : "Til forreste";

  const nums = Object.keys(lines).filter((n) => /^\d+$/.test(n));
  $("towerEmpty").hidden = nums.length > 0;

  // Hurtigste omgang overall (lilla)
  let overallBest = Infinity;
  for (const n of nums) {
    const v = lapSeconds(bestOf(lines[n], part));
    if (v !== null && v < overallBest) overallBest = v;
  }

  const order = nums
    .map((n) => ({ n, line: lines[n] }))
    .sort((a, b) => (Number(a.line.Line) || Number(a.line.Position) || 99) - (Number(b.line.Line) || Number(b.line.Position) || 99));

  const now = performance.now();
  const seen = new Set();
  order.forEach(({ n, line }, i) => {
    seen.add(n);
    let row = rows.get(n);
    if (!row) { row = makeRow(n); rows.set(n, row); tower.appendChild(row); }
    const el = row._el;
    row.style.transform = `translateY(calc(var(--row-h) * ${i}))`;

    const pos = Number(line.Position) || i + 1;
    const prev = lastPos.get(n);
    if (prev !== undefined && prev !== pos) moveUntil.set(n, { until: now + 4000, cls: pos < prev ? "c-pos up" : "c-pos down" });
    lastPos.set(n, pos);
    const mv = moveUntil.get(n);
    setText(el.pos, String(pos), mv && mv.until > now ? mv.cls : "c-pos");

    el.bar.style.background = teamColour(state, n);
    setText(el.tla, tla(state, n));
    setText(el.num, n);

    const out = truthy(line.Retired) || truthy(line.Stopped);
    const knocked = truthy(line.KnockedOut);
    row.classList.toggle("out", out);
    row.classList.toggle("knocked", knocked && !out);

    let gap, interval, catching = false;
    if (isRace) {
      gap = str(line.GapToLeader);
      interval = str(line.IntervalToPositionAhead?.Value);
      catching = truthy(line.IntervalToPositionAhead?.Catching);
      if (pos === 1 && !gap) gap = "Fører";
    } else {
      const stats = listOf(line.Stats);
      const s = part > 0 ? stats[part - 1] : null;
      gap = str(s?.TimeDiffToFastest ?? line.TimeDiffToFastest);
      interval = str(s?.TimeDifftoPositionAhead ?? line.TimeDiffToPositionAhead);
    }
    setText(el.gap, gap);
    setText(el.int, interval, catching ? "c-int catching" : "c-int");

    const last = line.LastLapTime;
    setText(el.last, str(last?.Value), "c-last " + timeClass(last));
    const bestVal = str(bestOf(line, part));
    const bestSec = lapSeconds(bestVal);
    setText(el.best, bestVal, "c-best" + (bestSec !== null && bestSec === overallBest ? " t-ob" : ""));

    const sectors = listOf(line.Sectors);
    for (let k = 0; k < 3; k++) {
      const sec = sectors[k];
      const cell = el.secs[k];
      const val = str(sec?.Value);
      setText(cell.val, val, "val " + (val ? timeClass(sec) || "t-norm" : ""));
      const segs = listOf(sec?.Segments);
      const sig = segs.map((s) => s?.Status ?? 0).join(",");
      if (cell.segs._sig !== sig) {
        cell.segs._sig = sig;
        cell.segs.innerHTML = segs.map((s) => `<i class="${segClass(s?.Status)}"></i>`).join("");
      }
    }

    const stints = listOf(app[n]?.Stints);
    const st = stints[stints.length - 1];
    const comp = str(st?.Compound).toUpperCase();
    setText(el.tyre, COMPOUND[comp] || (comp ? "?" : ""), "tyre " + (COMPOUND[comp] ? comp : ""));
    el.tyre.title = comp ? `${comp.toLowerCase()}${truthy(st?.New) ? ", nye" : ", brugte"}` : "";
    setText(el.tyreLaps, st?.TotalLaps !== undefined ? String(st.TotalLaps) : "");

    let pit = "";
    if (out) pit = `<span class="badge dnf">UDE</span>`;
    else if (truthy(line.InPit)) pit = `<span class="badge">PIT</span>`;
    else if (truthy(line.PitOut)) pit = `<span class="badge out">OUT</span>`;
    else if (Number(line.NumberOfPitStops) > 0) pit = String(line.NumberOfPitStops);
    if (el.pit._h !== pit) { el.pit.innerHTML = pit; el.pit._h = pit; }
  });

  for (const [n, row] of rows) {
    if (!seen.has(n)) { row.remove(); rows.delete(n); lastPos.delete(n); }
  }
  tower.style.height = `calc(var(--row-h) * ${order.length})`;
}

function bestOf(line, part) {
  if (!line) return "";
  if (part > 0) {
    const b = listOf(line.BestLapTimes)[part - 1];
    if (b?.Value) return b.Value;
  }
  return line.BestLapTime?.Value || "";
}

function lapSeconds(v) {
  const s = str(v);
  if (!s) return null;
  const m = /^(?:(\d+):)?(\d+(?:\.\d+)?)$/.exec(s);
  if (!m) return null;
  return (m[1] ? Number(m[1]) * 60 : 0) + Number(m[2]);
}

// ---------- Race control ----------

let rcSig = "";

export function renderRaceControl(state) {
  const msgs = listOf(state.RaceControlMessages?.Messages);
  const sig = msgs.length + ":" + (msgs[msgs.length - 1]?.Utc || "");
  if (sig === rcSig) return;
  rcSig = sig;
  const list = $("rc");
  const items = msgs.slice(-60).reverse();
  list.innerHTML = items.length
    ? items.map((m, i) => {
        const flag = String(m.Flag || (m.Category === "SafetyCar" ? "SC" : "")).replace(/\s+/g, "_");
        return `<li class="f-${esc(flag)}${i === 0 ? " fresh" : ""}">` +
          `<span class="lap">${m.Lap ? "Omg. " + esc(m.Lap) : ""}</span>` +
          `<span class="when">${esc(fmtLocalTime(m.Utc))}</span>` +
          `<span class="msg">${esc(m.Message || "")}</span></li>`;
      }).join("")
    : `<li class="none">Ingen meddelelser endnu.</li>`;
}

// ---------- Teamradio ----------

/** Kørere til filteret: holdkammerater ved siden af hinanden. */
export function radioDrivers(state) {
  return Object.values(state.DriverList || {})
    .filter((d) => d && typeof d === "object" && d.RacingNumber)
    .map((d) => ({ num: String(d.RacingNumber), tla: tla(state, d.RacingNumber), team: d.TeamName || "", colour: teamColour(state, d.RacingNumber) }))
    .sort((a, b) => a.team.localeCompare(b.team, "da") || Number(a.num) - Number(b.num));
}

let filterSig = "";

/** Kørerchips i teamradio-filteret (klik = vis/skjul). */
export function renderRadioFilter(state, hidden, onToggle) {
  const drivers = radioDrivers(state);
  const sig = drivers.map((d) => d.tla + d.colour).join() + "|" + [...hidden].sort().join();
  if (sig === filterSig) return drivers;
  filterSig = sig;
  const box = $("rfChips");
  box.innerHTML = drivers.length
    ? drivers.map((d) => `<button type="button" class="rf-chip" data-tla="${esc(d.tla)}" aria-pressed="${!hidden.has(d.tla)}" title="${esc(d.team)}">` +
        `<i style="background:${d.colour}"></i>${esc(d.tla)}</button>`).join("")
    : `<p class="rf-none">Kørerlisten kommer, når sessionen starter.</p>`;
  box.querySelectorAll(".rf-chip").forEach((b) => b.addEventListener("click", () => onToggle(b.dataset.tla)));
  return drivers;
}

let radioSig = "";

/** opts: { texts: {Path: {text, speech, error}}, textsVersion, waiting (live/forsinket: vis at teksten
 *  er på vej), hidden: Set<TLA>, clockMs } */
export function renderRadio(state, mediaStem, onPlay, opts = {}) {
  const caps = listOf(state.TeamRadio?.Captures);
  const hidden = opts.hidden || new Set();
  const texts = opts.texts || {};
  const recentMs = 3 * 60 * 1000; // så længe venter vi synligt på teksten
  const clock = opts.clockMs || Date.now();
  const sig = `${caps.length}:${caps[caps.length - 1]?.Utc || ""}:${mediaStem || ""}:${opts.textsVersion || 0}:` +
    `${[...hidden].join()}:${opts.waiting ? Math.floor(clock / 15000) : 0}`;
  if (sig === radioSig) return caps;
  radioSig = sig;
  const list = $("radio");
  const shown = caps.filter((c) => !hidden.has(tla(state, c.RacingNumber)));
  const items = shown.slice(-40).reverse();
  if (!items.length) {
    list.innerHTML = `<li class="none">${caps.length ? "Ingen teamradio fra de valgte kørere endnu." : "Ingen teamradio endnu."}</li>`;
    return caps;
  }
  list.innerHTML = items.map((c) => {
    const src = mediaStem && c.Path ? `media/${encodeURIComponent(mediaStem)}/${c.Path.split("/").map(encodeURIComponent).join("/")}` : "";
    const t = c.Path ? texts[c.Path] : null;
    let text = "";
    if (t && !t.error) {
      text = t.text ? `<p class="said">${esc(t.text)}</p>`
        : `<p class="said quiet">${t.speech === false ? "Ingen tale i klippet." : "Utydeligt."}</p>`;
    } else if (opts.waiting && src && clock - Date.parse(c.Utc) < recentMs) {
      text = `<p class="said quiet">Skriver det ned…</p>`;
    }
    return `<li${text ? " class=\"has-text\"" : ""}><i class="bar" style="background:${teamColour(state, c.RacingNumber)}"></i>` +
      `<span class="when">${esc(fmtLocalTime(c.Utc))}</span>` +
      `<span class="who">${esc(tla(state, c.RacingNumber))}</span>` +
      `<button type="button" data-src="${esc(src)}" ${src ? "" : "disabled title='Lydfilen er ikke hentet'"}>Afspil</button>` +
      text + `</li>`;
  }).join("");
  list.querySelectorAll("button[data-src]").forEach((b) => b.addEventListener("click", () => onPlay(b.dataset.src, b)));
  return caps;
}

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// ---------- Banekort ----------

/** Banens omrids fra /api/circuit (MultiViewer-data): drejet som på TV
 *  (rotation + 90°, y nedad) og med svingnumre lidt uden for banen.
 *  Returnerer { rot(x,y) → [x,y], pts, corners, bbox }. */
export function prepareOutline(d) {
  const xs = d.x, ys = d.y;
  const cx = (Math.min(...xs) + Math.max(...xs)) / 2, cy = (Math.min(...ys) + Math.max(...ys)) / 2;
  const a = (((Number(d.rotation) || 0) + 90) * Math.PI) / 180;
  const c = Math.cos(a), s = Math.sin(a);
  const rot = (x, y) => {
    const dx = x - cx, dy = y - cy;
    return [dx * c - dy * s + cx, dy * c + dx * s + cy];
  };
  const pts = xs.map((x, i) => rot(x, ys[i]));
  const corners = (d.corners || []).map((k) => {
    const ka = ((Number(k.angle) || 0) * Math.PI) / 180;
    const [x, y] = rot(k.x + 540 * Math.cos(ka), k.y + 540 * Math.sin(ka));
    return { label: String(k.n), x, y };
  });
  const bbox = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity };
  for (const [x, y] of [...pts, ...corners.map((k) => [k.x, k.y])]) {
    bbox.minX = Math.min(bbox.minX, x); bbox.maxX = Math.max(bbox.maxX, x);
    bbox.minY = Math.min(bbox.minY, y); bbox.maxY = Math.max(bbox.maxY, y);
  }
  return { rot, pts, corners, bbox };
}

export class TrackMap {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.points = new Map();
    this.cars = new Map();
    this.key = null;
    this.outline = null;
    this.lastSample = null;
    this.dirty = true;
  }

  setCircuit(key, year) {
    if (key === this.key) return;
    this.key = key;
    this.points = new Map();
    this.cars = new Map();
    this.outline = null;
    this.lastSample = null;
    try {
      const raw = key ? localStorage.getItem(`f1map:${key}`) : null;
      if (raw) for (const [x, y] of JSON.parse(raw)) this.points.set(`${x},${y}`, [x, y]);
    } catch { /* ingen lagring */ }
    if (key) this.loadOutline(key, year);
    this.dirty = true;
  }

  /** Rigtigt omrids fra serveren; ellers bygges det af bilernes positioner. */
  async loadOutline(key, year) {
    try {
      const y = Number(year) || new Date().getUTCFullYear();
      const res = await fetch(`api/circuit?key=${encodeURIComponent(key)}&year=${y}`);
      if (!res.ok || this.key !== key) return;
      const d = await res.json();
      if (this.key !== key || !Array.isArray(d.x) || d.x.length < 10) return;
      this.outline = prepareOutline(d);
      this.dirty = true;
    } catch { /* positionerne må gøre det */ }
  }

  save() {
    if (!this.key || this.points.size < 50) return;
    try { localStorage.setItem(`f1map:${this.key}`, JSON.stringify([...this.points.values()].slice(0, 6000))); } catch { /* fuld */ }
  }

  addPoints(entries) {
    let added = 0;
    for (const e of Object.values(entries || {})) {
      if (!e || e.Status === "OffTrack" || typeof e.X !== "number") continue;
      if (e.X === 0 && e.Y === 0) continue;
      const gx = Math.round(e.X / 40) * 40, gy = Math.round(e.Y / 40) * 40;
      const k = `${gx},${gy}`;
      if (!this.points.has(k)) { this.points.set(k, [gx, gy]); added++; }
    }
    if (added) this.dirty = true;
    if (added && this.points.size % 200 < added) this.save();
  }

  /** Nyt Position-sample (seneste i listen). */
  update(position, nowMs) {
    const samples = position?.Position;
    if (!Array.isArray(samples) || !samples.length) return;
    const s = samples[samples.length - 1];
    if (!s?.Entries || s.Timestamp === this.lastSample) return;
    this.lastSample = s.Timestamp;
    this.addPoints(s.Entries);
    for (const [num, e] of Object.entries(s.Entries)) {
      if (typeof e?.X !== "number") continue;
      const car = this.cars.get(num);
      if (!car) this.cars.set(num, { fx: e.X, fy: e.Y, tx: e.X, ty: e.Y, t0: nowMs, t1: nowMs, status: e.Status });
      else {
        const p = this._interp(car, nowMs);
        car.fx = p.x; car.fy = p.y; car.tx = e.X; car.ty = e.Y;
        car.t0 = nowMs; car.t1 = nowMs + Math.min(1500, Math.max(200, nowMs - (car.lastAt || nowMs - 1000)));
        car.status = e.Status;
      }
      this.cars.get(num).lastAt = nowMs;
    }
  }

  _interp(car, now) {
    if (now >= car.t1 || car.t1 === car.t0) return { x: car.tx, y: car.ty };
    const k = (now - car.t0) / (car.t1 - car.t0);
    return { x: car.fx + (car.tx - car.fx) * k, y: car.fy + (car.ty - car.fy) * k };
  }

  draw(state, nowMs) {
    const c = this.canvas;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, h = c.clientHeight;
    if (!w || !h) return;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) {
      c.width = Math.round(w * dpr);
      c.height = Math.round(h * dpr);
    }
    const ctx = this.ctx;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    if (this.outline) return this._drawOutlined(ctx, state, nowMs, w, h);
    const pts = [...this.points.values()];
    $("mapEmpty").hidden = pts.length > 0 || this.cars.size > 0;
    if (!pts.length && !this.cars.size) return;

    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    const all = pts.length ? pts : [...this.cars.values()].map((cr) => [cr.tx, cr.ty]);
    for (const [x, y] of all) {
      if (x < minX) minX = x; if (x > maxX) maxX = x;
      if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    const pad = 18;
    const sx = (w - pad * 2) / Math.max(1, maxX - minX);
    const sy = (h - pad * 2) / Math.max(1, maxY - minY);
    const s = Math.min(sx, sy);
    const ox = (w - (maxX - minX) * s) / 2, oy = (h - (maxY - minY) * s) / 2;
    const X = (x) => ox + (x - minX) * s;
    const Y = (y) => h - (oy + (y - minY) * s); // F1's Y peger op

    ctx.fillStyle = "#2a3c56";
    for (const [x, y] of pts) ctx.fillRect(X(x) - 1.6, Y(y) - 1.6, 3.2, 3.2);

    this._drawCars(ctx, state, nowMs, (x, y) => [X(x), Y(y)]);
  }

  /** Biler: prik i holdfarve, forreste øverst. Navne kun hvor der er plads —
   *  i pitten holder flere biler samme sted, og navnene ville flyde sammen. */
  _drawCars(ctx, state, nowMs, project) {
    const lines = state.TimingData?.Lines || {};
    const nums = [...this.cars.keys()].sort((a, b) => (Number(lines[a]?.Line) || 99) - (Number(lines[b]?.Line) || 99));
    const placed = [];
    const drawn = [];
    ctx.font = `600 11px "Barlow Semi Condensed", system-ui, sans-serif`;
    ctx.textBaseline = "middle";
    for (const num of nums) {
      const car = this.cars.get(num);
      const p = this._interp(car, nowMs);
      const [x, y] = project(p.x, p.y);
      const label = tla(state, num);
      const box = { x0: x + 7, x1: x + 9 + ctx.measureText(label).width, y0: y - 7, y1: y + 7 };
      const free = !placed.some((b) => box.x0 < b.x1 && box.x1 > b.x0 && box.y0 < b.y1 && box.y1 > b.y0);
      if (free) placed.push(box);
      drawn.push({ num, x, y, label: free ? label : null, off: car.status === "OffTrack" });
    }
    for (const d of drawn.reverse()) { // bagerste først, så P1 ligger øverst
      ctx.globalAlpha = d.off ? 0.35 : 1;
      ctx.beginPath();
      ctx.arc(d.x, d.y, 6, 0, Math.PI * 2);
      ctx.fillStyle = teamColour(state, d.num);
      ctx.fill();
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = "#0f1722";
      ctx.stroke();
    }
    ctx.fillStyle = "#e9eef5";
    for (const d of drawn) {
      if (!d.label) continue;
      ctx.globalAlpha = d.off ? 0.35 : 1;
      ctx.fillText(d.label, d.x + 8, d.y);
    }
    ctx.globalAlpha = 1;
    this.dirty = false;
  }

  _drawOutlined(ctx, state, nowMs, w, h) {
    $("mapEmpty").hidden = true;
    const o = this.outline, b = o.bbox, pad = 16;
    const s = Math.min((w - pad * 2) / Math.max(1, b.maxX - b.minX), (h - pad * 2) / Math.max(1, b.maxY - b.minY));
    const ox = (w - (b.maxX - b.minX) * s) / 2, oy = (h - (b.maxY - b.minY) * s) / 2;
    const X = (x) => ox + (x - b.minX) * s;
    const Y = (y) => oy + (y - b.minY) * s;

    ctx.beginPath();
    o.pts.forEach(([x, y], i) => (i ? ctx.lineTo(X(x), Y(y)) : ctx.moveTo(X(x), Y(y))));
    ctx.closePath();
    ctx.lineJoin = "round";
    ctx.lineCap = "round";
    ctx.lineWidth = 7;
    ctx.strokeStyle = "#2a3c56";
    ctx.stroke();

    ctx.font = `500 10px "Barlow Semi Condensed", system-ui, sans-serif`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#6f819a";
    for (const k of o.corners) ctx.fillText(k.label, X(k.x), Y(k.y));
    ctx.textAlign = "start";

    this._drawCars(ctx, state, nowMs, (x, y) => {
      const [rx, ry] = o.rot(x, y);
      return [X(rx), Y(ry)];
    });
  }
}
