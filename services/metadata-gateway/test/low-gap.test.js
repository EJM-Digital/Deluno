import assert from "node:assert/strict";
import test from "node:test";
import gateway from "../src/index.js";

// The gateway's small, mostly one-line promises (#1329): its health, method and path answers, the budgets, the cache lifetimes
// it asks Cloudflare for and the plain words it gives a failure. Each test drives the Worker's own fetch with a KV and a TMDb
// stand-in; nothing here reaches the network.

function kv({ failPuts = false, failGets = false } = {}) {
  const store = new Map();
  const puts = [];
  return {
    store,
    puts,
    get: async (key, type) => {
      if (failGets) throw new Error("KV unavailable");
      const value = store.get(key);
      if (value === undefined) return null;
      return type === "json" ? JSON.parse(value) : value;
    },
    put: async (key, value, options) => {
      if (failPuts) throw new Error("KV write refused");
      puts.push({ key, options });
      store.set(key, value);
    }
  };
}

function tmdb(routes) {
  const calls = [];
  const fetch = async (url) => {
    const parsed = new URL(url);
    calls.push(parsed.pathname);
    const body = routes[parsed.pathname];
    if (body === undefined) return { ok: false, status: 404, json: async () => ({}) };
    if (typeof body === "number") return { ok: false, status: body, json: async () => ({}) };
    return { ok: true, status: 200, json: async () => body };
  };
  return { calls, fetch };
}

async function ask(path, { env = {}, headers = {}, method = "GET", stub = tmdb({}) } = {}) {
  const realFetch = globalThis.fetch;
  globalThis.fetch = stub.fetch;
  try {
    return await gateway.fetch(new Request(`https://gateway.example${path}`, { method, headers }), { TMDB_API_KEY: "key", METADATA_CACHE: kv(), ...env });
  } finally {
    globalThis.fetch = realFetch;
  }
}

test("GET /health answers the service name and ok without needing a TMDb key or a cache", async () => {
  const response = await gateway.fetch(new Request("https://gateway.example/health"), {});
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { service: "deluno-metadata-gateway", status: "ok" });
});

test("any method other than GET answers 405 method_not_allowed with Allow GET", async () => {
  for (const method of ["POST", "PUT", "DELETE", "PATCH"]) {
    const response = await ask("/health", { method });
    assert.equal(response.status, 405, method);
    assert.equal(response.headers.get("Allow"), "GET");
    assert.deepEqual(await response.json(), { error: "method_not_allowed" });
  }
});

test("an unknown path answers 404 not_found as an HTTP response", async () => {
  const response = await ask("/no/such/route");
  assert.equal(response.status, 404);
  assert.deepEqual(await response.json(), { error: "not_found" });
});

test("the budgets are counted per CF-Connecting-IP and a request without it is counted under unknown", async () => {
  const spent = [];
  const env = { PROVIDER_CALLS_PER_INSTALL: { limit: async ({ key }) => { spent.push(key); return { success: true }; } } };
  const stub = tmdb({ "/3/person/7/external_ids": { imdb_id: "nm0000007" } });

  await ask("/person/7/imdb", { env: { ...env, METADATA_CACHE: kv() }, headers: { "CF-Connecting-IP": "203.0.113.9" }, stub });
  await ask("/person/7/imdb", { env: { ...env, METADATA_CACHE: kv() }, stub });

  assert.deepEqual(spent, ["203.0.113.9", "unknown"]);
});

test("search answers carry max-age 300 and the other TMDb answers max-age 600, the same on a cache hit", async () => {
  const stub = tmdb({
    "/3/search/movie": { results: [{ id: 1, title: "Alpha", release_date: "2000-01-01" }] },
    "/3/movie/popular": { results: [{ id: 2, title: "Beta" }] }
  });
  const cache = kv();

  for (const round of ["miss", "hit"]) {
    const search = await ask("/metadata/search?mediaType=movies&query=Alpha", { env: { METADATA_CACHE: cache }, stub });
    const popular = await ask("/metadata/movies/popular", { env: { METADATA_CACHE: cache }, stub });
    assert.equal(search.headers.get("Cache-Control"), "public, max-age=300", `search ${round}`);
    assert.equal(popular.headers.get("Cache-Control"), "public, max-age=600", `popular ${round}`);
  }
  assert.equal(stub.calls.filter((path) => path === "/3/search/movie").length, 1, "the second search came from the cache");
});

