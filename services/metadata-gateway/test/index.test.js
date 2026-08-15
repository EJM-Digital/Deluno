import assert from "node:assert/strict";
import test from "node:test";
import {
  buildCacheKey,
  enforceRateLimit,
  lookupTmdb,
  mapTmdbResult,
  parseLookup
} from "../src/index.js";

test("validates the public lookup contract", () => {
  assert.equal(parseLookup(new URLSearchParams("mediaType=movies&query=The+Matrix&year=1999")).value.query, "The Matrix");
  assert.match(parseLookup(new URLSearchParams("mediaType=music&query=The+Matrix")).error, /mediaType/);
  assert.match(parseLookup(new URLSearchParams("mediaType=tv&query=")).error, /query/);
  assert.match(parseLookup(new URLSearchParams("mediaType=tv&query=Lost&providerId=abc")).error, /providerId/);
});

test("uses a stable cache key without leaking the TMDb credential", () => {
  const key = buildCacheKey({ mediaType: "movies", query: "THE Matrix", year: 1999, providerId: null });
  assert.match(key, /^search:/);
  assert.equal(key.includes("api_key"), false);
});

test("maps a TMDb detail response into Deluno's broker contract with gateway-cached artwork", () => {
  const result = mapTmdbResult({
    id: 603,
    title: "The Matrix",
    original_title: "The Matrix",
    release_date: "1999-03-30",
    overview: "A hacker learns the truth.",
    poster_path: "/poster.jpg",
    backdrop_path: "/backdrop.jpg",
    vote_average: 8.2,
    vote_count: 25000,
    genres: [{ name: "Action" }, { name: "Science Fiction" }],
    external_ids: { imdb_id: "tt0133093" },
    credits: { cast: [{ name: "Keanu Reeves", character: "Neo", profile_path: "/neo.jpg" }] }
  }, "movies", "https://metadata.deluno.example");

  assert.equal(result.provider, "tmdb");
  assert.equal(result.imdbId, "tt0133093");
  assert.deepEqual(result.genres, ["Action", "Science Fiction"]);
  assert.equal(result.posterUrl, "https://metadata.deluno.example/artwork/w500/poster.jpg");
  assert.equal(result.backdropUrl, "https://metadata.deluno.example/artwork/w1280/backdrop.jpg");
  assert.deepEqual(result.cast, [{ name: "Keanu Reeves", character: "Neo", profileUrl: "https://metadata.deluno.example/artwork/w185/neo.jpg" }]);
});

test("rate-limits a client after the configured request window", async () => {
  const values = new Map();
  const cache = {
    get: async (key) => values.get(key) ?? null,
    put: async (key, value) => values.set(key, value)
  };
  const now = Date.UTC(2026, 7, 14, 0, 0, 0);
  for (let index = 0; index < 30; index += 1) {
    assert.equal((await enforceRateLimit(cache, "203.0.113.1", now)).allowed, true);
  }
  assert.equal((await enforceRateLimit(cache, "203.0.113.1", now)).allowed, false);
});

test("searches TMDb once and returns card-ready results without a detail fan-out", async () => {
  const calls = [];
  const response = (body) => new Response(JSON.stringify(body), { status: 200 });
  const mockFetch = async (url) => {
    calls.push(String(url));
    if (String(url).includes("/search/movie")) {
      return response({ results: [{ id: 603, title: "The Matrix", release_date: "1999-03-30", poster_path: "/poster.jpg" }] });
    }
    return response({ id: 603, title: "The Matrix", release_date: "1999-03-30", genres: [], external_ids: {} });
  };

  const results = await lookupTmdb({ mediaType: "movies", query: "The Matrix", providerId: null, year: 1999 }, "secret", mockFetch);
  assert.equal(results.length, 1);
  assert.equal(results[0].providerId, "603");
  assert.equal(results[0].posterUrl, "https://image.tmdb.org/t/p/w500/poster.jpg");
  assert.equal(calls.length, 1);
});
