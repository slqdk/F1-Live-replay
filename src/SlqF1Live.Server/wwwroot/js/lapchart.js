// Sammenligning af to kørere: omgangstider og afstanden mellem dem. Data fra
// /api/laps: pr. omgang [nr, t_ms, tid_ms|null, flag, gab_ms|null] — flag:
// P pit (ind- og udomgang), S safety car, V VSC, R rødt flag; gab = gab til
// føreren lige efter stregen. t er optagelsens tid, så forsinket visning og
// afspilning kun viser omgange op til det sted, der afspilles.

import { shapePath, colourDistance, SIMILAR, tickDecimals } from "./devchart.js";

const NS = "http://www.w3.org/2000/svg";
const FLAG_TEXT = { P: "pit", S: "SC", V: "VSC", R: "rødt flag" };

/** Rå omgange → [{lap, t, s, flags, g}] sorteret, kun dem med t ≤ cutoffMs. */
export function lapSeries(raw, cutoffMs = Infinity) {
  if (!Array.isArray(raw)) return [];
  return raw
    .filter((r) => Array.isArray(r) && r[1] <= cutoffMs)
    .map(([lap, t, ms, flags, gap]) => ({ lap, t, s: ms == null ? null : ms / 1000, flags: flags || "", g: gap == null ? null : gap / 1000 }))
    .sort((a, b) => a.lap - b.lap);
}

/** Afstand pr. omgang: gap > 0 = A foran (B er gap s bagud). Fra gab til
 *  føreren (samme tidtagningspunkt for begge); mangler det (overhalet), fra
 *  tidspunkterne de krydsede stregen (approx). flags = begges flag. */
export function gapSeries(a, b) {
  return lapRows(a, b).filter((r) => r.a && r.b).map((r) => {
    const exact = r.a.g != null && r.b.g != null;
    return { lap: r.lap, gap: exact ? r.b.g - r.a.g : (r.b.t - r.a.t) / 1000, approx: !exact, flags: r.a.flags + r.b.flags };
  });
}

/** Ændring i afstand pr. omgang (mindste kvadraters hældning) over de sidste
 *  n omgange uden pit/SC/VSC/rødt flag. > 0: A trækker fra. null: for få. */
export function gapTrend(gaps, n = 5) {
  const g = gaps.filter((x) => !x.flags).slice(-n);
  if (g.length < 3 || g.some((x, i) => i && Math.sign(x.gap) !== Math.sign(g[0].gap))) return null;
  const mx = mean(g.map((x) => x.lap)), my = mean(g.map((x) => x.gap));
  const num = g.reduce((s, x) => s + (x.lap - mx) * (x.gap - my), 0);
  const den = g.reduce((s, x) => s + (x.lap - mx) ** 2, 0);
  return den ? { slope: num / den, laps: g.length } : null;
}

export const COMPOUND = {
  SOFT: ["S", "#ec3340"], MEDIUM: ["M", "#f5c518"], HARD: ["H", "#eef1f4"],
  INTERMEDIATE: ["I", "#3cb94e"], WET: ["W", "#3a86f0"],
};

/** Dækstints fra TimingAppData → [{from, to, compound, letter, colour, age0}]
 *  i løbets omgange. age0 = omgange på dækket før stinten (brugte dæk). */
export function stintRanges(stints) {
  if (!stints || typeof stints !== "object") return [];
  const list = Object.entries(stints).sort((a, b) => Number(a[0]) - Number(b[0])).map(([, v]) => v).filter(Boolean);
  const out = [];
  let from = 1;
  for (const st of list) {
    const total = Number(st.TotalLaps) || 0, start = Number(st.StartLaps) || 0;
    const laps = Math.max(0, total - start);
    const [letter, colour] = COMPOUND[String(st.Compound || "").toUpperCase()] || ["?", "#5d6f87"];
    out.push({ from, to: from + Math.max(laps, 1) - 1, laps, compound: st.Compound || "", letter, colour, age0: start });
    from += laps;
  }
  return out;
}

/** Grøn omgang: tid kendt, ingen pit/SC/VSC/rødt flag og ikke 1. omgang (stående start). */
export const isGreen = (l) => !!l && l.s != null && !l.flags && l.lap > 1;

