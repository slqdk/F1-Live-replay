// DevChart — linjegraf over sæsonen (SVG, ingen biblioteker).
// Y = gab til pole/vinderen, 0 øverst (højere er bedre). X = Grand Prix.
// Linjer 2 px, punkter r 4,5 med 2 px ring i fladens farve, hårfine gitterlinjer,
// endelabels i en rende til højre (med føringslinje), krydsmarkør + tooltip
// der viser alle synlige serier ved det valgte GP. Tekst bærer aldrig
// seriefarven — identitet kommer fra farvestreg + punktform ved siden af.

const NS = "http://www.w3.org/2000/svg";

/** Holdenes punktformer — valgt så de kan skelnes ved ~9 px. Nedadvendt
 *  trekant er forbeholdt punkter uden for den zoomede skala (OUTLIER). */
export const SHAPES = ["circle", "square", "diamond", "triangle", "cross", "x"];
export const OUTLIER = "triangle-down";

const crossPts = (L, w) => [[-w, -L], [w, -L], [w, -w], [L, -w], [L, w], [w, w], [w, L], [-w, L], [-w, w], [-L, w], [-L, -w], [-w, -w]];
function polygon(x, y, pts) {
  return pts.map(([px, py], i) => `${i ? "L" : "M"}${(x + px).toFixed(2)},${(y + py).toFixed(2)}`).join("") + "z";
}

export function shapePath(shape, x, y, r) {
  switch (shape) {
    case "square": { const a = r * 0.9; return `M${x - a},${y - a}h${2 * a}v${2 * a}h${-2 * a}z`; }
    case "diamond": { const a = r * 1.25; return `M${x},${y - a}L${x + a},${y}L${x},${y + a}L${x - a},${y}z`; }
    case "triangle": { const a = r * 1.25; return `M${x},${y - a}L${x + a},${y + a * 0.8}L${x - a},${y + a * 0.8}z`; }
    case "triangle-down": { const a = r * 1.25; return `M${x},${y + a}L${x + a},${y - a * 0.8}L${x - a},${y - a * 0.8}z`; }
    case "cross": return polygon(x, y, crossPts(r * 1.25, r * 0.5));
    case "x": { const c = Math.SQRT1_2; return polygon(x, y, crossPts(r * 1.35, r * 0.5).map(([a, b]) => [(a - b) * c, (a + b) * c])); }
    case "hexagon": {
      const a = r * 1.1; let d = "";
      for (let i = 0; i < 6; i++) { const t = Math.PI / 3 * i; d += `${i ? "L" : "M"}${x + a * Math.cos(t)},${y + a * Math.sin(t)}`; }
      return d + "z";
    }
    default: return `M${x - r},${y}a${r},${r} 0 1,0 ${2 * r},0a${r},${r} 0 1,0 ${-2 * r},0`;
  }
}

// ---------- Farveafstand (OKLab, normal + protan/deutan-simulering) ----------

function lin(c) { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }
function rgbLin(hex) {
  const h = hex.replace("#", "");
  return [lin(parseInt(h.slice(0, 2), 16)), lin(parseInt(h.slice(2, 4), 16)), lin(parseInt(h.slice(4, 6), 16))];
}
function oklab([r, g, b]) {
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  return [0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
          1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
          0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s];
}
const CVD = { // Machado 2009, fuld styrke (lineær RGB)
  protan: [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]],
  deutan: [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.01182, 0.04294, 0.968881]],
};
function sim(rgb, m) { return m.map((row) => Math.max(0, Math.min(1, row[0] * rgb[0] + row[1] * rgb[1] + row[2] * rgb[2]))); }
function dE(a, b) { return 100 * Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]); }

/** Farvelighed mellem to hold: mindste ΔE under normalt syn, protan og deutan.
 *  CVD-afstande vægtes ×1,6, så tærsklen 25 svarer til ca. 16 under farveblindhed. */
