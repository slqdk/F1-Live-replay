// RecordingPlayer — afspiller en .f1rec i browseren.
//
// Henter optagelsen i en let profil fra serveren
//   /recordings/<fil>?profile=map&from=<offset>   (svar-header X-Next-Offset)
// og gemmer linjerne råt (t/type/topic læses fra linjens start); JSON parses
// først når linjen anvendes. Søgning genopbygger tilstanden fra nærmeste
// checkpoint (hvert 5. minut, gemt som JSON-tekst) og anvender kun den
// SENESTE Position/CarData-linje — de bærer hele billedet hver gang.
// follow=true: optagelsen er i gang; nye linjer hentes hvert 2. sekund
// (forsinket live).

import { deepMerge } from "./merge.js";

const CHECKPOINT_MS = 5 * 60 * 1000;
const HEAVY = new Set(["Position", "CarData", "Position.z", "CarData.z"]);

function peek(line) {
  let t = null, type = null, topic = null;
  if (line.startsWith('{"t":')) {
    const end = line.indexOf(",", 5);
    t = Number(line.slice(5, end));
  }
  const ti = line.indexOf('"type":"');
  if (ti >= 0 && ti < 48) type = line.slice(ti + 8, line.indexOf('"', ti + 8));
  if (type === "feed") {
    const pi = line.indexOf('"topic":"');
    if (pi >= 0 && pi < 96) topic = line.slice(pi + 9, line.indexOf('"', pi + 9));
  }
  if (t === null || Number.isNaN(t)) {
    try {
      const o = JSON.parse(line);
      t = typeof o.t === "number" ? o.t : null;
      type = o.type ?? type;
      topic = o.topic ?? topic;
    } catch { return null; }
  }
  return { t, type, topic };
}

export class RecordingPlayer {
  constructor() {
    /** Serverens ur minus browserens (sat af app'en ud fra /api/public). */
    this.clockOffsetMs = 0;
    this.reset();
  }

  reset() {
    this.file = null;
    this.header = null;
    this.entries = [];
    this.offset = 0;
    this.remainder = "";
    this.state = {};
    this.cursor = 0;
    this.positionMs = 0;
    this.playing = false;
    this.speed = 1;
    this.follow = false;
    this.delayMs = 0;
    this.checkpoints = new Map();
    this.markers = [];
    this.startT = null;
    this.version = 0;
    this.tsOffsetMs = 0;
    this._tsCalibrated = false;
    this._lastTick = null;
    this._pollTimer = null;
    this._abort = null;
  }

  dispose() {
    if (this._pollTimer) clearInterval(this._pollTimer);
    if (this._abort) this._abort.abort();
    this.reset();
  }

  get startedUtcMs() {
    const s = this.header?.started;
    return s ? Date.parse(s) : 0;
  }

  get durationMs() {
    return this.entries.length ? this.entries[this.entries.length - 1].t : 0;
  }

  /** Hvor langt live-hovedet er nået (forsinket live): vægur siden optagestart. */
  get liveHeadMs() {
    const wall = this.startedUtcMs ? Date.now() + this.clockOffsetMs - this.startedUtcMs : 0;
    return Math.max(this.durationMs, wall);
  }

  /** Sessionens UTC-tid ved afspilningspositionen — på F1's ur (via linjernes
   *  "ts"), så sessionsuret passer selv hvis optagerens ur afveg. */
  clockUtcMs() {
    return this.startedUtcMs + this.positionMs + this.tsOffsetMs;
  }

  _calibrateTs() {
    if (this._tsCalibrated) return;
    for (let i = 0; i < Math.min(this.entries.length, 400); i++) {
      const e = this.entries[i];
      if (e.type !== "feed" || e.topic === "Position" || e.topic === "CarData") continue;
      try {
        const ts = JSON.parse(e.line).ts;
        const ms = ts ? Date.parse(ts) : NaN;
        if (!Number.isNaN(ms)) {
          this.tsOffsetMs = ms - (this.startedUtcMs + e.t);
          this._tsCalibrated = true;
          return;
        }
      } catch { /* næste */ }
    }
  }

  async load(file, { profile = "map", follow = false, onProgress } = {}) {
    this.dispose();
    this.file = file;
    this.profile = profile;
    this.follow = follow;
    this._abort = new AbortController();
    await this._fetchMore(onProgress);
    this._scanMarkers(0);
    if (follow) {
      this._pollTimer = setInterval(() => {
        const from = this.entries.length;
        this._fetchMore().then(() => this._scanMarkers(from)).catch(() => {});
      }, 2000);
    }
    this.seek(0);
  }