/** Omgange flettet efter nummer: [{lap, a, b}] (a/b kan mangle). */
export function lapRows(a, b = []) {
  const m = new Map();
  for (const l of a) m.set(l.lap, { lap: l.lap, a: l, b: undefined });
  for (const l of b) { const r = m.get(l.lap); if (r) r.b = l; else m.set(l.lap, { lap: l.lap, a: undefined, b: l }); }
  return [...m.values()].sort((x, y) => x.lap - y.lap);
}

function mean(v) { return v.reduce((s, x) => s + x, 0) / v.length; }

/** Tempo: snit af de sidste n grønne omgange, som begge har kørt (eller den
 *  ene, hvis der kun er én kører), og bedste tid pr. kører. */
export function paceStats(a, b = null, n = 5) {
  const best = (s) => s.filter((l) => l.s != null && l.lap > 1 && !l.flags.includes("P"))
    .reduce((m, l) => (!m || l.s < m.s ? l : m), null);
  let common;
  if (b) common = lapRows(a, b).filter((r) => isGreen(r.a) && isGreen(r.b)).slice(-n);
  else common = a.filter(isGreen).slice(-n).map((l) => ({ lap: l.lap, a: l }));
  return {
    laps: common.map((r) => r.lap),
    avgA: common.length ? mean(common.map((r) => r.a.s)) : null,
    avgB: b && common.length ? mean(common.map((r) => r.b.s)) : null,
    bestA: best(a),
    bestB: b ? best(b) : null,
  };
}

/** Y-område (sekunder): alle grønne omgange skal kunne ses; pit- og SC-omgange
 *  må gå over kanten (vises som pil). Uden grønne omgange: alle tider. */
export function yDomain(...series) {
  const all = series.flat();
  let v = all.filter(isGreen).map((l) => l.s);
  if (v.length < 2) v = all.filter((l) => l.s != null).map((l) => l.s);
  if (!v.length) return null;
  let lo = Math.min(...v), hi = Math.max(...v);
  const pad = Math.max(0.15, (hi - lo) * 0.08);
  lo -= pad; hi += pad;
  if (hi - lo < 1) { const c = (hi + lo) / 2; lo = c - 0.5; hi = c + 0.5; }
  return [lo, hi];
}

/** Sammenhængende omgange med SC/VSC/rødt flag: [{from, to, kind}] (kind S|V|R). */
export function flagBands(...series) {
  const kindOf = new Map();
  const rank = { R: 3, S: 2, V: 1 };
  for (const l of series.flat()) {
    for (const k of ["R", "S", "V"]) {
      if (l.flags.includes(k) && (rank[k] > (rank[kindOf.get(l.lap)] || 0))) kindOf.set(l.lap, k);
    }
  }
  const laps = [...kindOf.keys()].sort((x, y) => x - y);
  const out = [];
  for (const lap of laps) {
    const k = kindOf.get(lap), last = out[out.length - 1];
    if (last && last.kind === k && last.to === lap - 1) last.to = lap;
    else out.push({ from: lap, to: lap, kind: k });
  }
  return out;
}

/** 75.432 → "1:15.432"; under et minut "59.123". */
export function fmtLap(s, dec = 3) {
  if (s == null || !Number.isFinite(s)) return "–";
  const m = Math.floor(s / 60), r = s - m * 60;
  const sec = r.toFixed(dec);
  return m ? `${m}:${sec.padStart(dec ? dec + 3 : 2, "0")}` : sec;
}

/** Forskel i tekst: 0.154 → "0,154 s". */
export const fmtSec = (d, dec = 3) => `${Math.abs(d).toFixed(dec).replace(".", ",")} s`;

/** Farve til en linje på mørk baggrund: for mørke holdfarver gøres lysere. */
export function lineColour(hex, lighten = 0) {
  const h = String(hex || "").replace("#", "");
  if (!/^[0-9a-f]{6}$/i.test(h)) return "#8a9bb2";
  let [r, g, b] = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
  const lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
  const mix = Math.max(lighten, lum < 0.42 ? (0.42 - lum) * 1.4 : 0);
  [r, g, b] = [r, g, b].map((c) => Math.round(c + (255 - c) * Math.min(0.6, mix)));
  return `#${[r, g, b].map((c) => c.toString(16).padStart(2, "0")).join("")}`;
}

/** Stil til de to linjer: B er altid stiplet med firkanter; ligner farverne
 *  hinanden (fx holdkammerater), gøres B's farve også lysere. */
