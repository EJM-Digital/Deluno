import assert from "node:assert/strict";
import test from "node:test";
import gateway from "../src/index.js";
import {
  ERROR_CACHE_SECONDS,
  MAX_KEY_LENGTH,
  boundCacheKey,
  readCachedError
} from "../src/cache.js";
import { MAX_EPISODE_GROUP_PAGES, lookupTvdbEpisodeGroups } from "../src/tvdb.js";

/** A KV stand-in that refuses what the real one refuses: a key over 512 bytes. */
function strictKv({ failReads = false } = {}) {
  const store = new Map();
  const puts = [];
  const check = (key) => {
    if (new TextEncoder().encode(key).length > 512) {
      throw new Error("KV key too long");
    }
  };
  return {
    store,
    puts,
    get: async (key, type) => {
      check(key);
      if (failReads && (key.startsWith("tvdb:") || key.startsWith("err:"))) throw new Error("KV unavailable");
      const value = store.get(key);
      if (value === undefined) return null;
      return type === "json" ? JSON.parse(value) : value;
    },
    put: async (key, value, options) => {
      check(key);
      puts.push({ key, options });
      store.set(key, value);
    }
  };
}

function stubTvdb(routes) {
  const calls = [];
  return {
    calls,
    fetch: async (url) => {
      const parsed = new URL(url);
      if (parsed.pathname === "/v4/login") {
        return { ok: true, status: 200, json: async () => ({ data: { token: "tok-1" }, status: "success" }) };
      }
      calls.push(parsed.pathname + parsed.search);
      const key = `${parsed.pathname}${parsed.searchParams.has("page") ? `?page=${parsed.searchParams.get("page")}` : ""}`;
      const body = routes[key] ?? routes[parsed.pathname];
      if (body === undefined) return { ok: false, status: 404, json: async () => ({}) };
      if (typeof body === "number") return { ok: false, status: body, json: async () => ({}) };
      return { ok: true, status: 200, json: async () => body };
    }
  };
}

async function withFetch(stub, run) {
  const realFetch = globalThis.fetch;
  globalThis.fetch = stub.fetch;
  try {
    return await run();
  } finally {
    globalThis.fetch = realFetch;
  }
}

test("a cache key over the limit is replaced by a hash and a short one is left alone", async () => {
  assert.equal(await boundCacheKey("tvdb:v1:series:81189"), "tvdb:v1:series:81189");

  const long = `tvdb:v1:search:${encodeURIComponent("世".repeat(160))}`;
  assert.ok(new TextEncoder().encode(long).length > 512, "the unbounded key is over the KV limit");
  const bounded = await boundCacheKey(long);
  assert.ok(bounded.length <= MAX_KEY_LENGTH);
  assert.equal(bounded, await boundCacheKey(long), "the same key hashes the same way");
  assert.notEqual(bounded, await boundCacheKey(long + "x"), "a different key hashes differently");
});

test("a long non-Latin TheTVDB search is answered and cached under a bounded key", async () => {
  const kv = strictKv();
  const query = "世".repeat(160);
  const stub = stubTvdb({ "/v4/search": { status: "success", data: [] } });
  const response = await withFetch(stub, () => gateway.fetch(
    new Request(`https://gw.example/metadata/tvdb/search?query=${encodeURIComponent(query)}`),
    { TVDB_API_KEY: "k", METADATA_CACHE: kv }));
  assert.equal(response.status, 200);
  assert.ok(kv.puts.length > 0);
  assert.ok(kv.puts.every((put) => put.key.length <= 250));
});

test("a KV read that fails does not fail the request", async () => {
  const stub = stubTvdb({ "/v4/series/9/extended": 404 });
  const response = await withFetch(stub, () => gateway.fetch(
    new Request("https://gw.example/metadata/tvdb/tv/9"),
    { TVDB_API_KEY: "k", METADATA_CACHE: strictKv({ failReads: true }) }));
  assert.equal(response.status, 404);
});

