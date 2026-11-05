// Analysesiden: sæsonudvikling i kvalifikation og løb, valgfri hold.
// Data: /api/dev (serveren regner med Core/DevStats — samme tal som den gamle
// side og Windows-app'en). URL: ?year=2026&mode=quali|race&per=driver|team&view=chart|table&zoom=1&teams=A,B
// To grafer med samme valg: gab pr. Grand Prix, og ændringen i gabet siden første Grand Prix.

import { DevChart, assignShapes, deltaSeries, fmtDelta, fmtM, lineKey } from "./devchart.js";

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const nowYear = new Date().getFullYear();

const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* privat vindue */ } },
};

const st = {
  year: Number(params.get("year")) || nowYear,
  mode: params.get("mode") === "race" ? "race" : "quali",
  per: params.get("per") === "team" ? "team" : "driver",
  view: params.get("view") === "table" ? "table" : "chart",
  zoom: params.has("zoom") ? params.get("zoom") === "1" : store.get("f1dev:zoom") === "1",
  urlTeams: params.get("teams") ? params.get("teams").split(",").map((s) => s.trim()).filter(Boolean) : null,
  data: null,
  cache: new Map(),
  selected: null,
  selYear: null,
  shapes: new Map(),
  token: 0,
};

const chart = new DevChart($("chart"), $("tooltip"));
const deltaChart = new DevChart($("deltaChart"), $("deltaTip"));

// ---------- Data ----------

async function load() {
  const key = `${st.year}:${st.mode}`;
  const my = ++st.token;
  $("chart").classList.add("loading");
  $("tableWrap").classList.add("loading");
  if (!st.cache.has(key)) $("status").textContent = "Henter sæsondata fra OpenF1…";
  try {
    let data = st.cache.get(key);
    if (!data) {
      data = await fetchSeason(st.year, st.mode, my);
      if (!data) return; // brugeren har valgt noget andet imens
      st.cache.set(key, data);
    }
    if (my !== st.token) return;
    st.data = data;
    initSelection();
    $("status").textContent = "";
    render();
  } catch (e) {
    if (my !== st.token) return;
    $("status").textContent = `Kunne ikke hente data: ${e.message}`;
  } finally {
    if (my === st.token) {
      $("chart").classList.remove("loading");
      $("tableWrap").classList.remove("loading");
    }
  }
}

/** Hent en sæson. Serveren svarer 202 med fremdrift, mens en kold sæson
 *  hentes fra OpenF1 (~1 min første gang) — så spørger vi igen. */
async function fetchSeason(year, mode, my) {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  for (let retries = 0; ;) {
    let res, data;
    try {
      res = await fetch(`api/dev?year=${year}&mode=${mode}`, { cache: "no-store" });
      data = await res.json().catch(() => ({}));
    } catch (e) {
      if (++retries > 3) throw e;
      await sleep(2000);
      continue;
    }
    if (my !== st.token) return null;
    if (res.status === 202 || ((res.status === 502 || res.status === 504) && !data.error && ++retries <= 3)) {
      const m = /\((\d+)\/(\d+)\)/.exec(data.progress || "");
      $("status").textContent = m
        ? `Henter sæsondata fra OpenF1: Grand Prix ${m[1]} af ${m[2]}… (første gang tager det op til et par minutter)`
        : "Henter sæsondata fra OpenF1… (første gang tager det op til et par minutter)";
      await sleep(1500);
      if (my !== st.token) return null;
      continue;
    }
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
    return data;
  }
}

function selKey() { return `f1dev:sel:${st.year}`; }

/** Valgte hold: URL → gemt valg for sæsonen → serverens standard (bedste + dårligste).
 *  Skift mellem kvalifikation og løb beholder valget (samme sæson). */
function initSelection() {
  const names = st.data.teams.map((t) => t.name);
  st.shapes = assignShapes(st.data.teams);
  if (st.selected && st.selYear === st.year) {
    st.selected = new Set([...st.selected].filter((n) => names.includes(n)));
    return;
  }
  st.selYear = st.year;
  let sel = null;
  if (st.urlTeams) { sel = st.urlTeams.filter((n) => names.includes(n)); st.urlTeams = null; }
  if (!sel) {
    try { const saved = JSON.parse(store.get(selKey()) || "null"); if (Array.isArray(saved)) sel = saved.filter((n) => names.includes(n)); } catch { /* ignorér */ }
  }
  if (!sel || (!sel.length && store.get(selKey()) === null)) {
    const hidden = new Set(st.data.defaultHidden);
    sel = names.filter((n) => !hidden.has(n));
  }
  st.selected = new Set(sel);
}

