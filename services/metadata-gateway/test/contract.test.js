// What the gateway promises the app, said in the contract's own words (coverage register, engines): the year filter has its
// two names, the search answer has its one envelope, a search that cannot be answered is told apart from a title that is gone,
// a TMDb row carries the TheTVDB id, the artwork route serves only what it should and says why when it cannot, and the deployed
// configuration binds the cache and the two limits the code asks for. Refs #1296.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import metadataGateway, { lookupTmdb, mapTmdbResult } from "../src/index.js";

function memoryKv() {
  const values = new Map();
  return {
    async get(key, format) {
      const value = values.get(key) ?? null;
      return format === "json" && typeof value === "string" ? JSON.parse(value) : value;
    },
    async put(key, value) { values.set(key, value); }
  };
}

/** Runs one request through the gateway with TMDb (and the image host) answered by the stand-in. */
async function viaGateway(path, answer, env = {}) {
  const realFetch = globalThis.fetch;
  globalThis.fetch = answer.fetch;
  try {
    return await metadataGateway.fetch(
      new Request(`https://gw.example${path}`, { headers: { "CF-Connecting-IP": "192.0.2.7" } }),
      { TMDB_API_KEY: "k", METADATA_CACHE: memoryKv(), ...env });
  } finally {
    globalThis.fetch = realFetch;
  }
}

function tmdbAnswering(status, body = {}) {
  const calls = [];
  return {
    calls,
    fetch: async (url) => {
      calls.push(String(url));
      return { ok: status >= 200 && status < 300, status, json: async () => body };
    }
  };
}

test("the year filter is sent as year for a film and as first_air_date_year for a show", async () => {
  const urls = [];
  const request = async (url) => {
    urls.push(new URL(url));
    return { ok: true, status: 200, json: async () => ({ results: [] }) };
  };

  await lookupTmdb({ mediaType: "movies", query: "Heat", providerId: null, year: 1995 }, "k", request);
  await lookupTmdb({ mediaType: "tv", query: "Heat", providerId: null, year: 2008 }, "k", request);

  assert.equal(urls[0].pathname, "/3/search/movie");
  assert.equal(urls[0].searchParams.get("year"), "1995");
  assert.equal(urls[0].searchParams.has("first_air_date_year"), false);
  assert.equal(urls[1].pathname, "/3/search/tv");
  assert.equal(urls[1].searchParams.get("first_air_date_year"), "2008");
  assert.equal(urls[1].searchParams.has("year"), false);
  assert.equal(urls[0].searchParams.get("include_adult"), "false");
});

test("the search answer is the one envelope the contract states, with the count of its results", async () => {
  const tmdb = tmdbAnswering(200, { results: [{ id: 603, title: "The Matrix", release_date: "1999-03-30", poster_path: "/p.jpg" }] });

  const response = await viaGateway("/metadata/search?mediaType=movies&query=The%20Matrix", tmdb);

  assert.equal(response.status, 200);
  const body = await response.json();
  assert.deepEqual(Object.keys(body).sort(), ["mode", "provider", "resultCount", "results"]);
  assert.equal(body.provider, "deluno-broker");
  assert.equal(body.mode, "broker");
  assert.equal(body.resultCount, body.results.length);
  assert.equal(body.resultCount, 1);
});

test("a search TMDb cannot answer is a 503, never a title that is gone, and a repeat does not reach TMDb again", async () => {
  const notFound = tmdbAnswering(404);
  const kv = memoryKv();

  const first = await viaGateway("/metadata/search?mediaType=movies&query=Removed", notFound, { METADATA_CACHE: kv });
  const second = await viaGateway("/metadata/search?mediaType=movies&query=Removed", notFound, { METADATA_CACHE: kv });

  assert.equal(first.status, 503);
  assert.equal((await first.json()).error, "provider_unavailable");
  assert.equal(second.status, 503);
  assert.equal(notFound.calls.length, 1, "the failure is remembered");
});

test("TMDb saying it is busy is a 503 provider_busy with a minute to wait, remembered for the next ask", async () => {
  const busy = tmdbAnswering(429);
  const kv = memoryKv();

  const first = await viaGateway("/metadata/search?mediaType=tv&query=Busy", busy, { METADATA_CACHE: kv });
  const second = await viaGateway("/metadata/search?mediaType=tv&query=Busy", busy, { METADATA_CACHE: kv });

  assert.equal(first.status, 503);
  assert.equal(first.headers.get("Retry-After"), "60");
  assert.equal((await first.json()).error, "provider_busy");
  assert.equal(second.status, 503);
  assert.equal(second.headers.get("Retry-After"), "60");
  assert.equal(busy.calls.length, 1, "the busy answer is remembered");
});

