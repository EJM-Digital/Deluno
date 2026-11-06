import assert from "node:assert/strict";
import test from "node:test";
import { installLocalCache } from "./local-cache-stub.js";
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
  installLocalCache({ putImpl: () => gate });
  try {
    const waiting = [];
    const response = await search(kv(), { waitUntil: (p) => waiting.push(p) });
    assert.equal(response.status, 200);
    assert.equal(waiting.length, 1);
    release();
    await Promise.all(waiting);
  } finally { delete globalThis.caches; }
});

test("a rejecting cache write still answers 200 with the results", async () => {
  installLocalCache({ putImpl: async () => { throw new Error("cache down"); } });
  try {
    const waiting = [];
    const response = await search(kv(), { waitUntil: (p) => waiting.push(p) });
    assert.equal(response.status, 200);
    assert.equal((await response.json()).provider, "deluno-broker");
    await Promise.all(waiting);
  } finally { delete globalThis.caches; }
});

test("a search lists each phase in Server-Timing and has no KV read", async () => {
  const response = await search(kv(), { waitUntil() {} });
  const header = response.headers.get("Server-Timing");
  for (const phase of ["cache-local", "limiter", "tmdb", "cache-write"]) {
    assert.match(header, new RegExp(phase + ";dur=[0-9]+(\.[0-9]+)?"));
  }
  assert.doesNotMatch(header, /cache-read/);
});

test("a miss goes straight to TMDb without touching KV, then fills the local cache inside waitUntil", async () => {
  const { log } = installLocalCache();
  try {
    const cache = kv();
    const waiting = [];
    const response = await search(cache, { waitUntil: (p) => waiting.push(p) });
    assert.equal(response.status, 200);
    assert.match(response.headers.get("Server-Timing"), /cache-local;dur=/);
    assert.equal(cache.log.gets.length, 0);
    assert.equal(cache.log.puts, 0);
    await Promise.all(waiting);
    assert.deepEqual(log.puts, ["public, max-age=43200"]);
  } finally { delete globalThis.caches; }
});

test("a local hit answers without touching KV or TMDb", async () => {
  installLocalCache();
  try {
    await Promise.all((() => { const w = []; return [search(kv(), { waitUntil: (p) => w.push(p) }).then(() => Promise.all(w))]; })());
    const cache = kv();
    const realFetch = globalThis.fetch;
    globalThis.fetch = async () => { throw new Error("TMDb must not be called"); };
    let response;
    try { response = await metadataGateway.fetch(
      new Request("https://gw.example/metadata/search?mediaType=movies&query=Dunkirk", { headers: { "CF-Connecting-IP": "192.0.2.7" } }),
      { TMDB_API_KEY: "k", METADATA_CACHE: cache }, { waitUntil() {} }); }
    finally { globalThis.fetch = realFetch; }
    assert.equal(response.status, 200);
    assert.equal(cache.log.gets.length, 0);
    assert.doesNotMatch(response.headers.get("Server-Timing"), /cache-read/);
  } finally { delete globalThis.caches; }
});
