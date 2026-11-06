import test from "node:test";

/** A Map-backed stand-in for caches.default, installed for one test and removed after it. */
export function installLocalCache({ putImpl } = {}) {
  const entries = new Map();
  const log = { matches: 0, puts: [] };
  globalThis.caches = {
    default: {
      async match(req) { log.matches++; const r = entries.get(req.url); return r ? r.clone() : undefined; },
      async put(req, res) {
        if (putImpl) await putImpl(req, res);
        log.puts.push(res.headers.get("Cache-Control"));
        entries.set(req.url, res);
      }
    }
  };
  return { entries, log };
}

export function localCacheTest(name, fn) {
  return test(name, async (t) => {
    const local = installLocalCache();
    try { await fn(local, t); } finally { delete globalThis.caches; }
  });
}
