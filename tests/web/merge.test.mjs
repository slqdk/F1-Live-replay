// node --test tests/web/*.test.mjs — samme merge-regler som serveren (StateStore.DeepMerge)
import { test } from "node:test";
import assert from "node:assert/strict";
import { deepMerge, listOf, isIndexPatch } from "../../src/SlqF1Live.Server/wwwroot/js/merge.js";

test("objekter flettes, søskende bevares", () => {
  assert.deepEqual(deepMerge({ a: { b: 1, c: 2 } }, { a: { b: 9 } }), { a: { b: 9, c: 2 } });
});
test("numeriske nøgler i objekter flettes pr. kører", () => {
  assert.deepEqual(deepMerge({ Lines: { 1: { P: 1 }, 44: { P: 2 } } }, { Lines: { 44: { P: 3 } } }), { Lines: { 1: { P: 1 }, 44: { P: 3 } } });
});
test("index-patch opdaterer og udvider arrays", () => {
  const t = { Stints: [{ Compound: "SOFT", TotalLaps: 10 }] };
  deepMerge(t, { Stints: { 0: { TotalLaps: 12 } } });
  deepMerge(t, { Stints: { 1: { Compound: "HARD" } } });
  assert.deepEqual(t.Stints, [{ Compound: "SOFT", TotalLaps: 12 }, { Compound: "HARD" }]);
});
test("index-patch på tomt array (race control)", () => {
  assert.deepEqual(deepMerge({ Messages: [] }, { Messages: { 0: { Message: "GREEN" } } }), { Messages: [{ Message: "GREEN" }] });
});
test("_deleted fjerner nøgler", () => {
  assert.deepEqual(deepMerge({ L: { 1: 1, 2: 2 } }, { L: { _deleted: ["2"] } }), { L: { 1: 1 } });
});
test("skalarer og arrays i patch erstatter", () => {
  assert.equal(deepMerge({ x: 1 }, 5), 5);
  assert.deepEqual(deepMerge([1, 2], [9]), [9]);
});
test("listOf håndterer array og index-objekt", () => {
  assert.deepEqual(listOf([1, null, 2]), [1, 2]);
  assert.deepEqual(listOf({ 2: "c", 0: "a", 1: "b" }), ["a", "b", "c"]);
  assert.equal(isIndexPatch({ 0: 1, x: 2 }), false);
});