export function seriesStyles(colourA, colourB) {
  const a = lineColour(colourA);
  const similar = colourB && colourDistance(`#${String(colourA).replace("#", "")}`, `#${String(colourB).replace("#", "")}`) < SIMILAR;
  const b = colourB ? lineColour(colourB, similar ? 0.45 : 0) : null;
  return { a: { colour: a, dash: "", shape: "circle" }, b: b && { colour: b, dash: "6 4", shape: "square" } };
}

function xStep(span, px) {
  for (const s of [1, 2, 5, 10, 20, 25, 50]) if (px / (span / s) >= 34) return s;
  return 100;
}
function yStep(span, px) {
  const want = span / Math.max(2, px / 30);
  for (const s of [0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 15, 30, 60]) if (s >= want) return s;
  return 120;
}
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

/** Grafen. kind "laps": x = omgang, y = omgangstid (langsommere opad).
 *  kind "gap": afstanden mellem dem (A foran opad), farvet efter hvem der
 *  fører. Dækstints som bånd under x-aksen. Krydshår + boks ved
 *  mus/berøring; piletaster flytter mellem omgange. */
export class LapChart {
  constructor(wrap, tip) {
    this.wrap = wrap;
    this.tip = tip;
    this.svg = document.createElementNS(NS, "svg");
    this.svg.setAttribute("class", "cmp-svg");
    this.svg.setAttribute("aria-hidden", "true");
    wrap.prepend(this.svg);
    this.model = null;
    this.geo = null;
    this.sel = null;
    const pick = (e) => this._pointer(e);
    wrap.addEventListener("pointermove", pick);
    wrap.addEventListener("pointerdown", pick);
    wrap.addEventListener("pointerleave", (e) => { if (e.pointerType === "mouse") this._select(null); });
    wrap.addEventListener("keydown", (e) => this._key(e));
    wrap.addEventListener("blur", () => this._select(null));
  }

