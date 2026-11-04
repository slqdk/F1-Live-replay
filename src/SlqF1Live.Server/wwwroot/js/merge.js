// Samme merge-regler som serveren (Core/StateStore.DeepMerge):
// objekter flettes pr. nøgle, skalarer/arrays erstatter; en patch med
// index-nøgler ({"2": {...}}) opdaterer et array pr. index; "_deleted"
// fjerner nøgler/indexer. Patches skal være friske objekter (fra JSON.parse) —
// de indsættes uden kopi.

export function isIndexPatch(patch) {
  let any = false;
  for (const k of Object.keys(patch)) {
    if (k === "_deleted") continue;
    if (!/^\d+$/.test(k)) return false;
    any = true;
  }
  return any || "_deleted" in patch;
}

export function deepMerge(target, patch) {
  if (patch === null || typeof patch !== "object" || Array.isArray(patch)) return patch;

  if (Array.isArray(target) && isIndexPatch(patch)) {
    const keys = Object.keys(patch).filter((k) => k !== "_deleted").map(Number).sort((a, b) => a - b);
    for (const i of keys) {
      while (target.length < i) target.push(null);
      if (i === target.length) target.push(deepMerge(null, patch[i]));
      else target[i] = deepMerge(target[i], patch[i]);
    }
    if (Array.isArray(patch._deleted)) {
      const idx = [...new Set(patch._deleted.map(Number))].filter((n) => n >= 0 && n < target.length).sort((a, b) => b - a);
      for (const n of idx) target.splice(n, 1);
    }
    return target;
  }

  const result = target && typeof target === "object" && !Array.isArray(target) ? target : {};
  for (const k of Object.keys(patch)) {
    const v = patch[k];
    if (k === "_deleted") {
      if (Array.isArray(v)) for (const d of v) delete result[String(d)];
      continue;
    }
    result[k] = deepMerge(result[k], v);
  }
  return result;
}

/** F1 sender lister som array (snapshot) eller objekt med index-nøgler (patches). */
export function listOf(v) {
  if (!v) return [];
  if (Array.isArray(v)) return v.filter((x) => x != null);
  if (typeof v === "object") {
    return Object.keys(v)
      .filter((k) => k !== "_deleted")
      .sort((a, b) => Number(a) - Number(b))
      .map((k) => v[k])
      .filter((x) => x != null);
  }
  return [];
}

export function cloneJson(v) {
  return v === undefined ? undefined : JSON.parse(JSON.stringify(v));
}