export function colourDistance(h1, h2) {
  try {
    const a = rgbLin(h1), b = rgbLin(h2);
    return Math.min(dE(oklab(a), oklab(b)), dE(oklab(sim(a, CVD.protan)), oklab(sim(b, CVD.protan))) * 1.6,
      dE(oklab(sim(a, CVD.deutan)), oklab(sim(b, CVD.deutan))) * 1.6);
  } catch { return 100; }
}

/** Hold med farveafstand under denne tærskel bør have hver sin punktform. */
export const SIMILAR = 25;

/** Fast punktform pr. hold for hele sæsonen (følger holdet, ikke filteret).
 *  Der er kun 6 former, og i 2026 er 7 hold parvis farvelignende under
 *  farveblindhed — så målet er: de hold, der må dele form, skal være så
 *  forskellige i farve som muligt. Vi finder den største tærskel T, hvor alle
 *  par med afstand < T kan få hver sin form (graffarvning med backtracking),
 *  og vælger derefter former, der spreder holdene mest muligt. Deterministisk:
 *  samme hold og farver giver altid samme former, uanset rækkefølge. */
export function assignShapes(teams) {
  const list = [...new Map(teams.map((t) => [t.name, t])).values()]
    .sort((a, b) => a.name.localeCompare(b.name, "da"));
  const out = new Map();
  if (!list.length) return out;
  const d = list.map((a) => list.map((b) => colourDistance(a.colour, b.colour)));
  const cands = [...new Set([SIMILAR, ...d.flatMap((row, i) => row.filter((v, j) => j > i && v < SIMILAR))])]
    .sort((a, b) => b - a);
  // Binær søgning: cands er faldende, og jo lavere T, jo færre konflikter
  let lo = 0, hi = cands.length - 1, best = colourGraph(d, cands[hi], SHAPES.length);
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    const col = colourGraph(d, cands[mid], SHAPES.length);
    if (col) { best = col; hi = mid; } else lo = mid + 1;
  }
  if (!best) best = list.map((_, i) => i % SHAPES.length); // kan ikke ske (laveste T har ingen konflikter)
  list.forEach((t, i) => out.set(t.name, SHAPES[best[i]]));
  return out;
}

/** Farv grafen "afstand < T" med k former (DSatur + backtracking).
 *  Returnerer formindeks pr. hold eller null. Nye former foretrækkes, ellers
 *  den form hvis nuværende indehavere er længst væk i farve. */
function colourGraph(d, T, k) {
  const n = d.length;
  const adj = d.map((row, i) => row.map((v, j) => i !== j && v < T));
  const col = new Array(n).fill(-1);
  let budget = 200000;
  const rec = (done, used) => {
    if (done === n) return true;
    if (--budget < 0) return false;
    let pick = -1, bestSat = -1, bestDeg = -1;
    for (let i = 0; i < n; i++) {
      if (col[i] >= 0) continue;
      const sat = new Set();
      let deg = 0;
      for (let j = 0; j < n; j++) if (adj[i][j]) { deg++; if (col[j] >= 0) sat.add(col[j]); }
      if (sat.size > bestSat || (sat.size === bestSat && deg > bestDeg)) { pick = i; bestSat = sat.size; bestDeg = deg; }
    }
    const options = [];
    for (let c = 0; c < Math.min(k, used + 1); c++) {
      let ok = true, near = Infinity;
      for (let j = 0; j < n && ok; j++) {
        if (col[j] !== c) continue;
        if (adj[pick][j]) ok = false;
        else near = Math.min(near, d[pick][j]);
      }
      if (ok) options.push({ c, near });
    }
    options.sort((a, b) => b.near - a.near || a.c - b.c);
    for (const { c } of options) {
      col[pick] = c;
      if (rec(done + 1, Math.max(used, c + 1))) return true;
      col[pick] = -1;
    }
    return false;
  };
  return rec(0, 0) ? col : null;
}

// ---------- Formatering ----------

