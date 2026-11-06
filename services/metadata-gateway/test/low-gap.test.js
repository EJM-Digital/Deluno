import assert from "node:assert/strict";
import test from "node:test";
import { localCacheTest } from "./local-cache-stub.js";
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

const localUrl = (key) => `https://local-cache.deluno.invalid/search/ok/${encodeURIComponent(key)}`;

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

localCacheTest("search answers carry max-age 300 and the other TMDb answers max-age 600, the same on a cache hit", async () => {
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

localCacheTest("cache lifetimes: search 12 h, popular 3 h, translations 7 days, release dates and collection 6 h, person to IMDb 365 days", async (local) => {
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
  assert.deepEqual(local.log.puts, ["public, max-age=43200"], "search is kept only in the local cache, for 12 hours");
  assert.equal(ttl("search:v9:"), undefined, "search never writes KV");
  assert.equal(ttl("popular:v1:movies:1"), 60 * 60 * 3);
  assert.equal(ttl("translations:v1:movie:5"), 60 * 60 * 24 * 7);
  assert.equal(ttl("release-dates:v1:5"), 60 * 60 * 6);
  assert.equal(ttl("collection:v1:9"), 60 * 60 * 6);
  assert.equal(ttl("person-external-ids:v1:7"), 60 * 60 * 24 * 365);
});

localCacheTest("the search cache key is versioned search:v9 and lower-cases the query", async (local) => {
  const cache = kv();
  const stub = tmdb({ "/3/search/movie": { results: [{ id: 1, title: "Alpha" }] } });

  await ask("/metadata/search?mediaType=movies&query=THE%20Matrix&year=1999", { env: { METADATA_CACHE: cache }, stub });

  assert.deepEqual([...local.entries.keys()], [localUrl(`search:v9:${encodeURIComponent("movies|the matrix|1999||1")}`)]);
  assert.equal(cache.store.size, 0);
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
  // The write runs after the answer (#1527), so a write that fails no longer fails the lookup.
  const writeFault = await ask("/person/7/imdb", { env: { METADATA_CACHE: kv({ failPuts: true }) }, stub });
  assert.equal(writeFault.status, 302);
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

test("release dates are served over HTTP as the earliest of each kind, cached under release-dates:v1:{id} for 6 hours", async () => {
  const cache = kv();
  const stub = tmdb({
    "/3/movie/5/release_dates": {
      results: [
        { iso_3166_1: "US", release_dates: [{ type: 3, release_date: "2020-05-01T00:00:00.000Z" }, { type: 4, release_date: "2020-07-01T00:00:00.000Z" }] },
        { iso_3166_1: "GB", release_dates: [{ type: 3, release_date: "2020-04-01T00:00:00.000Z" }, { type: 5, release_date: "2020-09-01T00:00:00.000Z" }, { type: 1, release_date: "2019-01-01T00:00:00.000Z" }] }
      ]
    }
  });

  const response = await ask("/metadata/movie/5/release-dates", { env: { METADATA_CACHE: cache }, stub });

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("Cache-Control"), "public, max-age=600");
  const body = await response.json();
  assert.deepEqual([body.inCinemas, body.digital, body.physical], ["2020-04-01", "2020-07-01", "2020-09-01"]);
  assert.equal(cache.puts.find((put) => put.key === "release-dates:v1:5").options.expirationTtl, 60 * 60 * 6);
  assert.equal((await ask("/metadata/movie/6/release-dates", { stub })).status, 404);
});

test("a collection is served over HTTP under collection:v1:{id} with its member films, and an unknown one is 404 not_found", async () => {
  const cache = kv();
  const stub = tmdb({ "/3/collection/9": { id: 9, name: " Saga ", parts: [{ id: 1, title: "Part One", release_date: "2001-01-01" }, { id: 2 }] } });

  const response = await ask("/metadata/movie/9/collection", { env: { METADATA_CACHE: cache }, stub });
  const body = await response.json();

  assert.equal(response.status, 200);
  assert.equal(body.name, "Saga");
  assert.deepEqual(body.movies.map((movie) => [movie.providerId, movie.title, movie.year]), [["1", "Part One", 2001]]);
  assert.ok(cache.store.has("collection:v1:9"));
  const missing = await ask("/metadata/movie/10/collection", { stub });
  assert.equal(missing.status, 404);
  assert.deepEqual(await missing.json(), { error: "not_found" });
});

test("translations are served over HTTP and cached under translations:v1:{kind}:{id}, one entry per kind", async () => {
  const cache = kv();
  const stub = tmdb({
    "/3/movie/5/translations": { translations: [{ iso_639_1: "fr", iso_3166_1: "FR", data: { title: "Un film", overview: "" } }, { iso_639_1: "de", data: {} }] },
    "/3/tv/5/translations": { translations: [{ iso_639_1: "es", data: { name: "Una serie" } }] }
  });

  const movie = await (await ask("/metadata/movie/5/translations", { env: { METADATA_CACHE: cache }, stub })).json();
  const show = await (await ask("/metadata/tv/5/translations", { env: { METADATA_CACHE: cache }, stub })).json();

  assert.deepEqual(movie.translations, [{ language: "fr", region: "FR", title: "Un film", overview: null, tagline: null }]);
  assert.equal(show.translations[0].title, "Una serie");
  assert.deepEqual([...cache.store.keys()].sort(), ["translations:v1:movie:5", "translations:v1:tv:5"]);
});

test("the artwork route is answered before the limiter wraps fetch, so it spends no provider call", async () => {
  let spent = 0;
  const env = { PROVIDER_CALLS_PER_INSTALL: { limit: async () => { spent++; return { success: true }; } } };
  const realCaches = globalThis.caches;
  const realFetch = globalThis.fetch;
  const stored = new Map();
  globalThis.caches = { default: { match: async (request) => stored.get(request.url), put: async (request, response) => { stored.set(request.url, response); } } };
  globalThis.fetch = async () => new Response("image-bytes", { status: 200, headers: { "Content-Type": "image/jpeg" } });
  try {
    const response = await gateway.fetch(new Request("https://gateway.example/artwork/w185/poster.jpg"), { TMDB_API_KEY: "key", METADATA_CACHE: kv(), ...env });

    assert.equal(response.status, 200);
    assert.equal(await response.text(), "image-bytes");
    assert.equal(spent, 0);
    assert.match(response.headers.get("Cache-Control"), /max-age=2592000/);
  } finally {
    globalThis.caches = realCaches;
    globalThis.fetch = realFetch;
  }
});

// #1473: a search page is TMDb's whole page of twenty, and the next page is one request away.
function searchPage(page, count, totalPages, totalResults) {
  return {
    page,
    total_pages: totalPages,
    total_results: totalResults,
    results: Array.from({ length: count }, (_, i) => ({ id: page * 100 + i + 1, title: `Kickboxer ${page}-${i + 1}`, release_date: "1989-01-01" }))
  };
}

test("a search returns all twenty matches of TMDb's page with its page numbers", async () => {
  const stub = tmdb({ "/3/search/movie": searchPage(1, 20, 3, 45) });
  const body = await (await ask("/metadata/search?mediaType=movies&query=Kickboxer", { stub })).json();
  assert.equal(body.resultCount, 20);
  assert.equal(body.results.length, 20);
  assert.deepEqual([body.page, body.totalPages, body.totalResults], [1, 3, 45]);
});

localCacheTest("page is passed to TMDb, kept in its own cache entry, and defaults to 1", async (local) => {
  const cache = kv();
  const asked = [];
  const stub = {
    calls: [],
    fetch: async (url) => {
      const parsed = new URL(url);
      asked.push(parsed.searchParams.get("page"));
      const page = Number(parsed.searchParams.get("page"));
      return { ok: true, status: 200, json: async () => searchPage(page, page === 3 ? 5 : 20, 3, 45) };
    }
  };
  const env = { METADATA_CACHE: cache };
  await ask("/metadata/search?mediaType=tv&query=Kickboxer", { env, stub });
  const third = await (await ask("/metadata/search?mediaType=tv&query=Kickboxer&page=3", { env, stub })).json();
  await ask("/metadata/search?mediaType=tv&query=Kickboxer&page=3", { env, stub });
  assert.deepEqual(asked, ["1", "3"], "the repeat came from the cache");
  assert.deepEqual([third.page, third.resultCount], [3, 5]);
  assert.deepEqual([...local.entries.keys()].sort(), [
    localUrl(`search:v9:${encodeURIComponent("tv|kickboxer|||1")}`),
    localUrl(`search:v9:${encodeURIComponent("tv|kickboxer|||3")}`)
  ]);
});

test("a TMDb answer with no page numbers still reports the page asked for", async () => {
  const stub = tmdb({ "/3/search/movie": { results: [{ id: 1, title: "Alpha" }] } });
  const body = await (await ask("/metadata/search?mediaType=movies&query=Alpha&page=2", { stub })).json();
  assert.deepEqual([body.page, body.totalPages, body.totalResults], [2, 2, 1]);
});

test("an exact provider id reports one page of one", async () => {
  const stub = tmdb({ "/3/movie/603": { id: 603, title: "The Matrix", release_date: "1999-03-30", genres: [], external_ids: {} } });
  const body = await (await ask("/metadata/search?mediaType=movies&query=x&providerId=603", { stub })).json();
  assert.deepEqual([body.page, body.totalPages, body.totalResults, body.resultCount], [1, 1, 1, 1]);
});

test("a page outside 1 to 500, or not a whole number, is a 400", async () => {
  for (const page of ["0", "501", "abc", "-1", "1.5", "9999999"]) {
    const response = await ask(`/metadata/search?mediaType=movies&query=Alpha&page=${page}`);
    assert.equal(response.status, 400, page);
    assert.match((await response.json()).message, /page must be between 1 and 500/);
  }
  assert.equal((await ask("/metadata/search?mediaType=movies&query=Alpha&page=500", { stub: tmdb({ "/3/search/movie": { results: [] } }) })).status, 200);
  assert.equal((await ask("/metadata/search?mediaType=movies&query=Alpha&page=1", { stub: tmdb({ "/3/search/movie": { results: [] } }) })).status, 200);
});

test("lookupTmdbPage sends the key, query, page and year to TMDb and drops cards without an id or a title", async () => {
  const { lookupTmdbPage } = await import("../src/index.js");
  const seen = [];
  const request = async (url) => {
    seen.push(new URL(url));
    return { ok: true, status: 200, json: async () => ({ results: [null, { title: "No id" }, { id: 2 }, { id: 3, name: "Show" }, { id: 4, title: "Film" }] }) };
  };
  const found = await lookupTmdbPage({ mediaType: "tv", query: "Q", providerId: null, year: null, page: 4 }, "KEY", request);
  const url = seen[0];
  assert.equal(url.searchParams.get("api_key"), "KEY");
  assert.equal(url.searchParams.get("query"), "Q");
  assert.equal(url.searchParams.get("page"), "4");
  assert.equal(url.searchParams.has("first_air_date_year"), false);
  assert.equal(url.searchParams.has("year"), false);
  assert.deepEqual(found.results.map((item) => item.providerId), ["3", "4"]);
  const noPage = await lookupTmdbPage({ mediaType: "movies", query: "Q", providerId: null, year: null }, "KEY", request);
  assert.equal(noPage.page, 1);
  assert.equal(seen[1].searchParams.get("page"), "1");
});

test("lookupTmdbPage takes TMDb's totals only when they are whole numbers and never reports fewer pages than the page asked for", async () => {
  const { lookupTmdbPage } = await import("../src/index.js");
  const lookup = { mediaType: "movies", query: "Q", providerId: null, year: null, page: 2 };
  const answer = (body) => async () => ({ ok: true, status: 200, json: async () => body });
  const one = [{ id: 1, title: "A" }];
  const good = await lookupTmdbPage(lookup, "k", answer({ results: one, total_pages: 7, total_results: 130 }));
  assert.deepEqual([good.totalPages, good.totalResults], [7, 130]);
  const exactlyTwo = await lookupTmdbPage(lookup, "k", answer({ results: one, total_pages: 2, total_results: 0 }));
  assert.deepEqual([exactlyTwo.totalPages, exactlyTwo.totalResults], [2, 0]);
  const below = await lookupTmdbPage(lookup, "k", answer({ results: one, total_pages: 1, total_results: -5 }));
  assert.deepEqual([below.totalPages, below.totalResults], [2, 0]);
  const text = await lookupTmdbPage(lookup, "k", answer({ results: one, total_pages: "9", total_results: "9" }));
  assert.deepEqual([text.totalPages, text.totalResults], [2, 1]);
  const none = await lookupTmdbPage(lookup, "k", answer({}));
  assert.deepEqual([none.totalPages, none.totalResults, none.results.length], [2, 0, 0]);
});

test("an exact lookup of a title TMDb has no record of is an empty page, and a page of ' 2 ' is page 2", async () => {
  const { lookupTmdbPage, parseLookup } = await import("../src/index.js");
  const found = await lookupTmdbPage({ mediaType: "movies", query: "x", providerId: "9", page: 1 }, "k", async () => ({ ok: true, status: 200, json: async () => ({}) }));
  assert.deepEqual(found, { page: 1, totalPages: 1, totalResults: 0, results: [] });
  assert.equal(parseLookup(new URLSearchParams("mediaType=tv&query=a&page=%202%20")).value.page, 2);
  assert.equal(parseLookup(new URLSearchParams("mediaType=tv&query=a&page=")).value.page, 1);
});