test("a TheTVDB failure is remembered for a minute per key, so a repeat never reaches upstream", async () => {
  const kv = strictKv();
  const env = { TVDB_API_KEY: "k", METADATA_CACHE: kv };
  const stub = stubTvdb({ "/v4/series/5/extended": 404, "/v4/series/6/extended": 429, "/v4/series/7/extended": 500 });

  await withFetch(stub, async () => {
    for (const id of ["5", "6", "7"]) {
      const first = await gateway.fetch(new Request(`https://gw.example/metadata/tvdb/tv/${id}`), env);
      const upstreamCalls = stub.calls.length;
      const second = await gateway.fetch(new Request(`https://gw.example/metadata/tvdb/tv/${id}`), env);
      assert.equal(second.status, first.status);
      assert.deepEqual(await second.json(), await first.json());
      assert.equal(stub.calls.length, upstreamCalls, `series ${id} was not asked for again`);
    }
  });

  const errorPuts = kv.puts.filter((put) => put.key.startsWith("err:"));
  assert.equal(errorPuts.length, 3);
  assert.ok(errorPuts.every((put) => put.options.expirationTtl === ERROR_CACHE_SECONDS));
  assert.ok(ERROR_CACHE_SECONDS >= 60, "KV will not hold a key for less than a minute");

  // The busy answer keeps its retry hint when replayed.
  const busy = await withFetch(stub, () => gateway.fetch(new Request("https://gw.example/metadata/tvdb/tv/6"), env));
  assert.equal(busy.status, 503);
  assert.equal(busy.headers.get("Retry-After"), "60");
});

test("a remembered failure for one key does not answer another key", async () => {
  const kv = strictKv();
  const env = { TVDB_API_KEY: "k", METADATA_CACHE: kv };
  const stub = stubTvdb({ "/v4/series/5/extended": 404 });
  await withFetch(stub, () => gateway.fetch(new Request("https://gw.example/metadata/tvdb/tv/5"), env));
  assert.ok(await readCachedError(kv, "tvdb:v1:series:5"));
  assert.equal(await readCachedError(kv, "tvdb:v1:series:6"), null);
});

test("a TMDb title-search failure is remembered too", async () => {
  const kv = strictKv();
  let upstream = 0;
  const realFetch = globalThis.fetch;
  globalThis.fetch = async () => {
    upstream++;
    return { ok: false, status: 500, json: async () => ({}) };
  };
  try {
    const env = { TMDB_API_KEY: "k", METADATA_CACHE: kv };
    const url = "https://gw.example/metadata/search?query=Dune&mediaType=movies";
    const first = await gateway.fetch(new Request(url), env);
    const asked = upstream;
    const second = await gateway.fetch(new Request(url), env);
    assert.equal(first.status, 503);
    assert.equal(second.status, 503);
    assert.ok(asked > 0);
    assert.equal(upstream, asked, "the repeat did not reach TMDb");
  } finally {
    globalThis.fetch = realFetch;
  }
});

test("the episode-groups route stops at its page budget however long the series is", async () => {
  const endless = { data: { episodes: [{ id: 1, seasonNumber: 1, number: 1 }] }, links: { next: "more" } };
  const stub = stubTvdb({ "/v4/series/7/episodes/official": endless });
  const env = { TVDB_API_KEY: "k", METADATA_CACHE: strictKv() };

  const result = await lookupTvdbEpisodeGroups("7", env, stub.fetch);
  assert.ok(stub.calls.length <= MAX_EPISODE_GROUP_PAGES, `${stub.calls.length} upstream pages`);
  assert.deepEqual(result.groups, []);
});

test("an alternate order that would overrun the budget is left out, and the rest is kept", async () => {
  const stub = stubTvdb({
    "/v4/series/7/episodes/official": { data: { episodes: [{ id: 10, seasonNumber: 1, number: 1 }] }, links: { next: null } },
    "/v4/series/7/episodes/dvd": { data: { episodes: [{ id: 10, seasonNumber: 1, number: 1 }] }, links: { next: "more" } },
    "/v4/series/7/episodes/absolute": { data: { episodes: [{ id: 10, seasonNumber: 1, number: 1 }] }, links: { next: null } }
  });
  const env = { TVDB_API_KEY: "k", METADATA_CACHE: strictKv() };

  const result = await lookupTvdbEpisodeGroups("7", env, stub.fetch);
  assert.ok(stub.calls.length <= MAX_EPISODE_GROUP_PAGES);
  assert.equal(result.groups.find((group) => group.type === 3), undefined, "the endless DVD order is dropped");
});