function saveSelection() {
  store.set(selKey(), JSON.stringify([...st.selected]));
  syncUrl();
}

// ---------- Serier ----------

function avg(values) {
  const v = values.filter((x) => x && Number.isFinite(x.s)).map((x) => x.s);
  return v.length ? v.reduce((a, b) => a + b, 0) / v.length : Infinity;
}

function teamColour(name) {
  return st.data.teams.find((t) => t.name === name)?.colour || "#8a9bb2";
}

/** Kørerserier pr. hold; holdets bedste kører over sæsonen = fuld linje, makker(e) = stiplet. */
function driverSeries(teamFilter) {
  const byTeam = new Map();
  for (const d of st.data.drivers) {
    if (teamFilter && !teamFilter.has(d.team)) continue;
    if (!byTeam.has(d.team)) byTeam.set(d.team, []);
    byTeam.get(d.team).push(d);
  }
  const out = [];
  for (const [team, ds] of byTeam) {
    ds.sort((a, b) => avg(a.points) - avg(b.points));
    ds.forEach((d, k) => out.push({
      key: d.dn, label: d.tla, short: d.tla, sub: team, team,
      colour: teamColour(team), dashed: k > 0, shape: st.shapes.get(team) || "circle",
      values: d.points, avg: avg(d.points),
    }));
  }
  return out.sort((a, b) => a.avg - b.avg);
}

/** Holdserier: pr. GP holdets bedste bil (mindste gab). */
function teamSeries(teamFilter) {
  const n = st.data.gps.length;
  const out = [];
  for (const t of st.data.teams) {
    if (teamFilter && !teamFilter.has(t.name)) continue;
    const ds = st.data.drivers.filter((d) => d.team === t.name);
    const values = [];
    for (let i = 0; i < n; i++) {
      let best = null, note = null;
      for (const d of ds) {
        const p = d.points[i];
        if (!p) continue;
        if (Number.isFinite(p.s) && (!best || p.s < best.s)) best = p;
        else if (!Number.isFinite(p.s) && p.note && !note) note = p.note;
      }
      values.push(best || (note ? { s: null, m: null, note } : null));
    }
    out.push({
      key: t.name, label: t.name, short: shortTeam(t.name), sub: "", team: t.name,
      colour: t.colour, dashed: false, shape: st.shapes.get(t.name) || "circle",
      values, avg: avg(values),
    });
  }
  return out.sort((a, b) => a.avg - b.avg);
}

function shortTeam(name) {
  const map = { "Red Bull Racing": "Red Bull", "Haas F1 Team": "Haas", "Aston Martin": "Aston", "Racing Bulls": "RB" };
  return map[name] || name;
}

function seriesFor(filter) {
  return st.per === "team" ? teamSeries(filter) : driverSeries(filter);
}

// ---------- Rendering ----------

