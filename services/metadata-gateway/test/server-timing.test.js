import assert from "node:assert/strict";
import test from "node:test";
import metadataGateway from "../src/index.js";

const tmdbSearch = { page: 1, total_pages: 1, total_results: 0, results: [] };

function kv({ getDelay = 0, putImpl } = {}) {
  const log = { gets: [], getsStartedBeforeAnyFinished: false, puts: 0 };
  let finished = 0;
  return {
    log,
    async get(key) {
      log.gets.push(key);
      if (finished === 0 && log.gets.length > 1) log.getsStartedBeforeAnyFinished = true;
      await new Promise((r) => setTimeout(r, getDelay));
      finished++;
      return null;
    },
    async put(...args) { log.puts++; return putImpl ? putImpl(...args) : undefined; }
  };
}

async function search(cache, ctx) {
  const realFetch = globalThis.fetch;
  globalThis.fetch = async () => ({ ok: true, status: 200, json: async () => tmdbSearch });
  try {
    return await metadataGateway.fetch(
      new Request("https://gw.example/metadata/search?mediaType=movies&query=Dunkirk", { headers: { "CF-Connecting-IP": "192.0.2.7" } }),
      { TMDB_API_KEY: "k", METADATA_CACHE: cache },
      ctx);
  } finally {
    globalThis.fetch = realFetch;
  }
}

test("the answer returns before a slow cache write finishes", async () => {
  let release;
  const gate = new Promise((r) => { release = r; });
  const cache = kv({ putImpl: () => gate });
  const waiting = [];
  const response = await search(cache, { waitUntil: (p) => waiting.push(p) });
  assert.equal(response.status, 200);
  assert.equal(waiting.length, 1);
  release();
  await Promise.all(waiting);
});

test("a rejecting cache write still answers 200 with the results", async () => {
  const cache = kv({ putImpl: async () => { throw new Error("kv down"); } });
  const waiting = [];
  const response = await search(cache, { waitUntil: (p) => waiting.push(p) });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).provider, "deluno-broker");
  await Promise.all(waiting);
});

test("a search lists each phase in Server-Timing", async () => {
  const response = await search(kv(), { waitUntil() {} });
  const header = response.headers.get("Server-Timing");
  for (const phase of ["cache-read", "limiter", "tmdb", "cache-write"]) {
    assert.match(header, new RegExp(phase + ";dur=[0-9]+(\.[0-9]+)?"));
  }
});

test("the answer and error reads are issued together", async () => {
  const cache = kv({ getDelay: 20 });
  await search(cache, { waitUntil() {} });
  assert.equal(cache.log.getsStartedBeforeAnyFinished, true);
});