export function fmtGap(s, isQuali) {
  if (s === 0) return isQuali ? "Pole" : "Vinder";
  return `+${s.toFixed(3)} s`;
}
export function fmtM(m) {
  if (m === null || m === undefined || !Number.isFinite(m)) return "";
  return m >= 1000 ? `${(m / 1000).toFixed(2)} km` : `${m.toFixed(1)} m`;
}

export function niceStep(raw) {
  const steps = [0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 15, 20, 30, 60, 120, 300];
  return steps.find((s) => s >= raw) || 600;
}

/** Decimaler på y-aksen efter trinnet: 0,25 → 2, 0,5 → 1, 2,5 → 1, 5 → 0. */
export function tickDecimals(step) {
  const whole = (v) => Math.abs(v - Math.round(v)) < 1e-9;
  return whole(step) ? 0 : whole(step * 10) ? 1 : 2;
}

function el(name, attrs = {}, parent) {
  const n = document.createElementNS(NS, name);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, String(v));
  if (parent) parent.appendChild(n);
  return n;
}

// ---------- Graf ----------

export class DevChart {
  constructor(container, tooltip) {
    this.box = container;
    this.tip = tooltip;
    this.data = null;
    this.index = null;
    this._onResize = () => this.data && this.render(this.data);
    window.addEventListener("resize", () => { clearTimeout(this._rt); this._rt = setTimeout(this._onResize, 120); });
    container.addEventListener("keydown", (e) => this._key(e));
    container.addEventListener("focus", () => { if (this.data?.gps.length) this._select(this.index ?? this.data.gps.length - 1); });
    container.addEventListener("blur", () => this._hideTip());
  }