  async _fetchMore(onProgress) {
    const url = `/recordings/${encodeURIComponent(this.file)}?profile=${this.profile}&from=${this.offset}`;
    const res = await fetch(url, { signal: this._abort?.signal, cache: "no-store" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const next = Number(res.headers.get("X-Next-Offset"));
    const total = Number(res.headers.get("Content-Length")) || 0;
    let text = "";
    if (res.body && onProgress) {
      const reader = res.body.getReader();
      const dec = new TextDecoder();
      let got = 0;
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        got += value.length;
        text += dec.decode(value, { stream: true });
        onProgress(got, total);
      }
      text += dec.decode();
    } else {
      text = await res.text();
    }
    if (!Number.isNaN(next) && next > 0) this.offset = next;
    this._ingest(text);
    this._calibrateTs();
    this.version++;
  }

  _ingest(text) {
    const lines = text.split("\n");
    for (const line of lines) {
      if (!line) continue;
      const p = peek(line);
      if (!p) continue;
      if (p.type === "header") {
        if (!this.header) try { this.header = JSON.parse(line); } catch { /* ignorér */ }
        continue;
      }
      if (p.t === null) continue;
      // Linjer kan i sjældne tilfælde komme ude af orden (genåbnet fil) — indsæt sorteret
      const e = { t: p.t, type: p.type, topic: p.topic, line };
      const n = this.entries.length;
      if (n === 0 || this.entries[n - 1].t <= e.t) this.entries.push(e);
      else {
        let i = n - 1;
        while (i > 0 && this.entries[i - 1].t > e.t) i--;
        this.entries.splice(i, 0, e);
        if (i < this.cursor) this.cursor++;
      }
    }
  }

  _scanMarkers(fromIdx) {
    for (let i = fromIdx; i < this.entries.length; i++) {
      const e = this.entries[i];
      if (e.topic !== "SessionStatus" && e.topic !== "TrackStatus") continue;
      let d;
      try { d = JSON.parse(e.line).data; } catch { continue; }
      if (e.topic === "SessionStatus") {
        if (d?.Status === "Started") {
          if (this.startT === null) this.startT = e.t;
          this.markers.push({ t: e.t, kind: "start" });
        } else if (d?.Status === "Finished") this.markers.push({ t: e.t, kind: "finish" });
      } else if (d?.Status === "5") this.markers.push({ t: e.t, kind: "red" });
      else if (d?.Status === "4") this.markers.push({ t: e.t, kind: "sc" });
    }
  }

  _apply(e) {
    let o;
    try { o = JSON.parse(e.line); } catch { return; }
    if (e.type === "snapshot") {
      const data = o.data || {};
      for (const k of Object.keys(data)) {
        if (k.endsWith(".z")) continue;
        this.state[k] = data[k];
      }
    } else if (e.type === "feed" && o.topic && !o.topic.endsWith(".z")) {
      this.state[o.topic] = deepMerge(this.state[o.topic], o.data);
    }
  }

  /** Første index med t > ms. */
  _indexAfter(ms) {
    let lo = 0, hi = this.entries.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (this.entries[mid].t <= ms) lo = mid + 1; else hi = mid;
    }
    return lo;
  }

  _maybeCheckpoint(idx) {
    // idx = antal anvendte linjer; checkpoint ved hver 5-min-grænse
    const t = idx > 0 ? this.entries[idx - 1].t : 0;
    const slot = Math.floor(t / CHECKPOINT_MS);
    if (slot > 0 && !this.checkpoints.has(slot)) {
      this.checkpoints.set(slot, { idx, json: JSON.stringify(this.state) });
    }
  }

  seek(ms) {
    const max = this.follow ? this.liveHeadMs : this.durationMs;
    ms = Math.max(0, Math.min(ms, max));
    const end = this._indexAfter(ms);
    // Nærmeste checkpoint før målet
    let best = null;
    for (const [slot, cp] of this.checkpoints) {
      if (cp.idx <= end && (!best || cp.idx > best.idx)) best = cp;
      void slot;
    }
    if (best) {
      this.state = JSON.parse(best.json);
      this.cursor = best.idx;
    } else {
      this.state = {};
      this.cursor = 0;
    }
    const lastHeavy = new Map();
    for (let i = this.cursor; i < end; i++) {
      const e = this.entries[i];
      if (e.topic && HEAVY.has(e.topic)) { lastHeavy.set(e.topic, e); continue; }
      this._apply(e);
      if ((i + 1) % 200 === 0) this._maybeCheckpoint(i + 1);
    }
    for (const e of lastHeavy.values()) this._apply(e);
    this.cursor = end;
    this._maybeCheckpoint(end);
    this.positionMs = ms;
    this.version++;
  }

  advanceTo(ms) {
    if (ms < this.positionMs) { this.seek(ms); return; }
    if (ms - this.positionMs > 30000) { this.seek(ms); return; }
    const end = this._indexAfter(ms);
    while (this.cursor < end) {
      this._apply(this.entries[this.cursor]);
      this.cursor++;
      if (this.cursor % 200 === 0) this._maybeCheckpoint(this.cursor);
    }
    this.positionMs = ms;
    this.version++;
  }

  play() { this.playing = true; this._lastTick = null; }
  pause() { this.playing = false; }

  /** Kaldes hver frame med performance.now(). */
  tick(now) {
    if (!this.file) return;
    if (this.follow) {
      if (!this.playing) { this._lastTick = now; return; }
      const target = Math.max(0, this.liveHeadMs - this.delayMs);
      if (Math.abs(target - this.positionMs) > 5) this.advanceTo(target);
      return;
    }
    if (!this.playing) { this._lastTick = now; return; }
    if (this._lastTick === null) { this._lastTick = now; return; }
    const dt = (now - this._lastTick) * this.speed;
    this._lastTick = now;
    const target = Math.min(this.positionMs + dt, this.durationMs);
    this.advanceTo(target);
    if (target >= this.durationMs) this.playing = false;
  }

  nudge(seconds) {
    if (this.follow) {
      this.setDelay(this.delayMs - seconds * 1000);
      return;
    }
    this.seek(this.positionMs + seconds * 1000);
  }

  /** Forsinket live: genoptag efter pause = forsinkelsen er vokset tilsvarende. */
  resumeFollow() {
    this.delayMs = Math.max(0, this.liveHeadMs - this.positionMs);
    this.playing = true;
  }

  setDelay(ms) {
    this.delayMs = Math.max(0, Math.round(ms));
    if (this.follow && this.playing) this.advanceTo(Math.max(0, this.liveHeadMs - this.delayMs));
  }
}