function render() {
  const d = st.data;
  if (!d) return;
  const isQuali = st.mode === "quali";
  $("chartTitle").textContent = isQuali ? "Tid efter pole pr. Grand Prix" : "Tid efter vinderen pr. Grand Prix";
  $("deltaTitle").textContent = `${isQuali ? "Gab til pole" : "Gab til vinderen"}: ændring siden første Grand Prix`;
  const ref = isQuali ? "pole" : "vinderen";
  $("deltaHelp").textContent =
    `0 = samme gab som i første Grand Prix. Opad er bedre: −0,20 s betyder 0,20 s tættere på ${ref} end dengang. ` +
    `${isQuali ? "Havde et hold eller en kører pole" : "Vandt et hold eller en kører"} i første Grand Prix, kan linjen ikke komme over 0. ` +
    "Kørte en kører ikke første Grand Prix, regnes fra vedkommendes første.";
  $("subtitle").textContent =
    `${isQuali ? "Kvalifikation" : "Løb"} ${d.year}: ${d.gps.length} Grand Prix. ` +
    `${st.per === "team" ? "Én linje pr. hold (bedste bil pr. GP)." : "Én linje pr. kører."} Data fra OpenF1.`;

  renderChips();

  const series = seriesFor(st.selected);
  const deltas = deltaSeries(series);
  if (st.view === "chart") {
    chart.render({ gps: d.gps, series, isQuali, zoom: st.zoom, kind: "gap" });
    deltaChart.render({ gps: d.gps, series: deltas, isQuali, zoom: st.zoom, kind: "delta" });
  } else {
    renderTable(series, isQuali);
    renderDeltaTable(deltas);
  }

  const notes = [];
  if (d.skipped?.length) notes.push(`Uden resultatdata (sprunget over): ${d.skipped.join(", ")}.`);
  if (d.failed?.length) notes.push(`Kunne ikke hentes lige nu og prøves igen om lidt: ${d.failed.join(", ")}.`);
  $("skipped").textContent = notes.join(" ");
  $("skipped").hidden = notes.length === 0;
  syncUrl();
}

function renderChips() {
  const box = $("chips");
  box.textContent = "";
  const all = seriesFor(null);
  const teamAvg = new Map();
  if (st.per === "team") for (const s of all) teamAvg.set(s.team, s.avg);
  for (const t of st.data.teams) {
    const on = st.selected.has(t.name);
    const b = document.createElement("button");
    b.type = "button";
    b.className = "an-chip";
    b.setAttribute("aria-pressed", String(on));
    b.appendChild(lineKey({ colour: t.colour, dashed: false, shape: st.shapes.get(t.name) || "circle" }, 24));
    const name = document.createElement("span");
    name.textContent = t.name;
    const a = document.createElement("span");
    a.className = "avg";
    const v = st.per === "team" ? teamAvg.get(t.name) : t.avg;
    a.textContent = Number.isFinite(v) ? `+${v.toFixed(v >= 10 ? 1 : 2)} s` : "–";
    b.append(name, a);
    b.title = `${on ? "Skjul" : "Vis"} ${t.name}`;
    b.addEventListener("click", () => {
      if (st.selected.has(t.name)) st.selected.delete(t.name); else st.selected.add(t.name);
      saveSelection();
      render();
    });
    box.appendChild(b);
  }
}

function renderTable(series, isQuali) {
  const wrap = $("tableWrap");
  wrap.textContent = "";
  if (!series.length) {
    const p = document.createElement("p");
    p.className = "an-foot";
    p.textContent = "Vælg et eller flere hold ovenfor.";
    wrap.appendChild(p);
    return;
  }
  const t = document.createElement("table");
  t.className = "an-table";
  const head = t.createTHead().insertRow();
  const th0 = document.createElement("th");
  th0.textContent = st.per === "team" ? "Hold" : "Kører";
  head.appendChild(th0);
  for (const g of st.data.gps) {
    const th = document.createElement("th");
    th.textContent = g.label;
    th.title = g.name;
    head.appendChild(th);
  }
  const thA = document.createElement("th");
  thA.textContent = "Snit";
  head.appendChild(thA);
  const body = t.createTBody();
  for (const s of series) {
    const tr = body.insertRow();
    const c0 = tr.insertCell();
    const key = document.createElement("span");
    key.className = "key";
    const label = document.createElement("span");
    label.textContent = s.sub ? `${s.label}, ${s.sub}` : s.label;
    key.append(lineKey(s, 24), label);
    c0.appendChild(key);
    for (const v of s.values) {
      const c = tr.insertCell();
      if (v && Number.isFinite(v.s)) {
        c.textContent = v.s === 0 ? (isQuali ? "Pole" : "Vinder") : `+${v.s.toFixed(3)}`;
        const m = fmtM(v.m);
        if (m && v.s > 0) c.title = m;
      } else {
        c.textContent = v?.note || "–";
        c.className = "note";
      }
    }
    const ca = tr.insertCell();
    ca.className = "avg";
    ca.textContent = Number.isFinite(s.avg) ? `+${s.avg.toFixed(3)}` : "–";
  }
  wrap.appendChild(t);
}

