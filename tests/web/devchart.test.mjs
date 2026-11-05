// node --test tests/web/*.test.mjs — udviklingsgrafens rene funktioner (ingen DOM)
import { test } from "node:test";
import assert from "node:assert/strict";
import { assignShapes, colourDistance, fmtGap, fmtM, niceStep, tickDecimals, SHAPES, OUTLIER, SIMILAR } from "../../src/SlqF1Live.Server/wwwroot/js/devchart.js";

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

test("de mest farvelignende hold deler aldrig form", () => {
  // 7 hold er parvis farvelignende i 2026, men der er kun 6 former: det
  // eneste par, der må dele, er det mest forskellige (Haas/Williams, ΔE 24,5).
  const shapes = assignShapes(TEAMS);
  let closest = Infinity;
  for (const a of TEAMS) for (const b of TEAMS) {
    if (a.name < b.name && shapes.get(a.name) === shapes.get(b.name)) closest = Math.min(closest, colourDistance(a.colour, b.colour));
  }
  assert.ok(closest >= 24, `tætteste par med samme form: ${closest.toFixed(1)}`);
  for (const s of shapes.values()) assert.ok(SHAPES.includes(s) && s !== OUTLIER);
});

test("når nogen må dele form, er det det mest forskellige par", () => {
  // 7 næsten ens grå (parvis under tærsklen): yderpunkterne deler form
  const greys = [0x80, 0x84, 0x88, 0x8c, 0x90, 0x94, 0x98].map((v, i) => ({ name: `T${i}`, colour: "#" + v.toString(16).repeat(3) }));
  for (const a of greys) for (const b of greys) if (a !== b) assert.ok(colourDistance(a.colour, b.colour) < SIMILAR);
  const shapes = assignShapes(greys);
  const shared = greys.filter((g) => greys.some((h) => h !== g && shapes.get(h.name) === shapes.get(g.name))).map((g) => g.name);
  assert.deepEqual(shared, ["T0", "T6"]);
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