test("the TheTVDB id is taken from the TMDb record's external ids, and is null when it has none", () => {
  const withId = mapTmdbResult({ id: 1396, name: "Breaking Bad", first_air_date: "2008-01-20", external_ids: { tvdb_id: 81189, imdb_id: "tt0903747" } }, "tv");
  const without = mapTmdbResult({ id: 5, name: "Obscure", first_air_date: "2001-01-01", external_ids: {} }, "tv");

  assert.equal(withId.tvDbId, "81189");
  assert.equal(without.tvDbId, null);
});

/** The Workers cache the artwork route keeps images in, as a map. */
function withWorkersCache(run) {
  const stored = new Map();
  const previous = globalThis.caches;
  globalThis.caches = {
    default: {
      async match(request) { return stored.get(request.url)?.clone(); },
      async put(request, response) { stored.set(request.url, response); }
    }
  };
  return run().finally(() => { globalThis.caches = previous; });
}

function imageHost(status = 200) {
  const calls = [];
  return {
    calls,
    fetch: async (url) => {
      calls.push(String(url));
      return new Response(status === 200 ? "image-bytes" : "no", { status, headers: { "Content-Type": "image/png" } });
    }
  };
}

test("artwork is served only for the sizes TMDb has and a plain file name, anything else is a 404 that reaches nobody", async () => {
  await withWorkersCache(async () => {
    const host = imageHost();
    for (const path of ["/artwork/w999/poster.jpg", "/artwork/w92/a%20b.jpg", "/artwork/w92/a%2Fb.jpg", "/artwork/w92", "/artwork/w92/x/y.jpg", `/artwork/w92/${"a".repeat(256)}.jpg`]) {
      const response = await viaGateway(path, host);
      assert.equal(response.status, 404, path);
      assert.equal((await response.json()).error, "not_found", path);
    }
    for (const size of ["w92", "w185", "w342", "w500", "w780", "w1280", "original"]) {
      assert.equal((await viaGateway(`/artwork/${size}/Poster_1-a.jpg`, imageHost())).status, 200, size);
    }
    assert.equal(host.calls.length, 0, "a refused path is never fetched upstream");
  });
});

test("artwork is fetched from TMDb's image host once, kept, and served for 30 days to any page with TMDb's content type", async () => {
  await withWorkersCache(async () => {
    const host = imageHost();

    const first = await viaGateway("/artwork/w342/abc.png", host);
    const again = await viaGateway("/artwork/w342/abc.png", host);

    assert.equal(first.status, 200);
    assert.equal(first.headers.get("Cache-Control"), `public, max-age=${60 * 60 * 24 * 30}, immutable`);
    assert.equal(first.headers.get("Content-Type"), "image/png");
    assert.equal(first.headers.get("Access-Control-Allow-Origin"), "*");
    assert.equal(await first.text(), "image-bytes");
    assert.equal(again.status, 200);
    assert.deepEqual(host.calls, ["https://image.tmdb.org/t/p/w342/abc.png"]);
  });
});

test("when TMDb's image host cannot give the file a 404 stays a 404 and anything else is a 503 artwork_unavailable", async () => {
  await withWorkersCache(async () => {
    const gone = await viaGateway("/artwork/w92/gone.jpg", imageHost(404));
    const broken = await viaGateway("/artwork/w92/broken.jpg", imageHost(500));
    const limited = await viaGateway("/artwork/w92/limited.jpg", imageHost(429));

    assert.equal(gone.status, 404);
    assert.equal((await gone.json()).error, "artwork_unavailable");
    assert.equal(broken.status, 503);
    assert.equal((await broken.json()).error, "artwork_unavailable");
    assert.equal(limited.status, 503);
  });
});

test("the deployed configuration binds the cache and the two limits the code asks for", () => {
  const text = readFileSync(new URL("../wrangler.jsonc", import.meta.url), "utf8").replace(/^\s*\/\/.*$/gm, "");
  const config = JSON.parse(text);

  assert.deepEqual(config.kv_namespaces.map((item) => item.binding), ["METADATA_CACHE"]);
  assert.match(config.kv_namespaces[0].id, /^[0-9a-f]{32}$/);
  const limits = Object.fromEntries(config.ratelimits.map((item) => [item.name, item]));
  assert.equal(limits.PROVIDER_CALLS_PER_INSTALL.namespace_id, "2001");
  assert.deepEqual(limits.PROVIDER_CALLS_PER_INSTALL.simple, { limit: 120, period: 60 });
  assert.equal(limits.PROVIDER_CALLS_SHARED.namespace_id, "2002");
  assert.deepEqual(limits.PROVIDER_CALLS_SHARED.simple, { limit: 400, period: 10 });
});

