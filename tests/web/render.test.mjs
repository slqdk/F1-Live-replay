// node --test tests/web/*.test.mjs — banekortets omrids (ingen DOM)
import { test } from "node:test";
import assert from "node:assert/strict";
import { prepareOutline } from "../../src/SlqF1Live.Server/wwwroot/js/render.js";

const square = { rotation: 0, x: [0, 100, 100, 0, 0, 50, 100, 50, 0, 100], y: [0, 0, 100, 100, 50, 0, 50, 100, 50, 50], corners: [] };

test("omridset drejes rotation + 90° om midten", () => {
  const o = prepareOutline({ ...square, x: [0, 100, ...square.x.slice(2)], y: [50, 50, ...square.y.slice(2)] });
  // (0,50) og (100,50) ligger vandret om midten (50,50); efter 90° ligger de lodret
  const [a, b] = o.pts;
  assert.ok(Math.abs(a[0] - 50) < 1e-9 && Math.abs(b[0] - 50) < 1e-9, `${a} ${b}`);
  assert.ok(Math.abs(Math.abs(a[1] - b[1]) - 100) < 1e-9);
});

test("bilernes positioner får samme transformation som omridset", () => {
  const o = prepareOutline({ ...square, rotation: 258 });
  const [x, y] = o.rot(square.x[3], square.y[3]);
  assert.deepEqual([x, y], o.pts[3]);
});

test("svingnumre ligger uden for banen og er med i rammen", () => {
  const o = prepareOutline({ ...square, corners: [{ n: "4a", x: 100, y: 100, angle: 45 }] });
  assert.equal(o.corners[0].label, "4a");
  const { x, y } = o.corners[0];
  assert.ok(x >= o.bbox.minX && x <= o.bbox.maxX && y >= o.bbox.minY && y <= o.bbox.maxY);
  const far = Math.hypot(x - 50, y - 50) > Math.hypot(50, 50);
  assert.ok(far, "label skal ligge længere ude end hjørnet");
});
