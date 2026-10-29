import assert from "node:assert/strict";
import test from "node:test";
import gateway, { lookupTmdb, mapTmdbResult } from "../src/index.js";

// What the gateway keeps of a TMDb record (#1329): the result cap, the identity fields, the people lists and the keyword
// list. Each test hands the mapping a TMDb-shaped record and reads back the broker contract.

const ORIGIN = "https://gateway.example";

test("at most 6 search results are returned and items with no id or no title are dropped", async () => {
  const results = [
    { id: 0, title: "No id" },
    { id: 1 },
    ...Array.from({ length: 9 }, (_, index) => ({ id: index + 10, title: `Film ${index}` }))
  ];
  const request = async () => ({ ok: true, status: 200, json: async () => ({ results }) });

  const found = await lookupTmdb({ mediaType: "movies", query: "Film", year: null, providerId: null }, "key", request);

  assert.equal(found.length, 6);
  assert.deepEqual(found.map((item) => item.providerId), ["10", "11", "12", "13", "14", "15"]);
});

test("a result carries provider tmdb, its id, media type, title, original title and the year of its release date", () => {
  const result = mapTmdbResult(
    { id: 603, title: "The Matrix", original_title: "Matrix (original)", release_date: "1999-03-30" },
    "movies");

  assert.equal(result.provider, "tmdb");
  assert.equal(result.providerId, "603");
  assert.equal(result.mediaType, "movies");
  assert.equal(result.title, "The Matrix");
  assert.equal(result.originalTitle, "Matrix (original)");
  assert.equal(result.year, 1999);
  assert.equal(result.externalUrl, "https://www.themoviedb.org/movie/603");
  assert.equal(mapTmdbResult({ id: 5, title: "Undated", release_date: "" }, "movies").year, null);
});

test("a TV result takes its title from name and its year from first_air_date", () => {
  const result = mapTmdbResult(
    { id: 1396, name: "Breaking Bad", original_name: "Breaking Bad (orig)", first_air_date: "2008-01-20", release_date: "1999-01-01" },
    "tv");

  assert.equal(result.title, "Breaking Bad");
  assert.equal(result.originalTitle, "Breaking Bad (orig)");
  assert.equal(result.year, 2008);
  assert.equal(result.externalUrl, "https://www.themoviedb.org/tv/1396");
});

test("no rating means ratings is an empty list, and a rating gives one TMDb entry out of 10 with its votes", () => {
  const none = mapTmdbResult({ id: 1, title: "Unrated" }, "movies");
  const rated = mapTmdbResult({ id: 2, title: "Rated", vote_average: 7.4, vote_count: 1200 }, "movies");

  assert.equal(none.rating, null);
  assert.deepEqual(none.ratings, []);
  assert.deepEqual(rated.ratings, [{
    source: "tmdb", label: "TMDb", score: 7.4, maxScore: 10, voteCount: 1200,
    url: "https://www.themoviedb.org/movie/2", kind: "community"
  }]);
});

test("keywords are read from keywords.keywords for a film or keywords.results for a show, trimmed and capped at 25", () => {
  const names = Array.from({ length: 30 }, (_, index) => ({ name: `  word ${index}  ` }));

  const film = mapTmdbResult({ id: 1, title: "Film", keywords: { keywords: names } }, "movies");
  const show = mapTmdbResult({ id: 2, name: "Show", keywords: { results: [{ name: " time loop " }, { name: "" }, {}] } }, "tv");

  assert.equal(film.keywords.length, 25);
  assert.equal(film.keywords[0], "word 0");
  assert.deepEqual(show.keywords, ["time loop"]);
  assert.deepEqual(mapTmdbResult({ id: 3, title: "None" }, "movies").keywords, []);
});

test("cast is at most 30 billed players with person id, character, a w185 portrait and a gateway person resolver link", () => {
  const cast = Array.from({ length: 35 }, (_, index) => ({
    id: 100 + index, name: `Player ${index}`, character: index === 0 ? " Neo " : "", profile_path: `/p${index}.jpg`
  }));
  cast.splice(1, 0, { id: 999, name: "   " });

  const result = mapTmdbResult({ id: 1, title: "Film", credits: { cast } }, "movies", ORIGIN);

  assert.equal(result.cast.length, 30);
  assert.deepEqual(result.cast[0], {
    personId: "100", name: "Player 0", character: "Neo",
    profileUrl: `${ORIGIN}/artwork/w185/p0.jpg`, imdbUrl: `${ORIGIN}/person/100/imdb`
  });
  assert.equal(result.cast[1].name, "Player 1", "a billed player with a blank name is skipped");
  assert.equal(result.cast[1].character, null);
});

test("crew keeps only recognised jobs in title-card order, one entry per person with joined jobs, capped at 20", () => {
  const crew = [
    { id: 1, name: "Gaffer Grip", job: "Gaffer" },
    { id: 2, name: "Denis V", job: "Producer" },
    { id: 2, name: "Denis V", job: "Director" },
    { id: 3, name: "Eric R", job: "Screenplay" },
    { id: 3, name: "Eric R", job: "Novel" },
    ...Array.from({ length: 25 }, (_, index) => ({ id: 500 + index, name: `Producer ${index}`, job: "Executive Producer" }))
  ];

  const result = mapTmdbResult({ id: 1, title: "Film", credits: { crew } }, "movies", ORIGIN);

  assert.equal(result.crew.length, 20);
  assert.deepEqual(result.crew.slice(0, 3).map((person) => [person.name, person.job]), [
    ["Denis V", "Director, Producer"],
    ["Eric R", "Screenplay, Novel"],
    ["Producer 0", "Executive Producer"]
  ]);
  assert.equal(result.crew.some((person) => person.name === "Gaffer Grip"), false);
  assert.equal(result.director, "Denis V");
  assert.equal(
    mapTmdbResult({ id: 1, title: "Film", credits: { crew: [{ id: 9, name: "Boss", job: "Director" }] } }, "movies").director,
    "Boss");
});

test("the catalogue is served over HTTP under catalogue:v1:{id} and cached for 6 hours", async () => {
  const store = new Map();
  const puts = [];
  const cache = {
    get: async (key, type) => (store.has(key) ? JSON.parse(store.get(key)) : null),
    put: async (key, value, options) => { puts.push({ key, options }); store.set(key, value); }
  };
  const bodies = {
    "/3/tv/55": { id: 55, name: "Show", number_of_seasons: 1, seasons: [{ season_number: 1 }], external_ids: {} },
    "/3/tv/55/season/1": { episodes: [{ episode_number: 1, name: "Pilot", air_date: "2020-01-01" }] }
  };
  const realFetch = globalThis.fetch;
  globalThis.fetch = async (url) => {
    const body = bodies[new URL(url).pathname];
    return body ? { ok: true, status: 200, json: async () => body } : { ok: false, status: 404, json: async () => ({}) };
  };
  try {
    const response = await gateway.fetch(new Request(`${ORIGIN}/metadata/tv/55/catalogue`), { TMDB_API_KEY: "key", METADATA_CACHE: cache });

    assert.equal(response.status, 200);
    assert.equal(response.headers.get("Cache-Control"), "public, max-age=600");
    assert.equal(puts.find((put) => put.key === "catalogue:v1:55").options.expirationTtl, 60 * 60 * 6);
  } finally {
    globalThis.fetch = realFetch;
  }
});