/** Ændring siden første GP: én række pr. serie, "Seneste" = nyeste værdi. */
function renderDeltaTable(series) {
  const wrap = $("deltaTableWrap");
  wrap.textContent = "";
  if (!series.length) return;
  const t = document.createElement("table");
  t.className = "an-table";
  const head = t.createTHead().insertRow();
  const th0 = document.createElement("th");
  th0.textContent = st.per === "team" ? "Hold" : "Kører";
  head.appendChild(th0);
  for (const g of st.data.gps) {
    const th = document.createElement("th");
    th.textContent = g.label;
    th.title = g.name;
    head.appendChild(th);
  }
  const thL = document.createElement("th");
  thL.textContent = "Seneste";
  head.appendChild(thL);
  const body = t.createTBody();
  for (const s of series) {
    const tr = body.insertRow();
    const c0 = tr.insertCell();
    const key = document.createElement("span");
    key.className = "key";
    const label = document.createElement("span");
    label.textContent = s.sub ? `${s.label}, ${s.sub}` : s.label;
    key.append(lineKey(s, 24), label);
    c0.appendChild(key);
    for (const v of s.values) {
      const c = tr.insertCell();
      if (v && Number.isFinite(v.s)) {
        c.textContent = v.base ? "basis" : fmtDelta(v.s).replace(" s", "");
        if (v.base) c.className = "base";
      } else {
        c.textContent = v?.note || "–";
        c.className = "note";
      }
    }
    const cl = tr.insertCell();
    cl.className = "avg";
    cl.textContent = Number.isFinite(s.latest) ? fmtDelta(s.latest).replace(" s", "") : "–";
  }
  wrap.appendChild(t);
}

function syncUrl() {
  const p = new URLSearchParams();
  if (st.year !== nowYear) p.set("year", st.year);
  if (st.mode !== "quali") p.set("mode", st.mode);
  if (st.per !== "driver") p.set("per", st.per);
  if (st.view !== "chart") p.set("view", st.view);
  if (st.zoom) p.set("zoom", "1");
  if (st.selected) p.set("teams", [...st.selected].join(","));
  history.replaceState(null, "", `?${p.toString()}`);
}

// ---------- Kontroller ----------

function pressed(selector, attr, value) {
  document.querySelectorAll(selector).forEach((b) => b.setAttribute("aria-pressed", String(b.dataset[attr] === value)));
}

const yearSel = $("year");
for (let y = nowYear; y >= 2023; y--) yearSel.add(new Option(String(y), String(y)));
yearSel.value = String(st.year);
yearSel.addEventListener("change", () => { st.year = Number(yearSel.value); load(); });

document.querySelectorAll("[data-mode]").forEach((b) => b.addEventListener("click", () => {
  st.mode = b.dataset.mode; pressed("[data-mode]", "mode", st.mode); load();
}));
document.querySelectorAll("[data-per]").forEach((b) => b.addEventListener("click", () => {
  st.per = b.dataset.per; pressed("[data-per]", "per", st.per); render();
}));
document.querySelectorAll("[data-view]").forEach((b) => b.addEventListener("click", () => {
  st.view = b.dataset.view; pressed("[data-view]", "view", st.view);
  showView();
  render();
}));
$("zoom").checked = st.zoom;
$("zoom").addEventListener("change", (e) => { st.zoom = e.target.checked; store.set("f1dev:zoom", st.zoom ? "1" : "0"); render(); });

$("teamsAll").addEventListener("click", () => { st.selected = new Set(st.data.teams.map((t) => t.name)); saveSelection(); render(); });
$("teamsNone").addEventListener("click", () => { st.selected = new Set(); saveSelection(); render(); });
$("teamsDefault").addEventListener("click", () => {
  const hidden = new Set(st.data.defaultHidden);
  st.selected = new Set(st.data.teams.map((t) => t.name).filter((n) => !hidden.has(n)));
  saveSelection();
  render();
});

pressed("[data-mode]", "mode", st.mode);
pressed("[data-per]", "per", st.per);
pressed("[data-view]", "view", st.view);
showView();
load();

function showView() {
  const chartView = st.view === "chart";
  for (const id of ["chartWrap", "chartHelp", "deltaWrap"]) $(id).hidden = !chartView;
  for (const id of ["tableWrap", "deltaTableWrap"]) $(id).hidden = chartView;
}