test("cache lifetimes: search 12 h, popular 3 h, translations 7 days, release dates and collection 6 h, person to IMDb 365 days", async () => {
  const stub = tmdb({
    "/3/search/movie": { results: [{ id: 1, title: "Alpha" }] },
    "/3/movie/popular": { results: [{ id: 2, title: "Beta" }] },
    "/3/movie/5/translations": { translations: [{ iso_639_1: "fr", data: { title: "Un" } }] },
    "/3/movie/5/release_dates": { results: [] },
    "/3/collection/9": { id: 9, name: "Saga", parts: [] },
    "/3/person/7/external_ids": { imdb_id: "nm0000007" }
  });
  const cache = kv();
  const env = { METADATA_CACHE: cache };

  await ask("/metadata/search?mediaType=movies&query=Alpha", { env, stub });
  await ask("/metadata/movies/popular", { env, stub });
  await ask("/metadata/movie/5/translations", { env, stub });
  await ask("/metadata/movie/5/release-dates", { env, stub });
  await ask("/metadata/movie/9/collection", { env, stub });
  await ask("/person/7/imdb", { env, stub });

  const ttl = (prefix) => cache.puts.find((put) => put.key.startsWith(prefix))?.options.expirationTtl;
  assert.equal(ttl("search:v8:"), 60 * 60 * 12);
  assert.equal(ttl("popular:v1:movies:1"), 60 * 60 * 3);
  assert.equal(ttl("translations:v1:movie:5"), 60 * 60 * 24 * 7);
  assert.equal(ttl("release-dates:v1:5"), 60 * 60 * 6);
  assert.equal(ttl("collection:v1:9"), 60 * 60 * 6);
  assert.equal(ttl("person-external-ids:v1:7"), 60 * 60 * 24 * 365);
});

test("the search cache key is versioned search:v8 and lower-cases the query", async () => {
  const cache = kv();
  const stub = tmdb({ "/3/search/movie": { results: [{ id: 1, title: "Alpha" }] } });

  await ask("/metadata/search?mediaType=movies&query=THE%20Matrix&year=1999", { env: { METADATA_CACHE: cache }, stub });

  assert.deepEqual([...cache.store.keys()], [`search:v8:${encodeURIComponent("movies|the matrix|1999|")}`]);
});

test("a KV write that throws never fails the request", async () => {
  const stub = tmdb({ "/3/search/movie": { results: [{ id: 1, title: "Alpha" }] } });

  const response = await ask("/metadata/search?mediaType=movies&query=Alpha", { env: { METADATA_CACHE: kv({ failPuts: true }) }, stub });

  assert.equal(response.status, 200);
  assert.equal((await response.json()).resultCount, 1);
});

test("the person to IMDb route uses KV directly, so a KV fault there is an unhandled error and not a degraded answer", async () => {
  const stub = tmdb({ "/3/person/7/external_ids": { imdb_id: "nm0000007" } });

  await assert.rejects(ask("/person/7/imdb", { env: { METADATA_CACHE: kv({ failGets: true }) }, stub }), /KV unavailable/);
  // A write that fails sits inside the route's own try, so it becomes the plain "unavailable" answer.
  const writeFault = await ask("/person/7/imdb", { env: { METADATA_CACHE: kv({ failPuts: true }) }, stub });
  assert.equal(writeFault.status, 503);
  assert.equal((await writeFault.json()).error, "provider_unavailable");
});

test("a TMDb 404 on a catalogue route answers 404 not_found and is remembered for 60 seconds", async () => {
  const cache = kv();
  const stub = tmdb({});

  const first = await ask("/metadata/tv/123/catalogue", { env: { METADATA_CACHE: cache }, stub });
  const callsAfterFirst = stub.calls.length;
  const second = await ask("/metadata/tv/123/catalogue", { env: { METADATA_CACHE: cache }, stub });

  assert.equal(first.status, 404);
  assert.deepEqual(await first.json(), { error: "not_found" });
  assert.equal(second.status, 404);
  assert.equal(stub.calls.length, callsAfterFirst, "the remembered failure stopped a second call to TMDb");
  assert.equal(cache.puts.find((put) => put.key === "err:v1:catalogue:v1:123").options.expirationTtl, 60);
});

