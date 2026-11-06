// node --test tests/web/*.test.mjs — pitprognosen (syntetiske data)
import { test } from "node:test";
import assert from "node:assert/strict";
import { secondsOf, raceOrder, teamStopTime, pitLossNow, predictPit, describePit, DEFAULT_STOP } from "../../src/SlqF1Live.Server/wwwroot/js/pit.js";

const close = (a, b, msg) => assert.ok(Math.abs(a - b) < 1e-9, `${msg}: ${a} != ${b}`);

/** TimingData.Lines ud fra [num, pos, gapToLeader, interval, extra]. */
function timing(rows) {
  const Lines = {};
  for (const [num, pos, gap, iv, extra] of rows) {
    Lines[num] = { Position: String(pos), Line: pos, GapToLeader: gap, IntervalToPositionAhead: { Value: iv }, ...(extra || {}) };
  }
  return { TimingData: { Lines } };
}

test("secondsOf læser gab og intervaller", () => {
  assert.equal(secondsOf("+1.320"), 1.32);
  assert.equal(secondsOf("1.320"), 1.32);
  close(secondsOf("+1:02.345"), 62.345, "minutter");
  assert.equal(secondsOf("12"), 12);
  for (const v of ["1 L", "+2 LAPS", "LAP 23", "", undefined, null]) assert.equal(secondsOf(v), null, String(v));
});

test("raceOrder: fører 0, overhalede biler via intervaller, udgåede springes over", () => {
  const s = timing([
    ["10", 1, "LAP 40", "LAP 40"],
    ["20", 2, "+3.000", "+3.000"],
    ["30", 3, "+9.500", "+6.500"],
    ["40", 4, "1 L", "+80.000"],
    ["50", 5, "", "", { Retired: true }],
    ["60", 6, "2 L", ""],
  ]);
  const o = raceOrder(s);
  assert.deepEqual(o.map((r) => r.num), ["10", "20", "30", "40", "50", "60"]);
  assert.equal(o[0].gap, 0);
  assert.equal(o[2].gap, 9.5);
  close(o[3].gap, 89.5, "overhalet: 9,5 + 80");
  assert.equal(o[3].approx, false);
  assert.equal(o[4].retired, true);
  close(o[5].gap, 90.5, "ukendt interval: forrige ikke-udgåede + 1 s");
  assert.equal(o[5].approx, true);
});

test("predictPit: midt i feltet, forrest og bagerst", () => {
  const s = timing([
    ["1", 1, "", ""], ["2", 2, "+5.0", "+5.0"], ["3", 3, "+12.0", "+7.0"],
    ["4", 4, "+20.0", "+8.0"], ["5", 5, "+27.0", "+7.0"], ["6", 6, "+40.0", "+13.0"],
  ]);
  const o = raceOrder(s);
  const mid = predictPit(o, "3", 22);
  assert.equal(mid.pos, 5);
  assert.equal(mid.ahead.num, "5"); close(mid.ahead.by, 7, "bag 5");
  assert.equal(mid.behind.num, "6"); close(mid.behind.by, 6, "foran 6");

  const lead = predictPit(raceOrder(timing([["1", 1, "", ""], ["2", 2, "+25.0", "+25.0"]])), "1", 22);
  assert.equal(lead.pos, 1);
  assert.equal(lead.ahead, null);
  close(lead.behind.by, 3, "føreren bevarer P1");

  const last = predictPit(o, "6", 22);
  assert.equal(last.pos, 6);
  assert.equal(last.behind, null);
  assert.equal(last.ahead.num, "5");

  assert.equal(predictPit(o, "99", 22), null);
  const out = raceOrder(timing([["1", 1, "", ""], ["2", 2, "", "", { Retired: true }]]));
  assert.equal(predictPit(out, "2", 22), null, "udgået kører");
});

test("pitLossNow: bane-tabel pr. banestatus; tallet indeholder allerede stoppet", () => {
  const circuit = { pitLoss: { normal: 21.84, sc: 14.16, vsc: 15.05 } };
  const at = (status) => pitLossNow({ TrackStatus: { Status: status } }, circuit, "1");
  assert.deepEqual(at("1"), { seconds: 21.84, kind: "normal", stop: DEFAULT_STOP, teamStop: false, estimated: false });
  assert.equal(at("2").kind, "normal", "gult flag");
  assert.equal(at("4").seconds, 14.16);
  assert.equal(at("6").seconds, 15.05);
  assert.equal(at("7").kind, "vsc", "VSC ending");
  assert.deepEqual([at("5").kind, at("5").seconds], ["red", 0]);
  const est = pitLossNow({}, null, "1");
  assert.equal(est.seconds, 22);
  assert.equal(est.estimated, true);
});

test("teamStopTime: median af holdets egne stop erstatter de 2,5 s", () => {
  const state = {
    DriverList: { 1: { TeamName: "Rød" }, 2: { TeamName: "Rød" }, 3: { TeamName: "Blå" } },
    PitStopSeries: { PitTimes: {
      1: [{ PitStop: { RacingNumber: "1", PitStopTime: "2.1" } }, { PitStop: { RacingNumber: "1", PitStopTime: "9.0" } }],
      2: { 0: { PitStop: { RacingNumber: "2", PitStopTime: "2.3" } }, 1: { PitStop: { RacingNumber: "2", PitStopTime: "0.4" } } },
      3: [{ PitStop: { RacingNumber: "3", PitStopTime: "1.9" } }],
    } },
  };
  assert.equal(teamStopTime(state, "1"), 2.3, "2,1 · 2,3 · 9,0 (0,4 og andre hold tæller ikke)");
  assert.equal(teamStopTime(state, "3"), 1.9);
  assert.equal(teamStopTime({ DriverList: state.DriverList }, "1"), null);
  const loss = pitLossNow(state, { pitLoss: { normal: 21.84, sc: 14.16, vsc: 15.05 } }, "2");
  close(loss.seconds, 21.84 - 2.5 + 2.3, "holdets stoptid");
  assert.equal(loss.teamStop, true);
});

test("describePit: dansk tekst til tårnet", () => {
  const tlaOf = (n) => ({ 1: "AAA", 2: "BBB", 3: "CCC" })[n];
  const loss = { seconds: 21.84, kind: "normal", stop: 2.5, teamStop: false, estimated: false };
  const p = { num: "1", pos: 7, ahead: { num: "2", by: 2.14 }, behind: { num: "3", by: 1.4 }, approx: false, inPit: false };
  assert.deepEqual(describePit(p, loss, tlaOf), {
    main: "AAA ved pit nu → P7 · 2,1 s bag BBB · 1,4 s foran CCC",
    detail: "pittab 21,8 s inkl. 2,5 s stop, grønt flag",
  });
  const sc = describePit({ ...p, approx: true, behind: null }, { ...loss, kind: "sc", seconds: 13.9, stop: 2.2, teamStop: true, estimated: true }, tlaOf);
  assert.equal(sc.main, "AAA ved pit nu → P7 (ca.) · 2,1 s bag BBB");
  assert.equal(sc.detail, "pittab 13,9 s inkl. 2,2 s stop (holdets snit), safety car, anslået");
  assert.equal(describePit({ ...p, inPit: true }, loss, tlaOf).main, "AAA er i pitten nu");
  assert.match(describePit(p, { ...loss, kind: "red", seconds: 0 }, tlaOf).main, /^Rødt flag/);
});