  /** model: { kind, a: {tla, series, colour, dash, shape, stints}, b: … | null } */
  render(model) {
    this.model = model;
    const W = this.wrap.clientWidth, H = this.wrap.clientHeight;
    const series = [model.a, model.b].filter(Boolean);
    const gapMode = model.kind === "gap" && !!model.a && !!model.b;
    const gaps = gapMode ? gapSeries(model.a.series, model.b.series) : [];
    this.gaps = gaps;
    const pts = gapMode ? gaps.map((g) => ({ lap: g.lap, v: g.gap })) : series.flatMap((x) => x.series).filter((l) => l.s != null).map((l) => ({ lap: l.lap, v: l.s }));
    const dom = gapMode ? gapDomain(gaps) : yDomain(...series.map((x) => x.series));
    if (!pts.length || !dom || W < 120 || H < 80) { this.svg.innerHTML = ""; this.geo = null; this._select(null); return; }
    const strips = series.filter((x) => x.stints?.length);
    const m = { l: 46, r: 40, t: 12, b: 22 + (strips.length ? strips.length * 10 + 6 : 0) };
    const pw = W - m.l - m.r, ph = H - m.t - m.b;
    const laps = pts.map((p) => p.lap);
    const x0 = Math.min(...laps) - 0.5, x1 = Math.max(...laps) + 0.5;
    const [lo, hi] = dom;
    const X = (lap) => m.l + ((lap - x0) / (x1 - x0)) * pw;
    const Yraw = (v) => m.t + ((hi - v) / (hi - lo)) * ph;
    const Y = (v) => Math.max(m.t, Math.min(m.t + ph, Yraw(v)));
    this.geo = { m, pw, ph, x0, x1, X, Y, W, H, gapMode, lapsAll: [...new Set(laps)].sort((p, q) => p - q) };
    const pxLap = pw / (x1 - x0);
    let s = "";

    // SC/VSC/rødt flag som bånd bag alt andet
    for (const band of flagBands(...series.map((x) => x.series))) {
      if (band.to < x0 || band.from > x1) continue;
      const bx = X(Math.max(band.from - 0.5, x0)), bw = X(Math.min(band.to + 0.5, x1)) - bx;
      s += `<rect class="band band-${band.kind}" x="${bx.toFixed(1)}" y="${m.t}" width="${bw.toFixed(1)}" height="${ph}"/>`;
      const label = band.kind === "S" ? "SC" : band.kind === "V" ? "VSC" : "Rødt";
      if (bw >= label.length * 7 + 4) s += `<text class="band-label" x="${(bx + 3).toFixed(1)}" y="${m.t + 11}">${label}</text>`;
    }
    // Y-akse
    const ys = yStep(hi - lo, ph), dec = tickDecimals(ys);
    for (let v = Math.ceil(lo / ys) * ys; v <= hi + 1e-9; v += ys) {
      const y = Yraw(v).toFixed(1);
      const zero = gapMode && Math.abs(v) < 1e-9;
      s += `<line class="${zero ? "zero" : "grid"}" x1="${m.l}" x2="${m.l + pw}" y1="${y}" y2="${y}"/>`;
      const label = gapMode ? (zero ? "0" : `${v > 0 ? "+" : "−"}${Math.abs(v).toFixed(dec).replace(".", ",")}`) : fmtLap(v, dec);
      s += `<text class="tick" x="${m.l - 6}" y="${y}" text-anchor="end" dominant-baseline="middle">${label}</text>`;
    }
    // X-akse
    const xs = xStep(x1 - x0, pw);
    for (let lap = Math.ceil((x0 + 0.5) / xs) * xs; lap <= x1; lap += xs) {
      s += `<text class="tick" x="${X(lap).toFixed(1)}" y="${m.t + ph + 15}" text-anchor="middle">${lap}</text>`;
    }
    s += `<line class="axis" x1="${m.l}" x2="${m.l + pw}" y1="${m.t + ph}" y2="${m.t + ph}"/>`;

    if (gapMode) s += this._gapMarks(gaps, model, X, Y, Yraw, m, pw, ph);
    else s += this._lapMarks(series, X, Y, hi, pxLap, m, ph);

    // Dækstints under x-aksen (samme x som grafen)
    strips.forEach((ser, i) => {
      const y = m.t + ph + 22 + i * 10;
      s += `<text class="strip-label" x="${m.l - 6}" y="${y + 3.5}" text-anchor="end" dominant-baseline="middle">${esc(ser.tla)}</text>`;
      for (const st of ser.stints) {
        if (st.to < x0 + 0.5 || st.from > x1 - 0.5) continue;
        const sx = X(Math.max(st.from - 0.5, x0)) + 1, ex = X(Math.min(st.to + 0.5, x1)) - 1;
        if (ex - sx < 1) continue;
        s += `<rect class="stint" x="${sx.toFixed(1)}" y="${y}" width="${(ex - sx).toFixed(1)}" height="7" rx="2" fill="${st.colour}"/>`;
        if (ex - sx >= 16) s += `<text class="stint-label" x="${((sx + ex) / 2).toFixed(1)}" y="${y + 3.6}" text-anchor="middle" dominant-baseline="middle">${st.letter}</text>`;
      }
    });
    s += `<g class="hover"></g>`;
    this.svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
    this.svg.setAttribute("width", W);
    this.svg.setAttribute("height", H);
    this.svg.innerHTML = s;
    this._select(this.sel && this.geo.lapsAll.includes(this.sel) ? this.sel : null);
  }

  _lapMarks(series, X, Y, hi, pxLap, m, ph) {
    let s = "";
    const ends = [];
    for (const ser of series) {
      let d = "", prev = null;
      for (const l of ser.series) {
        if (l.s == null) { prev = null; continue; }
        d += `${prev && l.lap === prev.lap + 1 ? "L" : "M"}${X(l.lap).toFixed(1)},${Y(l.s).toFixed(1)}`;
        prev = l;
      }
      s += `<path class="lap-line" d="${d}" stroke="${ser.colour}"${ser.dash ? ` stroke-dasharray="${ser.dash}"` : ""}/>`;
      const r = pxLap >= 10 ? 3.6 : 2.6;
      for (const l of ser.series) {
        if (l.s == null) continue;
        const x = X(l.lap), y = Y(l.s);
        if (l.s > hi) s += `<path class="mark" d="${shapePath("arrow-up", x, y + 6, 4.2)}" fill="${ser.colour}"/>`;
        else if (l.flags.includes("P")) s += `<path class="mark pit" d="${shapePath(ser.shape, x, y, 3.6)}" stroke="${ser.colour}"/>`;
        else if (pxLap >= 7) s += `<path class="mark" d="${shapePath(ser.shape, x, y, r)}" fill="${ser.colour}"/>`;
      }
      const last = [...ser.series].reverse().find((l) => l.s != null);
      if (last) ends.push({ tla: ser.tla, x: X(last.lap), y: Y(last.s) });
    }
    // Navne for enden af linjerne (skubbes fra hinanden)
    if (ends.length === 2 && Math.abs(ends[0].y - ends[1].y) < 13) {
      const [u, v] = ends[0].y <= ends[1].y ? [ends[0], ends[1]] : [ends[1], ends[0]];
      const mid = (u.y + v.y) / 2;
      u.y = mid - 6.5; v.y = mid + 6.5;
    }
    for (const e of ends) {
      s += `<text class="end-label" x="${(e.x + 7).toFixed(1)}" y="${Math.max(m.t + 5, Math.min(m.t + ph - 2, e.y)).toFixed(1)}" dominant-baseline="middle">${esc(e.tla)}</text>`;
    }
    return s;
  }