test("a query over 160 characters or a bad year is 400 invalid_request before any provider call or budget is spent", async () => {
  let spent = 0;
  const env = { PROVIDER_CALLS_PER_INSTALL: { limit: async () => { spent++; return { success: true }; } } };
  const stub = tmdb({ "/3/search/movie": { results: [] } });
  const bad = [
    `/metadata/search?mediaType=movies&query=${"a".repeat(161)}`,
    "/metadata/search?mediaType=movies&query=Alpha&year=1799",
    "/metadata/search?mediaType=movies&query=Alpha&year=2101",
    "/metadata/search?mediaType=movies&query=Alpha&year=soon"
  ];

  for (const path of bad) {
    const response = await ask(path, { env, stub });
    assert.equal(response.status, 400, path);
    const body = await response.json();
    assert.equal(body.error, "invalid_request");
    assert.ok(body.message.length > 0);
  }
  const edge = await ask(`/metadata/search?mediaType=movies&query=${"a".repeat(160)}&year=1800`, { env, stub });
  assert.equal(edge.status, 200, "160 characters and the year 1800 are inside the limits");
  assert.equal(stub.calls.length, 1, "only the valid request reached TMDb");
  assert.equal(spent, 1, "only the valid request spent a provider call");
});

test("the person route answers 302 to the IMDb page with a day of caching and remembers the answer", async () => {
  const cache = kv();
  const stub = tmdb({ "/3/person/7/external_ids": { imdb_id: "nm0000007" } });

  const response = await ask("/person/7/imdb", { env: { METADATA_CACHE: cache }, stub });

  assert.equal(response.status, 302);
  assert.equal(response.headers.get("Location"), "https://www.imdb.com/name/nm0000007/");
  assert.equal(response.headers.get("Cache-Control"), "public, max-age=86400");
  assert.deepEqual(JSON.parse(cache.store.get("person-external-ids:v1:7")), { imdbId: "nm0000007" });
});

test("the person route answers 404 not_found when TMDb has no IMDb id and remembers that too", async () => {
  const cache = kv();
  const stub = tmdb({ "/3/person/8/external_ids": { imdb_id: null } });

  const first = await ask("/person/8/imdb", { env: { METADATA_CACHE: cache }, stub });
  const second = await ask("/person/8/imdb", { env: { METADATA_CACHE: cache }, stub });

  assert.equal(first.status, 404);
  assert.deepEqual(await first.json(), { error: "not_found" });
  assert.equal(second.status, 404);
  assert.equal(stub.calls.length, 1);
});

test("the person route maps TMDb 429 to 503 provider_busy with Retry-After 60, other failures to 503 provider_unavailable and a spent budget to 429", async () => {
  const busy = await ask("/person/7/imdb", { stub: tmdb({ "/3/person/7/external_ids": 429 }) });
  assert.equal(busy.status, 503);
  assert.equal(busy.headers.get("Retry-After"), "60");
  assert.equal((await busy.json()).error, "provider_busy");

  const down = await ask("/person/7/imdb", { stub: tmdb({ "/3/person/7/external_ids": 500 }) });
  assert.equal(down.status, 503);
  assert.equal((await down.json()).error, "provider_unavailable");

  const spent = await ask("/person/7/imdb", {
    env: { PROVIDER_CALLS_PER_INSTALL: { limit: async () => ({ success: false }) } },
    stub: tmdb({ "/3/person/7/external_ids": { imdb_id: "nm0000007" } })
  });
  assert.equal(spent.status, 429);
  assert.equal(spent.headers.get("Retry-After"), "60");
  assert.equal((await spent.json()).error, "rate_limited");
});

test("the 429 text is generic and carries no title query", async () => {
  const stub = tmdb({ "/3/search/movie": { results: [] } });
  const response = await ask("/metadata/search?mediaType=movies&query=Secret%20Private%20Title", {
    env: { PROVIDER_CALLS_PER_INSTALL: { limit: async () => ({ success: false }) } },
    stub
  });

  assert.equal(response.status, 429);
  const text = await response.text();
  assert.deepEqual(JSON.parse(text), { error: "rate_limited", message: "Please try title matching again shortly." });
  assert.equal(text.includes("Secret"), false);
});

test("the popular list is cached under popular:v1:{type}:{page} and served over HTTP, one entry per media type and page", async () => {
  const cache = kv();
  const stub = tmdb({
    "/3/movie/popular": { results: [{ id: 2, title: "Beta" }] },
    "/3/tv/popular": { results: [{ id: 3, name: "Gamma" }] }
  });
  const env = { METADATA_CACHE: cache };

  const movies = await ask("/metadata/movies/popular?page=2", { env, stub });
  const shows = await ask("/metadata/tv/popular", { env, stub });

  assert.equal(movies.status, 200);
  assert.equal((await movies.json()).results[0].title, "Beta");
  assert.equal((await shows.json()).results[0].name, "Gamma");
  assert.deepEqual([...cache.store.keys()].sort(), ["popular:v1:movies:2", "popular:v1:tv:1"]);
});