// ---- TheTVDB ------------------------------------------------------------------------------------------------------

import { TVDB_REMOTE_ID_TTL_SECONDS, getTvdbToken, lookupTvdbRemote, serveTvdbRoute, matchTvdbRoute } from "../src/tvdb.js";

/** TheTVDB v4 as far as the sign-in and the one route under test: the login hands out numbered tokens, the series route accepts only the newest. */
function tvdbWithExpiringTokens(routeAnswer) {
  const state = { logins: 0, seriesAsks: [], tokensAccepted: new Set() };
  return {
    state,
    fetch: async (url, init = {}) => {
      const parsed = new URL(url);
      if (parsed.pathname === "/v4/login") {
        state.logins++;
        const token = `token-${state.logins}`;
        if (routeAnswer.loginStatus && routeAnswer.loginStatus !== 200) return { ok: false, status: routeAnswer.loginStatus, json: async () => ({}) };
        state.tokensAccepted.add(token);
        return { ok: true, status: 200, json: async () => ({ data: { token } }) };
      }
      state.seriesAsks.push(init.headers.Authorization);
      const token = init.headers.Authorization.replace("Bearer ", "");
      if (routeAnswer.expireFirstToken && token === "token-1") return { ok: false, status: 401, json: async () => ({}) };
      if (routeAnswer.alwaysUnauthorised) return { ok: false, status: 401, json: async () => ({}) };
      return { ok: true, status: 200, json: async () => routeAnswer.body ?? { data: [] } };
    }
  };
}

test("a TheTVDB 401 logs in again once and asks again with the new token", async () => {
  const tvdb = tvdbWithExpiringTokens({ expireFirstToken: true, body: { data: [{ series: { id: 81189 } }] } });
  const env = { TVDB_API_KEY: "k", METADATA_CACHE: memoryKv() };

  const remote = await lookupTvdbRemote("tt0903747", env, tvdb.fetch);

  assert.equal(remote.tvdbId, "81189");
  assert.equal(tvdb.state.logins, 2, "one login for the first token, one more after the 401");
  assert.deepEqual(tvdb.state.seriesAsks, ["Bearer token-1", "Bearer token-2"]);
});

test("a TheTVDB that keeps saying 401, or refuses the login, is reported as a TheTVDB error after one retry", async () => {
  const stubborn = tvdbWithExpiringTokens({ alwaysUnauthorised: true });
  await assert.rejects(() => lookupTvdbRemote("tt1", { TVDB_API_KEY: "k", METADATA_CACHE: memoryKv() }, stubborn.fetch), (error) => error.status === 401);
  assert.equal(stubborn.state.seriesAsks.length, 2, "asked twice and no more");

  for (const [loginStatus, expected] of [[401, 401], [500, 502]]) {
    const refused = tvdbWithExpiringTokens({ loginStatus });
    await assert.rejects(() => getTvdbToken({ TVDB_API_KEY: "k", METADATA_CACHE: memoryKv() }, refused.fetch), (error) => error.status === expected, `login ${loginStatus}`);
  }
});

test("the remote id route answers the TheTVDB id for an IMDb id, null when TheTVDB has none, and keeps the answer for seven days", async () => {
  assert.equal(TVDB_REMOTE_ID_TTL_SECONDS, 60 * 60 * 24 * 7);
  const puts = [];
  const kv = { async get() { return null; }, async put(key, value, options) { puts.push({ key, value, options }); } };
  const json = (value, status = 200) => new Response(JSON.stringify(value), { status });
  const found = tvdbWithExpiringTokens({ body: { data: [{ series: { id: 81189 } }] } });
  const none = tvdbWithExpiringTokens({ body: { data: [] } });
  const route = matchTvdbRoute("/metadata/tvdb/remote/tt0903747");

  const hit = await serveTvdbRoute(route, new URL("https://gw.example/metadata/tvdb/remote/tt0903747"), { TVDB_API_KEY: "k", METADATA_CACHE: kv }, kv, json, found.fetch);
  const miss = await serveTvdbRoute(route, new URL("https://gw.example/metadata/tvdb/remote/tt0903747"), { TVDB_API_KEY: "k", METADATA_CACHE: kv }, kv, json, none.fetch);

  assert.equal((await hit.json()).tvdbId, "81189");
  assert.equal((await miss.json()).tvdbId, null);
  const remoteWrites = puts.filter((put) => put.key.includes(":remote:"));
  assert.equal(remoteWrites.length, 2);
  assert.ok(remoteWrites.every((put) => put.options.expirationTtl === 60 * 60 * 24 * 7), "kept for seven days");
});