  /** Afstand: flade over/under nul i førerens farve, linje i tekstfarve. */
  _gapMarks(gaps, model, X, Y, Yraw, m, pw, ph) {
    const y0 = Math.max(m.t, Math.min(m.t + ph, Yraw(0)));
    let s = `<defs><clipPath id="cmpClipA"><rect x="0" y="0" width="${m.l + pw + m.r}" height="${y0}"/></clipPath>` +
      `<clipPath id="cmpClipB"><rect x="0" y="${y0}" width="${m.l + pw + m.r}" height="${m.t + ph - y0}"/></clipPath></defs>`;
    // Sammenhængende stykker (brud ved manglende omgange)
    const runs = [];
    for (const g of gaps) {
      const last = runs[runs.length - 1];
      if (last && g.lap === last[last.length - 1].lap + 1) last.push(g); else runs.push([g]);
    }
    for (const run of runs) {
      const line = run.map((g, i) => `${i ? "L" : "M"}${X(g.lap).toFixed(1)},${Y(g.gap).toFixed(1)}`).join("");
      const area = `${line}L${X(run[run.length - 1].lap).toFixed(1)},${y0.toFixed(1)}L${X(run[0].lap).toFixed(1)},${y0.toFixed(1)}z`;
      s += `<path class="gap-area" d="${area}" fill="${model.a.colour}" clip-path="url(#cmpClipA)"/>`;
      s += `<path class="gap-area" d="${area}" fill="${model.b.colour}" clip-path="url(#cmpClipB)"/>`;
      s += `<path class="gap-line" d="${line}"/>`;
    }
    for (const g of gaps) {
      const x = X(g.lap), y = Y(g.gap);
      if (g.flags.includes("P")) s += `<circle class="gap-pit" cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="3.6"/>`;
    }
    // Hvem er foran: tekst i hjørnerne (kun hvor der er plads over/under nul)
    if (y0 - m.t >= 18) s += `<text class="side-label" x="${m.l + 6}" y="${m.t + 12}">${esc(model.a.tla)} foran</text>`;
    if (m.t + ph - y0 >= 18) s += `<text class="side-label" x="${m.l + 6}" y="${m.t + ph - 6}">${esc(model.b.tla)} foran</text>`;
    return s;
  }

  _pointer(e) {
    const g = this.geo;
    if (!g) return;
    const rect = this.wrap.getBoundingClientRect();
    const lap = g.x0 + ((e.clientX - rect.left - g.m.l) / g.pw) * (g.x1 - g.x0);
    let best = null;
    for (const l of g.lapsAll) if (best === null || Math.abs(l - lap) < Math.abs(best - lap)) best = l;
    this._select(best);
  }

  _key(e) {
    const g = this.geo;
    if (!g || !["ArrowLeft", "ArrowRight", "Home", "End", "Escape"].includes(e.key)) return;
    e.preventDefault();
    const L = g.lapsAll;
    if (e.key === "Escape") return this._select(null);
    let i = this.sel == null ? L.length - 1 : L.indexOf(this.sel);
    if (e.key === "ArrowLeft") i = Math.max(0, i - 1);
    if (e.key === "ArrowRight") i = Math.min(L.length - 1, this.sel == null ? i : i + 1);
    if (e.key === "Home") i = 0;
    if (e.key === "End") i = L.length - 1;
    this._select(L[i]);
  }

