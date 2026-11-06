// Pitprognose: hvor kommer en kører ud, hvis han pitter nu? Kun i løb.
// Pittab pr. bane fra MultiViewer (via /api/circuit) for grønt flag, safety
// car og VSC. Tallet er det samlede tab inkl. et normalt stop på ~2,5 s —
// målt mod 65 rigtige stop i Interlagos 2023 og 2025: median 22,1 s mod
// MultiViewer's 21,8 s. Har holdet stoppet i dette løb, bruges holdets egen
// stoptid i stedet for de 2,5 s. Antager at ingen andre pitter samtidig.

export const DEFAULT_STOP = 2.5;
const FALLBACK = { normal: 22, sc: 14, vsc: 15 }; // når banen ikke har data

/** "+1.320" / "1.320" / "+1:02.345" → sekunder; ellers null ("1 L", "LAP 23", ""). */
export function secondsOf(v) {
  const s = String(v ?? "").trim();
  const m = /^\+?(?:(\d+):)?(\d+(?:\.\d+)?)$/.exec(s);
  if (!m) return null;
  return (m[1] ? Number(m[1]) * 60 : 0) + Number(m[2]);
}

const truthy = (v) => v === true || v === "true" || v === 1 || v === "1";

/** Feltet i løbsrækkefølge med samlet tid efter føreren. Overhalede biler
 *  ("1 L") får tiden ved at lægge intervallerne sammen ned gennem feltet. */
export function raceOrder(state) {
  const lines = state.TimingData?.Lines || {};
  const rows = Object.entries(lines)
    .filter(([n, l]) => /^\d+$/.test(n) && l)
    .map(([n, l]) => ({ num: n, line: l, pos: Number(l.Position) || Number(l.Line) || 99 }))
    .sort((a, b) => a.pos - b.pos);
  const out = [];
  let prev = null;
  for (const r of rows) {
    const l = r.line;
    const retired = truthy(l.Retired) || truthy(l.Stopped);
    let gap = r.pos === 1 || !prev ? 0 : secondsOf(l.GapToLeader);
    let approx = false;
    if (gap === null) {
      const iv = secondsOf(l.IntervalToPositionAhead?.Value);
      gap = prev.gap + (iv ?? 1);
      approx = iv === null || prev.approx;
    }
    const row = { num: r.num, pos: r.pos, gap, approx, retired, inPit: truthy(l.InPit) };
    if (!retired) prev = row;
    out.push(row);
  }
  return out;
}

/** Holdets typiske stoptid i dette løb (median af PitStopSeries), ellers null. */
export function teamStopTime(state, num) {
  const team = state.DriverList?.[num]?.TeamName;
  if (!team) return null;
  const mates = new Set(Object.entries(state.DriverList || {}).filter(([, d]) => d?.TeamName === team).map(([n]) => n));
  const times = [];
  const walk = (v) => {
    if (!v || typeof v !== "object") return;
    const rn = v.RacingNumber != null ? String(v.RacingNumber) : null;
    const t = Number(v.PitStopTime);
    if (rn && mates.has(rn) && Number.isFinite(t) && t > 1.5 && t < 15) times.push(t);
    for (const k of Object.keys(v)) walk(v[k]);
  };
  walk(state.PitStopSeries);
  if (!times.length) return null;
  times.sort((a, b) => a - b);
  const mid = times.length >> 1;
  return times.length % 2 ? times[mid] : (times[mid - 1] + times[mid]) / 2;
}

/** Pittab lige nu. kind: normal | sc | vsc | red. */
export function pitLossNow(state, circuit, num) {
  const st = String(state.TrackStatus?.Status ?? "1");
  const kind = st === "5" ? "red" : st === "4" ? "sc" : st === "6" || st === "7" ? "vsc" : "normal";
  const table = circuit?.pitLoss && Number.isFinite(circuit.pitLoss.normal) ? circuit.pitLoss : null;
  const base = kind === "red" ? 0 : (table || FALLBACK)[kind];
  const team = teamStopTime(state, num);
  const stop = team ?? DEFAULT_STOP;
  const seconds = kind === "red" ? 0 : base - DEFAULT_STOP + stop;
  return { seconds, kind, stop, teamStop: team !== null, estimated: !table };
}

/** Hvor lander num, hvis han pitter nu? null hvis det ikke giver mening. */
export function predictPit(order, num, lossSeconds) {
  const me = order.find((r) => r.num === num);
  if (!me || me.retired) return null;
  const after = me.gap + lossSeconds;
  const others = order.filter((r) => r.num !== num && !r.retired);
  let ahead = null, behind = null;
  for (const r of others) {
    if (r.gap < after) { if (!ahead || r.gap > ahead.gap) ahead = r; }
    else if (!behind || r.gap < behind.gap) behind = r;
  }
  return {
    num,
    from: me.pos,
    pos: 1 + others.filter((r) => r.gap < after).length,
    ahead: ahead && { num: ahead.num, by: after - ahead.gap },
    behind: behind && { num: behind.num, by: behind.gap - after },
    approx: me.approx || !!ahead?.approx || !!behind?.approx,
    inPit: me.inPit,
  };
}

const KIND_TEXT = { normal: "grønt flag", sc: "safety car", vsc: "VSC", red: "rødt flag" };

/** Tekst til tårnets pitrække: { main, detail }. tlaOf(num) → "VER". */
export function describePit(p, loss, tlaOf) {
  const f = (s) => s.toFixed(1).replace(".", ",");
  const me = tlaOf(p.num);
  if (p.inPit) return { main: `${me} er i pitten nu`, detail: "" };
  if (loss.kind === "red") return { main: `Rødt flag: ${me} kan skifte dæk uden at tabe placering`, detail: "" };
  const parts = [`${me} ved pit nu → P${p.pos}${p.approx ? " (ca.)" : ""}`];
  if (p.ahead) parts.push(`${f(p.ahead.by)} s bag ${tlaOf(p.ahead.num)}`);
  if (p.behind) parts.push(`${f(p.behind.by)} s foran ${tlaOf(p.behind.num)}`);
  const detail = `pittab ${f(loss.seconds)} s inkl. ${f(loss.stop)} s stop${loss.teamStop ? " (holdets snit)" : ""}, ` +
    `${KIND_TEXT[loss.kind]}${loss.estimated ? ", anslået" : ""}`;
  return { main: parts.join(" · "), detail };
}