  /** data: { gps:[{label,name,date}], series:[{label, sub, colour, dashed, shape, values:[{s,m,note}|null]}], isQuali, zoom } */
  render(data) {
    this.data = data;
    const box = this.box;
    box.textContent = "";
    const W = Math.max(300, box.clientWidth || 800);
    const H = Math.round(Math.max(320, Math.min(620, window.innerHeight * 0.58)));
    const n = data.gps.length;
    const labelled = data.series.length > 0 && data.series.length <= 8;
    const narrow = W < 560;
    const left = 62, right = labelled ? (narrow ? 48 : 74) : 16;
    // Hvor mange GP-navne der er plads til, og hvor dybt de skrå navne når
    const every = Math.max(1, Math.ceil(26 / Math.max(1, (W - left - right) / Math.max(1, n - 1))));
    const shown = data.gps.filter((_, i) => i % every === 0 || i === n - 1);
    const longest = Math.max(4, ...shown.map((g) => g.label.length));
    const M = { top: 14, left, right, bottom: Math.round(22 + Math.min(120, longest * 6.6 * 0.62)) };
    const pw = W - M.left - M.right, ph = H - M.top - M.bottom;
    const svg = el("svg", { viewBox: `0 0 ${W} ${H}`, width: W, height: H, role: "img" }, box);
    this.svg = svg;
    this.geom = { W, H, M, pw, ph, n };

    const vals = [];
    for (const s of data.series) for (const v of s.values) if (v && Number.isFinite(v.s)) vals.push(v.s);
    svg.setAttribute("aria-label", data.series.length
      ? `${data.series.length} serier over ${n} Grand Prix. Brug tabelvisningen for alle værdier.`
      : "Ingen serier valgt.");

    if (!n || !vals.length) {
      const t = el("text", { x: W / 2, y: H / 2, "text-anchor": "middle", class: "empty-msg" }, svg);
      t.textContent = !n ? "Ingen afviklede Grand Prix med data i denne sæson endnu." : "Vælg et eller flere hold ovenfor.";
      return;
    }

    // Y-skala (0 øverst)
    const sorted = [...vals].sort((a, b) => a - b);
    let top = sorted[sorted.length - 1];
    if (data.zoom && sorted.length > 4) {
      const p90 = sorted[Math.floor(sorted.length * 0.9)];
      top = Math.min(top, Math.max(p90 * 1.25, sorted[0] + 0.5));
    }
    const step = niceStep(Math.max(top, 0.05) / 5);
    const yMax = Math.max(step, Math.ceil(top / step - 1e-9) * step);
    const y = (v) => M.top + (Math.min(v, yMax) / yMax) * ph;
    const x = (i) => M.left + (n === 1 ? pw / 2 : (i * pw) / (n - 1));
    this.scale = { x, y, yMax };

    // Gitter + akser
    const dec = tickDecimals(step);
    for (let v = 0; v <= yMax + 1e-9; v += step) {
      const yy = Math.round(y(v)) + 0.5;
      el("line", { x1: M.left, x2: M.left + pw, y1: yy, y2: yy, class: "grid" }, svg);
      const t = el("text", { x: M.left - 10, y: yy + 4, "text-anchor": "end", class: "axis-label" }, svg);
      t.textContent = `+${v.toFixed(dec)} s`;
    }
    for (let i = 0; i < n; i++) {
      const xx = Math.round(x(i)) + 0.5;
      el("line", { x1: xx, x2: xx, y1: M.top, y2: M.top + ph, class: "grid" }, svg);
      if (i % every !== 0 && i !== n - 1) continue;
      const t = el("text", {
        x: xx, y: M.top + ph + 14, "text-anchor": "end", class: "x-label",
        transform: `rotate(-38 ${xx} ${M.top + ph + 14})`,
      }, svg);
      t.textContent = data.gps[i].label;
    }

    // Serier
    const ends = [];
    for (const s of data.series) {
      const g = el("g", {}, svg);
      let d = "";
      let pen = false;
      s.values.forEach((v, i) => {
        if (v && Number.isFinite(v.s)) { d += `${pen ? "L" : "M"}${x(i)},${y(v.s)}`; pen = true; }
        else pen = false;
      });
      if (d) el("path", { d, class: "series", stroke: s.colour, "stroke-dasharray": s.dashed ? "7 5" : "none" }, g);
      let last = null;
      s.values.forEach((v, i) => {
        if (!v || !Number.isFinite(v.s)) return;
        const out = v.s > yMax;
        el("path", { d: shapePath(out ? OUTLIER : s.shape, x(i), y(v.s), out ? 5 : 4.5), fill: s.colour, class: "dot" }, g);
        last = { i, yy: y(v.s) };
      });
      if (last && labelled) ends.push({ s, ...last });
    }

    // Endelabels i renden til højre — skubbes fra hinanden, føringslinje til punktet
    if (labelled) {
      ends.sort((a, b) => a.yy - b.yy);
      const gap = 15;
      ends.forEach((e, k) => { e.ly = k === 0 ? e.yy : Math.max(e.yy, ends[k - 1].ly + gap); });
      const overflow = ends.length ? ends[ends.length - 1].ly - (M.top + ph) : 0;
      if (overflow > 0) for (const e of ends) e.ly = Math.max(M.top, e.ly - overflow);
      const lx = M.left + pw + 10;
      for (const e of ends) {
        el("line", { x1: x(e.i) + 6, y1: e.yy, x2: lx - 3, y2: e.ly, class: "leader" }, svg);
        const t = el("text", { x: lx, y: e.ly + 4, class: "end-label" }, svg);
        t.textContent = e.s.short || e.s.label;
      }
    }

    // Krydsmarkør + hit-flade
    this.cross = el("line", { x1: 0, x2: 0, y1: M.top, y2: M.top + ph, class: "crosshair", visibility: "hidden" }, svg);
    const hit = el("rect", { x: M.left - 12, y: M.top, width: pw + 24, height: ph, class: "hit" }, svg);
    hit.addEventListener("pointermove", (e) => {
      const r = svg.getBoundingClientRect();
      const px = ((e.clientX - r.left) / r.width) * W;
      const i = n === 1 ? 0 : Math.round(((px - M.left) / pw) * (n - 1));
      this._select(Math.max(0, Math.min(n - 1, i)));
    });
    hit.addEventListener("pointerleave", () => { if (document.activeElement !== this.box) this._hideTip(); });
    if (this.index !== null && this.index < n && document.activeElement === this.box) this._select(this.index);
  }

