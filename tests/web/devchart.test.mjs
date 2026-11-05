// node --test tests/web/*.test.mjs — udviklingsgrafens rene funktioner (ingen DOM)
import { test } from "node:test";
import assert from "node:assert/strict";
import { assignShapes, colourDistance, deltaSeries, fmtDelta, fmtGap, fmtM, niceStep, tickDecimals, SHAPES, OUTLIER_SHAPES, SIMILAR }
  from "../../src/SlqF1Live.Server/wwwroot/js/devchart.js";

// Holdfarver som OpenF1 leverer dem for 2026
const TEAMS = [
  ["Mercedes", "#00D7B6"], ["Ferrari", "#ED1131"], ["McLaren", "#F47600"], ["Red Bull Racing", "#4781D7"],
  ["Racing Bulls", "#6C98FF"], ["Alpine", "#00A1E8"], ["Audi", "#F50537"], ["Haas F1 Team", "#9C9FA2"],
  ["Williams", "#1868DB"], ["Aston Martin", "#229971"], ["Cadillac", "#909090"],
].map(([name, colour]) => ({ name, colour }));

test("aksens decimaler følger trinnet", () => {
  assert.deepEqual([0.05, 0.1, 0.2, 0.25, 0.5, 1, 2.5, 5].map(tickDecimals), [2, 1, 1, 2, 1, 0, 1, 0]);
  assert.equal((0.75).toFixed(tickDecimals(0.25)), "0.75");
});

test("pænt trin dækker det rå trin", () => {
  assert.equal(niceStep(0.21), 0.25);
  assert.equal(niceStep(0.6), 1);
  assert.equal(niceStep(700), 600);
});

test("Ferrari og Audi er næsten samme farve — og får forskellig form", () => {
  const ferrari = TEAMS.find((t) => t.name === "Ferrari").colour;
  const audi = TEAMS.find((t) => t.name === "Audi").colour;
  assert.ok(colourDistance(ferrari, audi) < 25);
  const shapes = assignShapes(TEAMS);
  assert.notEqual(shapes.get("Ferrari"), shapes.get("Audi"));
});

test("farvelignende hold deler aldrig form (2026)", () => {
  // 7 hold er parvis farvelignende i 2026 — og der er netop 7 former
  const shapes = assignShapes(TEAMS);
  for (const a of TEAMS) for (const b of TEAMS) {
    if (a.name >= b.name || colourDistance(a.colour, b.colour) >= SIMILAR) continue;
    assert.notEqual(shapes.get(a.name), shapes.get(b.name), `${a.name} / ${b.name}`);
  }
  for (const s of shapes.values()) assert.ok(SHAPES.includes(s) && !OUTLIER_SHAPES.includes(s));
});

test("når nogen må dele form, er det det mest forskellige par", () => {
  // 8 næsten ens grå (parvis under tærsklen), 7 former: yderpunkterne deler
  const greys = [0x80, 0x83, 0x86, 0x89, 0x8c, 0x8f, 0x92, 0x95].map((v, i) => ({ name: `T${i}`, colour: "#" + v.toString(16).repeat(3) }));
  for (const a of greys) for (const b of greys) if (a !== b) assert.ok(colourDistance(a.colour, b.colour) < SIMILAR);
  const shapes = assignShapes(greys);
  const shared = greys.filter((g) => greys.some((h) => h !== g && shapes.get(h.name) === shapes.get(g.name))).map((g) => g.name);
  assert.deepEqual(shared, ["T0", "T7"]);
});

test("formen følger holdet, ikke rækkefølgen eller filteret", () => {
  const a = assignShapes(TEAMS);
  const b = assignShapes([...TEAMS].reverse());
  assert.deepEqual([...a.entries()].sort(), [...b.entries()].sort());
});

test("formatering af gab og meter", () => {
  assert.equal(fmtGap(0, true), "Pole");
  assert.equal(fmtGap(0, false), "Vinder");
  assert.equal(fmtGap(0.1234, true), "+0.123 s");
  assert.equal(fmtM(950), "950.0 m");
  assert.equal(fmtM(1500), "1.50 km");
  assert.equal(fmtM(null), "");
});

test("ændring siden første Grand Prix", () => {
  const p = (s, m = null) => ({ s, m });
  const [a, b, c] = deltaSeries([
    { label: "LEC", values: [p(0.3, 20), p(0.5, 30), null, p(0.1, 8), { s: null, m: null, note: "DNF" }] },
    { label: "BOT", values: [null, p(1.2), p(0.9), p(1.5)] },          // kørte ikke første GP
    { label: "XXX", values: [null, { s: null, m: null, note: "DNF" }] }, // ingen tid overhovedet
  ]);
  assert.equal(a.baseIndex, 0);
  assert.deepEqual(a.values.map((v) => v && (Number.isFinite(v.s) ? +v.s.toFixed(3) : v.note)), [0, 0.2, null, -0.2, "DNF"]);
  assert.ok(a.values[0].base && !a.values[1].base);
  assert.equal(a.values[3].gap, 0.1);
  assert.equal(+a.values[3].m.toFixed(1), -12);
  assert.equal(+a.latest.toFixed(3), -0.2);
  assert.equal(b.baseIndex, 1);
  assert.deepEqual(b.values.map((v) => v && +v.s.toFixed(3)), [null, 0, -0.3, 0.3]);
  assert.equal(c.baseIndex, -1);
  assert.equal(c.values[1].note, "DNF");
  assert.ok(Number.isNaN(c.latest));
});

test("ændring formateres med fortegn", () => {
  assert.equal(fmtDelta(-0.1234), "−0.123 s");
  assert.equal(fmtDelta(0.5), "+0.500 s");
  assert.equal(fmtDelta(0.0001), "0 s");
  assert.equal(fmtDelta(-0.25, 2), "−0.25 s");
});
