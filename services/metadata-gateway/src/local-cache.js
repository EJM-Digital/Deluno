// First-level cache in front of KV: the Workers Cache API (caches.default), local to
// the data centre that served the request (#1527). KV stays the shared, longer-lived
// cache. Every call is safe where the Cache API is absent and never throws.

const origin = "https://local-cache.deluno.invalid/search/";
const urlFor = (key, failure) => new Request(`${origin}${failure ? "err/" : "ok/"}${encodeURIComponent(key)}`);

function store() {
  return typeof caches !== "undefined" ? caches.default : null;
}

/** { answer, failure }: the remembered answer or failure for this key, or both null. */
export async function readLocal(key) {
  const cache = store();
  if (!cache) return { answer: null, failure: null };
  try {
    const [ok, err] = await Promise.all([cache.match(urlFor(key, false)), cache.match(urlFor(key, true))]);
    return {
      answer: ok ? await ok.json() : null,
      failure: !ok && err ? await err.json() : null
    };
  } catch {
    return { answer: null, failure: null };
  }
}

export async function writeLocal(key, value, ttlSeconds, failure = false) {
  const cache = store();
  if (!cache) return;
  try {
    await cache.put(urlFor(key, failure), new Response(JSON.stringify(value), {
      headers: { "Content-Type": "application/json", "Cache-Control": `public, max-age=${ttlSeconds}` }
    }));
  } catch {
    // A local cache that will not take a write only costs a KV read.
  }
}