  _key(e) {
    if (!this.data?.gps.length) return;
    const n = this.data.gps.length;
    if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
      e.preventDefault();
      const cur = this.index ?? n - 1;
      this._select(Math.max(0, Math.min(n - 1, cur + (e.key === "ArrowLeft" ? -1 : 1))));
    } else if (e.key === "Escape") this._hideTip();
  }

  _select(i) {
    this.index = i;
    const { x } = this.scale || {};
    if (!x || !this.cross) return;
    const xx = Math.round(x(i)) + 0.5;
    this.cross.setAttribute("x1", xx);
    this.cross.setAttribute("x2", xx);
    this.cross.setAttribute("visibility", "visible");
    this._showTip(i, xx);
  }

  _hideTip() {
    this.tip.hidden = true;
    this.cross?.setAttribute("visibility", "hidden");
  }

  _showTip(i, xx) {
    const d = this.data;
    const gp = d.gps[i];
    const rows = d.series
      .map((s) => ({ s, v: s.values[i] }))
      .filter((r) => r.v && (Number.isFinite(r.v.s) || r.v.note))
      .sort((a, b) => (Number.isFinite(a.v.s) ? a.v.s : 1e9) - (Number.isFinite(b.v.s) ? b.v.s : 1e9));
    const tip = this.tip;
    tip.textContent = "";
    const h = document.createElement("h3");
    h.textContent = gp.name + " ";
    const small = document.createElement("small");
    small.textContent = gp.date ? new Date(gp.date).toLocaleDateString("da-DK", { day: "numeric", month: "short" }) : "";
    h.appendChild(small);
    tip.appendChild(h);
    const ol = document.createElement("ol");
    for (const { s, v } of rows) {
      const li = document.createElement("li");
      if (!Number.isFinite(v.s)) li.className = "note";
      li.appendChild(lineKey(s, 26));
      const val = document.createElement("span");
      val.className = "v";
      val.textContent = Number.isFinite(v.s) ? fmtGap(v.s, d.isQuali) : v.note;
      const name = document.createElement("span");
      name.className = "n";
      const m = Number.isFinite(v.s) && v.s > 0 ? fmtM(v.m) : "";
      name.textContent = `${s.label}${s.sub ? `, ${s.sub}` : ""}${m ? ` (${m})` : ""}`;
      li.append(val, name);
      ol.appendChild(li);
    }
    if (!rows.length) {
      const li = document.createElement("li");
      li.textContent = "Ingen data for de valgte hold.";
      ol.appendChild(li);
    }
    tip.appendChild(ol);
    tip.hidden = false;
    const { W } = this.geom;
    const scale = this.box.clientWidth / W;
    const px = xx * scale;
    const tw = tip.offsetWidth;
    const left = px + 14 + tw > this.box.clientWidth ? px - 14 - tw : px + 14;
    tip.style.left = `${Math.max(0, left)}px`;
    tip.style.top = `${this.geom.M.top * scale}px`;
  }
}

/** Lille nøgle: farvestreg (stiplet for makker) + holdets punktform. */
export function lineKey(s, w = 26) {
  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("width", w);
  svg.setAttribute("height", 14);
  svg.setAttribute("aria-hidden", "true");
  el("line", { x1: 1, x2: w - 1, y1: 7, y2: 7, stroke: s.colour, "stroke-width": 2, "stroke-dasharray": s.dashed ? "5 3" : "none", "stroke-linecap": "round" }, svg);
  el("path", { d: shapePath(s.shape, w / 2, 7, 4), fill: s.colour, stroke: "#152030", "stroke-width": 1.5 }, svg);
  return svg;
}
