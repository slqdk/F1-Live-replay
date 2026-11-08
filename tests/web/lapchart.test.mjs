// node --test tests/web/*.test.mjs — sammenligning af omgangstider (syntetiske data)
import { test } from "node:test";
import assert from "node:assert/strict";
import { lapSeries, isGreen, lapRows, paceStats, yDomain, flagBands, fmtLap, fmtSec, gapSeries, gapTrend, gapDomain,
  stintRanges, lineColour, seriesStyles } from "../../src/SlqF1Live.Server/wwwroot/js/lapchart.js";

const close = (a, b, msg, eps = 1e-9) => assert.ok(Math.abs(a - b) < eps, `${msg}: ${a} != ${b}`);
// [omgang, t_ms, tid_ms, flag, gab_ms]
const raw = (laps) => laps.map(([lap, ms, flags = "", gap = null]) => [lap, lap * 80000, ms, flags, gap]);

test("lapSeries: sekunder, gab og kun omgange op til afspilningen", () => {
  const s = lapSeries(raw([[2, 80500, "", 3000], [1, 90000], [3, null, "P"]]), 2.5 * 80000);
  assert.deepEqual(s.map((l) => [l.lap, l.s, l.flags, l.g]), [[1, 90, "", null], [2, 80.5, "", 3]]);
  assert.deepEqual(lapSeries(undefined), []);
  assert.equal(isGreen(s[0]), false, "1. omgang");
  assert.equal(isGreen(s[1]), true);
  assert.equal(isGreen({ lap: 5, s: 80, flags: "S" }), false);
});

test("paceStats: snit af de sidste fælles grønne omgange og bedste tid", () => {
  const a = lapSeries(raw([[1, 90000], [2, 80000], [3, 80200], [4, 79000, "S"], [5, 80400], [6, 99000, "P"], [7, 80100]]));
  const b = lapSeries(raw([[1, 91000], [2, 80300], [3, 80600], [4, 79100, "S"], [5, 80700], [7, 80000]]));
  const p = paceStats(a, b, 3);
  assert.deepEqual(p.laps, [3, 5, 7], "4 (SC) og 6 (kun A) tæller ikke");
  close(p.avgA, (80.2 + 80.4 + 80.1) / 3, "snit A");
  close(p.avgB, (80.6 + 80.7 + 80.0) / 3, "snit B");
  assert.equal(p.bestA.lap, 4, "SC-omgang må godt være bedst; pit tæller ikke");
  const single = paceStats(a, null, 2);
  assert.deepEqual(single.laps, [5, 7]);
  assert.equal(single.avgB, null);
});

test("yDomain: grønne omgange bestemmer skalaen, pit/SC må gå over kanten", () => {
  const a = lapSeries(raw([[2, 80000], [3, 81000], [4, 101000, "P"], [5, 115000, "S"]]));
  const [lo, hi] = yDomain(a);
  assert.ok(lo < 80 && hi > 81 && hi < 82, `${lo}–${hi}`);
  const [lo2, hi2] = yDomain(lapSeries(raw([[1, 90000]])));
  close(hi2 - lo2, 1, "mindst 1 s");
  assert.equal(yDomain([]), null);
});

test("flagBands: sammenhængende SC/VSC/rødt flag", () => {
  const a = lapSeries(raw([[3, 1, "S"], [4, 1, "S"], [5, 1, ""], [6, 1, "V"], [8, 1, "PR"]]));
  const b = lapSeries(raw([[5, 1, "V"]]));
  assert.deepEqual(flagBands(a, b), [{ from: 3, to: 4, kind: "S" }, { from: 5, to: 6, kind: "V" }, { from: 8, to: 8, kind: "R" }]);
});

test("fmtLap og fmtSec", () => {
  assert.equal(fmtLap(75.432), "1:15.432");
  assert.equal(fmtLap(65.05), "1:05.050");
  assert.equal(fmtLap(59.1234), "59.123");
  assert.equal(fmtLap(76, 0), "1:16");
  assert.equal(fmtLap(75.5, 1), "1:15.5");
  assert.equal(fmtLap(null), "–");
  assert.equal(fmtSec(-0.1544), "0,154 s");
});

test("gapSeries: gab til føreren trækkes fra; uden gab bruges tidspunkterne (ca.)", () => {
  const a = lapSeries(raw([[10, 80000, "", 0], [11, 80000, "", 0], [12, 80000, "P", 0]]));
  const b = lapSeries([[10, 10 * 80000, 81000, "", 5000], [11, 11 * 80000 + 5400, 80000, "", null], [13, 1, 1, "", 1]]);
  const g = gapSeries(a, b);
  assert.deepEqual(g.map((x) => [x.lap, x.approx, x.flags]), [[10, false, ""], [11, true, ""], [12, false, "P"]].slice(0, 2));
  close(g[0].gap, 5, "B 5 s bagud");
  close(g[1].gap, 5.4, "fra tidspunkterne");
});

test("gapTrend: hældning over de sidste grønne omgange", () => {
  const mk = (vals, flags = {}) => vals.map((v, i) => ({ lap: i + 1, gap: v, approx: false, flags: flags[i + 1] || "" }));
  close(gapTrend(mk([10, 9.6, 9.2, 8.8, 8.4]), 5).slope, -0.4, "B henter 0,4 s/omg.", 1e-9);
  close(gapTrend(mk([10, 9.6, 30, 8.8, 8.4], { 3: "P" }), 5).slope, -0.4, "pit-omgang springes over", 1e-9);
  assert.equal(gapTrend(mk([1, 0.5, -0.2, -0.6])), null, "skifter fører: ingen tendens");
  assert.equal(gapTrend(mk([1, 2])), null, "for få omgange");
});

test("gapDomain: altid med 0", () => {
  const [lo, hi] = gapDomain([{ gap: 5 }, { gap: 8 }]);
  assert.ok(lo < 0 && hi > 8);
  assert.equal(gapDomain([]), null);
});

test("stintRanges: omgange pr. stint i løbet, brugte dæk", () => {
  const st = stintRanges({ 0: { Compound: "INTERMEDIATE", TotalLaps: 16, StartLaps: 0 }, 1: { Compound: "SOFT", TotalLaps: 11, StartLaps: 3 } });
  assert.deepEqual(st.map((s) => [s.from, s.to, s.letter, s.age0]), [[1, 16, "I", 0], [17, 24, "S", 3]]);
  assert.deepEqual(stintRanges([{ Compound: "HARD", TotalLaps: 0, StartLaps: 0 }]).map((s) => [s.from, s.to]), [[1, 1]]);
  assert.deepEqual(stintRanges(null), []);
});

test("farver: mørke holdfarver lysnes; holdkammerater skilles ad", () => {
  assert.equal(lineColour("#ff8000"), "#ff8000");
  const dark = lineColour("#1e41ff");
  assert.notEqual(dark, "#1e41ff");
  assert.equal(lineColour("bogus"), "#8a9bb2");
  const mates = seriesStyles("#ff8000", "#ff8000");
  assert.notEqual(mates.a.colour, mates.b.colour, "samme hold: B lysere");
  assert.equal(mates.b.dash, "6 4");
  assert.equal(seriesStyles("#ff8000", "#27f4d2").b.colour, "#27f4d2");
});