  _select(lap) {
    this.sel = lap;
    const hov = this.svg.querySelector(".hover");
    const g = this.geo, model = this.model;
    if (!hov || !g || lap == null || !model) {
      if (hov) hov.innerHTML = "";
      this.tip.hidden = true;
      return;
    }
    const x = g.X(lap);
    let h = `<line class="cross" x1="${x}" x2="${x}" y1="${g.m.t}" y2="${g.m.t + g.ph}"/>`;
    const rows = [];
    const at = {};
    for (const [k, ser] of [["a", model.a], ["b", model.b]]) {
      if (!ser) continue;
      const l = ser.series.find((q) => q.lap === lap);
      at[k] = l;
      if (!g.gapMode && l?.s != null) h += `<circle class="hl" cx="${x}" cy="${g.Y(l.s)}" r="5.5" stroke="${ser.colour}"/>`;
      const flags = l ? [...l.flags].map((f) => FLAG_TEXT[f]).filter(Boolean).join(", ") : "";
      const st = ser.stints?.find((q) => lap >= q.from && lap <= q.to);
      const tyre = st ? `${st.letter} ${lap - st.from + 1 + st.age0}` : "";
      rows.push(`<tr><th scope="row"><svg class="sw" width="22" height="8" aria-hidden="true"><line x1="1" x2="21" y1="4" y2="4" stroke="${ser.colour}"${ser.dash ? ' stroke-dasharray="5 3"' : ""}/></svg>${esc(ser.tla)}</th>` +
        `<td>${l?.s != null ? fmtLap(l.s) : "–"}</td><td class="fl">${tyre}</td><td class="fl">${flags}</td></tr>`);
    }
    let foot = "";
    if (g.gapMode) {
      const i = this.gaps.findIndex((q) => q.lap === lap);
      const cur = this.gaps[i];
      if (cur) {
        h += `<circle class="hl" cx="${x}" cy="${g.Y(cur.gap)}" r="5.5" stroke="#e9eef5"/>`;
        const [lead, trail] = cur.gap >= 0 ? [model.a.tla, model.b.tla] : [model.b.tla, model.a.tla];
        foot = `${esc(trail)} ${fmtSec(cur.gap, cur.approx ? 1 : 3)} bag ${esc(lead)}${cur.approx ? " (ca.)" : ""}`;
        const prev = this.gaps[i - 1];
        if (prev && prev.lap === lap - 1 && Math.sign(prev.gap) === Math.sign(cur.gap)) {
          const d = Math.abs(cur.gap) - Math.abs(prev.gap);
          if (Math.abs(d) >= 0.0005) foot += `<br>${esc(trail)} ${d < 0 ? "hentede" : "tabte"} ${fmtSec(d)}`;
        }
      }
    } else if (model.b && at.a?.s != null && at.b?.s != null) {
      const d = at.a.s - at.b.s;
      foot = Math.abs(d) < 0.0005 ? "Lige hurtige" : `${esc(d < 0 ? model.a.tla : model.b.tla)} ${fmtSec(d)} hurtigere`;
    }
    hov.innerHTML = h;
    this.tip.innerHTML = `<div class="tip-head">Omgang ${lap}</div><table>${rows.join("")}</table>${foot ? `<div class="tip-diff">${foot}</div>` : ""}`;
    this.tip.hidden = false;
    const tw = this.tip.offsetWidth, th = this.tip.offsetHeight;
    const left = x + 12 + tw > g.W ? x - 12 - tw : x + 12;
    this.tip.style.left = `${Math.max(0, left)}px`;
    this.tip.style.top = `${Math.max(0, Math.min(g.H - th, g.m.t + 4))}px`;
  }
}

/** Y-område for afstanden: altid med 0, mindst ±1 s. */
export function gapDomain(gaps) {
  if (!gaps.length) return null;
  const v = gaps.map((g) => g.gap);
  let lo = Math.min(0, ...v), hi = Math.max(0, ...v);
  const pad = Math.max(0.3, (hi - lo) * 0.1);
  lo -= pad; hi += pad;
  if (hi - lo < 2) { const c = (hi + lo) / 2; lo = c - 1; hi = c + 1; }
  return [lo, hi];
}
