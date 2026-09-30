import assert from "node:assert/strict";
import test from "node:test";
import gateway from "../src/index.js";

/** A Workers Rate Limiting binding stand-in: allows `allowed` calls per key, then refuses. */
function fakeLimiter(allowed) {
  const used = new Map();
  return {
    used,
    limit: async ({ key }) => {
      const count = (used.get(key) ?? 0) + 1;
      used.set(key, count);
      return { success: count <= allowed };
    }
  };
}

/** A KV stand-in that fails the test if anything counts a rate limit in it. */
function kv() {
  const store = new Map();
  return {
    store,
    get: async (key, type) => {
      const value = store.get(key);
      if (value === undefined) return null;
      return type === "json" ? JSON.parse(value) : value;
    },
    put: async (key, value) => {
      assert.ok(!key.startsWith("rate:"), "the limiter must not count in KV");
      store.set(key, value);
    }
  };
}

function stub() {
  const calls = [];
  return {
    calls,
    fetch: async (url) => {
      const text = String(url);
      calls.push(text);
      if (text.includes("api4.thetvdb.com/v4/login")) {
        return new Response(JSON.stringify({ data: { token: "t" } }), { status: 200 });
      }
      if (text.includes("api4.thetvdb.com")) {
        return new Response(JSON.stringify({ data: { id: 1, name: "Show" } }), { status: 200 });
      }
      return new Response(JSON.stringify({ results: [] }), { status: 200 });
    }
  };
}

async function call(path, env, upstream, ip = "203.0.113.9") {
  const realFetch = globalThis.fetch;
  globalThis.fetch = upstream.fetch;
  try {
    return await gateway.fetch(
      new Request(`https://gw.example${path}`, { headers: { "CF-Connecting-IP": ip } }),
      { TMDB_API_KEY: "k", TVDB_API_KEY: "k", METADATA_CACHE: kv(), ...env });
  } finally {
    globalThis.fetch = realFetch;
  }
}

test("a cache hit is never counted or limited", async () => {
  const perInstall = fakeLimiter(1);
  const upstream = stub();
  const env = { METADATA_CACHE: kv(), PROVIDER_CALLS_PER_INSTALL: perInstall };
  const path = "/metadata/search?mediaType=movies&query=matrix";

  assert.equal((await call(path, env, upstream)).status, 200);
  for (let i = 0; i < 10; i++) {
    assert.equal((await call(path, env, upstream)).status, 200);
  }
  assert.equal(perInstall.used.get("203.0.113.9"), 1);
  assert.equal(upstream.calls.length, 1);
});

test("the provider call after the install's budget gets 429 with Retry-After", async () => {
  const perInstall = fakeLimiter(120);
  const upstream = stub();
  const env = { METADATA_CACHE: kv(), PROVIDER_CALLS_PER_INSTALL: perInstall };

  for (let i = 0; i < 120; i++) {
    const ok = await call(`/metadata/search?mediaType=movies&query=film${i}`, env, upstream);
    assert.equal(ok.status, 200);
  }
  const limited = await call("/metadata/search?mediaType=movies&query=one-too-many", env, upstream);
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get("Retry-After"), "60");
  assert.equal((await limited.json()).error, "rate_limited");
  assert.equal(upstream.calls.length, 120, "the refused call never reached TMDb");

  // Another install is not affected.
  assert.equal((await call("/metadata/search?mediaType=movies&query=other", env, upstream, "198.51.100.4")).status, 200);
});

test("a refusal is not remembered as a provider failure", async () => {
  const upstream = stub();
  const cache = kv();
  const path = "/metadata/search?mediaType=movies&query=retry-me";
  const refused = await call(path, { METADATA_CACHE: cache, PROVIDER_CALLS_PER_INSTALL: fakeLimiter(0) }, upstream);
  assert.equal(refused.status, 429);
  const later = await call(path, { METADATA_CACHE: cache }, upstream);
  assert.equal(later.status, 200);
});

test("the shared ceiling is honoured and retries soon", async () => {
  const upstream = stub();
  const env = { METADATA_CACHE: kv(), PROVIDER_CALLS_SHARED: fakeLimiter(2) };
  assert.equal((await call("/metadata/search?mediaType=movies&query=a", env, upstream)).status, 200);
  assert.equal((await call("/metadata/search?mediaType=movies&query=b", env, upstream, "198.51.100.1")).status, 200);
  const limited = await call("/metadata/search?mediaType=movies&query=c", env, upstream, "198.51.100.2");
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get("Retry-After"), "10");
});

test("missing bindings allow the call", async () => {
  const upstream = stub();
  const env = { METADATA_CACHE: kv() };
  for (let i = 0; i < 40; i++) {
    assert.equal((await call(`/metadata/search?mediaType=movies&query=m${i}`, env, upstream)).status, 200);
  }
});

test("a limiter that throws never takes the gateway down", async () => {
  const upstream = stub();
  const broken = { limit: async () => { throw new Error("binding unavailable"); } };
  const response = await call(
    "/metadata/search?mediaType=movies&query=x",
    { METADATA_CACHE: kv(), PROVIDER_CALLS_PER_INSTALL: broken },
    upstream);
  assert.equal(response.status, 200);
});

test("TheTVDB and catalogue routes use the same budget, and TheTVDB cache hits are free", async () => {
  const perInstall = fakeLimiter(50);
  const upstream = stub();
  const env = { METADATA_CACHE: kv(), PROVIDER_CALLS_PER_INSTALL: perInstall };

  const first = await call("/metadata/tvdb/tv/1", env, upstream);
  assert.equal(first.status, 200);
  const spent = perInstall.used.get("203.0.113.9");
  assert.ok(spent >= 2, "the login and the lookup were both counted");
  assert.equal((await call("/metadata/tvdb/tv/1", env, upstream)).status, 200);
  assert.equal(perInstall.used.get("203.0.113.9"), spent);

  const refused = { METADATA_CACHE: kv(), PROVIDER_CALLS_PER_INSTALL: fakeLimiter(0) };
  const tvdb = await call("/metadata/tvdb/tv/2", refused, upstream);
  assert.equal(tvdb.status, 429);
  assert.equal(tvdb.headers.get("Retry-After"), "60");
  const tmdb = await call("/metadata/movie/603/release-dates", refused, upstream);
  assert.equal(tmdb.status, 429);
});
