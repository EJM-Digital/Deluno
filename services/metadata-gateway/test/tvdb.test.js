import assert from "node:assert/strict";
import test from "node:test";
import gateway from "../src/index.js";
import {
  TVDB_ATTRIBUTION,
  TVDB_CATALOGUE_TTL_SECONDS,
  TVDB_SEARCH_TTL_SECONDS,
  TVDB_ORDERS_TTL_SECONDS,
  getTvdbToken,
  lookupTvdbCatalogue,
  lookupTvdbEpisodeGroups,
  lookupTvdbSeries,
  lookupTvdbTranslation,
  mapTvdbSeries,
  matchTvdbRoute,
  parseTvdbSearch,
  searchTvdb
} from "../src/tvdb.js";

/** In-memory stand-in for the Worker's KV binding. */
function memoryKv() {
  const store = new Map();
  const puts = [];
  return {
    store,
    puts,
    get: async (key, type) => {
      const value = store.get(key);
      if (value === undefined) return null;
      return type === "json" ? JSON.parse(value) : value;
    },
    put: async (key, value, options) => {
      puts.push({ key, options });
      store.set(key, value);
    }
  };
}

/** Recorded-shape TheTVDB v4 stand-in: answers by path, counts calls, records bodies. */
function stubTvdb(routes) {
  const calls = [];
  const logins = [];
  return {
    calls,
    logins,
    fetch: async (url, init = {}) => {
      const parsed = new URL(url);
      if (parsed.pathname === "/v4/login") {
        logins.push(JSON.parse(init.body));
        return { ok: true, status: 200, json: async () => ({ data: { token: "tok-1" }, status: "success" }) };
      }
      const key = `${parsed.pathname}${parsed.searchParams.has("page") ? `?page=${parsed.searchParams.get("page")}` : ""}`;
      calls.push({ path: parsed.pathname, auth: init.headers?.Authorization, search: parsed.search });
      const body = routes[key] ?? routes[parsed.pathname];
      if (body === undefined) return { ok: false, status: 404, json: async () => ({}) };
      if (typeof body === "number") return { ok: false, status: body, json: async () => ({}) };
      return { ok: true, status: 200, json: async () => body };
    }
  };
}

const env = () => ({ TVDB_API_KEY: "placeholder-key", METADATA_CACHE: memoryKv() });

const SEARCH_FIXTURE = {
  status: "success",
  data: [
    {
      tvdb_id: "81189",
      name: "Breaking Bad",
      year: "2008",
      overview: "A chemistry teacher turns to crime.",
      image_url: "https://artworks.thetvdb.com/banners/posters/81189-10.jpg",
      genres: ["Crime", "Drama"],
      network: "AMC",
      status: "Ended",
      primary_language: "eng",
      remote_ids: [
        { id: "tt0903747", type: 2, sourceName: "IMDB" },
        { id: "1396", type: 12, sourceName: "TheMovieDB.com" }
      ],
      aliases: ["BB"]
    },
    { tvdb_id: "999", name: "No Tmdb Show", year: "2020", remote_ids: [{ id: "movie/55", type: 12, sourceName: "TheMovieDB.com" }] }
  ]
};

test("routes only the documented TVDB endpoints", () => {
  assert.deepEqual(matchTvdbRoute("/metadata/tvdb/search"), { kind: "tvdb-search" });
  assert.deepEqual(matchTvdbRoute("/metadata/tvdb/tv/81189"), { kind: "tvdb-series", id: "81189" });
  assert.deepEqual(matchTvdbRoute("/metadata/tvdb/tv/81189/catalogue"), { kind: "tvdb-catalogue", id: "81189" });
  assert.deepEqual(matchTvdbRoute("/metadata/tvdb/tv/81189/episode-groups"), { kind: "tvdb-episode-groups", id: "81189" });
  assert.deepEqual(matchTvdbRoute("/metadata/tvdb/remote/tt0903747"), { kind: "tvdb-remote", id: "tt0903747" });
  assert.equal(matchTvdbRoute("/metadata/tvdb/tv/abc"), null);
  assert.equal(matchTvdbRoute("/metadata/tvdb/anything"), null);
});

test("validates a TVDB search lookup", () => {
  assert.equal(parseTvdbSearch(new URLSearchParams("query=Lost&year=2004")).value.year, 2004);
  assert.match(parseTvdbSearch(new URLSearchParams("query=")).error, /query/);
  assert.match(parseTvdbSearch(new URLSearchParams("query=Lost&year=99")).error, /year/);
});

test("logs in with the key alone, sends the PIN only when one is set, and reuses the token", async () => {
  const stub = stubTvdb({});
  const plain = env();
  assert.equal(await getTvdbToken(plain, stub.fetch), "tok-1");
  assert.equal(await getTvdbToken(plain, stub.fetch), "tok-1");
  assert.equal(stub.logins.length, 1, "the cached token is reused");
  assert.deepEqual(stub.logins[0], { apikey: "placeholder-key" });

  const withPin = { ...env(), TVDB_PIN: "1234ABCD" };
  await getTvdbToken(withPin, stub.fetch);
  assert.deepEqual(stub.logins[1], { apikey: "placeholder-key", pin: "1234ABCD" });
});

test("search maps TVDB hits to the contract, with the TMDb id taken from remote ids", async () => {
  const stub = stubTvdb({ "/v4/search": SEARCH_FIXTURE });
  const result = await searchTvdb({ query: "Breaking Bad", year: 2008 }, env(), stub.fetch);

  assert.equal(result.provider, "tvdb");
  assert.deepEqual(result.attribution, TVDB_ATTRIBUTION);
  const first = result.results[0];
  assert.equal(first.tvdbId, "81189");
  assert.equal(first.tmdbId, "1396");
  assert.equal(first.imdbId, "tt0903747");
  assert.equal(first.year, 2008);
  assert.equal(first.network, "AMC");
  assert.deepEqual(first.genres, ["Crime", "Drama"]);
  assert.equal(first.posterUrl, "https://artworks.thetvdb.com/banners/posters/81189-10.jpg");
  // A "movie/..." TMDb id is a film, never a series identity.
  assert.equal(result.results[1].tmdbId, null);
  assert.match(stub.calls[0].search, /type=series/);
  assert.equal(stub.calls[0].auth, "Bearer tok-1");
});

test("series detail prefers the English translation and picks the best artwork of each kind", async () => {
  const stub = stubTvdb({
    "/v4/series/81189/extended": {
      data: {
        id: 81189,
        name: "Breaking Bad (original)",
        overview: "Original overview",
        image: "https://artworks.thetvdb.com/banners/posters/81189-10.jpg",
        year: "2008",
        averageRuntime: 47,
        originalLanguage: "eng",
        status: { name: "Ended" },
        genres: [{ name: "Drama" }],
        originalNetwork: { name: "AMC" },
        remoteIds: [{ id: "tt0903747", sourceName: "IMDB" }, { id: "tv/1396", sourceName: "TheMovieDB.com" }],
        contentRatings: [{ name: "TV-MA", country: "usa" }],
        artworks: [
          { type: 3, image: "/banners/fanart/low.jpg", score: 1 },
          { type: 3, image: "/banners/fanart/high.jpg", score: 9 },
          { type: 1, image: "/banners/graphical/banner.jpg", score: 3 },
          { type: 23, image: "/banners/clearlogo/logo.png", score: 3 }
        ]
      }
    },
    "/v4/series/81189/translations/eng": { data: { name: "Breaking Bad", overview: "A teacher turns to crime.", aliases: ["BrBa"] } }
  });

  const { series, attribution } = await lookupTvdbSeries("81189", env(), stub.fetch);
  assert.equal(series.title, "Breaking Bad");
  assert.equal(series.originalTitle, "Breaking Bad (original)");
  assert.equal(series.overview, "A teacher turns to crime.");
  assert.equal(series.tmdbId, "1396");
  assert.equal(series.runtimeMinutes, 47);
  assert.equal(series.status, "Ended");
  assert.equal(series.network, "AMC");
  assert.equal(series.certification, "TV-MA");
  assert.equal(series.backdropUrl, "https://artworks.thetvdb.com/banners/fanart/high.jpg");
  assert.equal(series.bannerUrl, "https://artworks.thetvdb.com/banners/graphical/banner.jpg");
  assert.equal(series.logoUrl, "https://artworks.thetvdb.com/banners/clearlogo/logo.png");
  assert.ok(series.aliases.includes("BrBa"));
  assert.deepEqual(attribution, TVDB_ATTRIBUTION);
});

test("a series with no English translation keeps its base name", async () => {
  const stub = stubTvdb({
    "/v4/series/1/extended": { data: { id: 1, name: "Base Name", year: "2001" } }
  });
  const { series } = await lookupTvdbSeries("1", env(), stub.fetch);
  assert.equal(series.title, "Base Name");
});

test("the catalogue is the aired order with TVDB's absolute numbers, across pages", async () => {
  const stub = stubTvdb({
    "/v4/series/7/episodes/official/eng?page=0": {
      data: { episodes: [
        { id: 1, seasonNumber: 1, number: 1, absoluteNumber: 1, name: "Pilot", aired: "2010-04-03", overview: "Start" },
        { id: 2, seasonNumber: 1, number: 2, absoluteNumber: 2, name: "Two", aired: "2010-04-10" }
      ] },
      links: { next: 1 }
    },
    "/v4/series/7/episodes/official/eng?page=1": {
      data: { episodes: [
        { id: 3, seasonNumber: 2, number: 1, absoluteNumber: 3, name: "Return", aired: "2011-01-01" },
        { id: 4, seasonNumber: 0, number: 1, name: "Special", aired: "2010-01-01" }
      ] },
      links: { next: null }
    }
  });

  const catalogue = await lookupTvdbCatalogue("7", env(), stub.fetch);
  assert.equal(catalogue.provider, "tvdb");
  assert.deepEqual(catalogue.seasons.map((season) => season.seasonNumber), [0, 1, 2]);
  assert.equal(catalogue.episodeCount, 4);
  const season1 = catalogue.seasons[1];
  assert.equal(season1.episodes[0].title, "Pilot");
  assert.equal(season1.episodes[0].absoluteNumber, 1);
  assert.equal(season1.episodes[1].overview, null, "a blank field is null so the app can fall back to TMDb");
  assert.equal(catalogue.seasons[0].episodes[0].absoluteNumber, null);
  assert.equal(stub.calls.length, 2);
});

test("falls back to untranslated episodes when TVDB has no English list", async () => {
  const stub = stubTvdb({
    "/v4/series/7/episodes/official": {
      data: { episodes: [{ id: 1, seasonNumber: 1, number: 1, name: "Pilot" }] },
      links: { next: null }
    }
  });
  const catalogue = await lookupTvdbCatalogue("7", env(), stub.fetch);
  assert.equal(catalogue.episodeCount, 1);
});

test("alternate orders are keyed back to the aired episode by TVDB's episode id", async () => {
  const stub = stubTvdb({
    "/v4/series/7/episodes/official": {
      data: { episodes: [
        { id: 10, seasonNumber: 1, number: 1 },
        { id: 11, seasonNumber: 1, number: 2 },
        { id: 12, seasonNumber: 2, number: 1 }
      ] },
      links: { next: null }
    },
    "/v4/series/7/episodes/dvd": {
      data: { episodes: [{ id: 11, seasonNumber: 1, number: 1 }, { id: 10, seasonNumber: 1, number: 2 }] },
      links: { next: null }
    },
    "/v4/series/7/episodes/absolute": {
      data: { episodes: [{ id: 10, seasonNumber: 1, number: 1 }, { id: 11, seasonNumber: 1, number: 2 }, { id: 12, seasonNumber: 1, number: 3 }] },
      links: { next: null }
    }
  });

  const { groups } = await lookupTvdbEpisodeGroups("7", env(), stub.fetch);
  const dvd = groups.find((group) => group.type === 3);
  const absolute = groups.find((group) => group.type === 2);
  assert.deepEqual(dvd.episodes[0], { originalSeasonNumber: 1, originalEpisodeNumber: 2, groupSeasonNumber: 1, groupEpisodeNumber: 1 });
  assert.equal(absolute.episodes.length, 3);
  assert.deepEqual(absolute.episodes[2], { originalSeasonNumber: 2, originalEpisodeNumber: 1, groupSeasonNumber: 1, groupEpisodeNumber: 3 });
});

test("a show TVDB has no DVD order for simply lists no DVD group", async () => {
  const stub = stubTvdb({
    "/v4/series/7/episodes/official": { data: { episodes: [{ id: 10, seasonNumber: 1, number: 1 }] }, links: { next: null } }
  });
  const { groups } = await lookupTvdbEpisodeGroups("7", env(), stub.fetch);
  assert.deepEqual(groups, []);
});

test("mapTvdbSeries refuses a record with no id or no title", () => {
  assert.equal(mapTvdbSeries({ name: "x" }), null);
  assert.equal(mapTvdbSeries({ tvdb_id: "1" }), null);
});

test("the gateway serves a TVDB search, caches it with the documented TTL, and never leaks the key", async () => {
  const stub = stubTvdb({ "/v4/search": SEARCH_FIXTURE });
  const realFetch = globalThis.fetch;
  globalThis.fetch = stub.fetch;
  try {
    const bindings = env();
    const request = () => new Request("https://gw.example/metadata/tvdb/search?query=Breaking+Bad&year=2008", {
      headers: { "CF-Connecting-IP": "203.0.113.9" }
    });

    const first = await gateway.fetch(request(), bindings);
    assert.equal(first.status, 200);
    const body = await first.json();
    assert.equal(body.results[0].tvdbId, "81189");
    assert.equal(JSON.stringify(body).includes("placeholder-key"), false);
    assert.equal(bindings.METADATA_CACHE.puts.some((put) => put.options?.expirationTtl === TVDB_SEARCH_TTL_SECONDS), true);

    const callsAfterFirst = stub.calls.length;
    const second = await gateway.fetch(request(), bindings);
    assert.equal(second.status, 200);
    assert.equal(stub.calls.length, callsAfterFirst, "the second identical search is served from the cache");
  } finally {
    globalThis.fetch = realFetch;
  }
});

test("without a TVDB key the gateway says so instead of failing obscurely", async () => {
  const response = await gateway.fetch(
    new Request("https://gw.example/metadata/tvdb/tv/81189/catalogue"),
    { METADATA_CACHE: memoryKv() });
  assert.equal(response.status, 503);
  assert.equal((await response.json()).error, "service_unavailable");
});

test("a TVDB 404 is a plain not_found, and a busy TVDB is a retryable 503", async () => {
  const realFetch = globalThis.fetch;
  try {
    globalThis.fetch = stubTvdb({ "/v4/series/5/extended": 404 }).fetch;
    const missing = await gateway.fetch(new Request("https://gw.example/metadata/tvdb/tv/5"), env());
    assert.equal(missing.status, 404);

    globalThis.fetch = stubTvdb({ "/v4/series/6/extended": 429 }).fetch;
    const busy = await gateway.fetch(new Request("https://gw.example/metadata/tvdb/tv/6"), env());
    assert.equal(busy.status, 503);
    assert.equal(busy.headers.get("Retry-After"), "60");
  } finally {
    globalThis.fetch = realFetch;
  }
});

test("catalogue and order answers use their own cache lifetimes", () => {
  assert.equal(TVDB_CATALOGUE_TTL_SECONDS, 60 * 60 * 6);
  assert.equal(TVDB_ORDERS_TTL_SECONDS, 60 * 60 * 24);
});

test("routes and serves one series' translation, and says not found when there is none", async () => {
  assert.deepEqual(matchTvdbRoute("/metadata/tvdb/tv/81100/translations/fra"), { kind: "tvdb-translation", id: "81100", language: "fra" });
  assert.equal(matchTvdbRoute("/metadata/tvdb/tv/81100/translations/fr"), null);

  const tvdb = stubTvdb({ "/v4/series/81100/translations/fra": { data: { name: "Une série", overview: "Résumé." } } });
  const env = { TVDB_API_KEY: "k", METADATA_CACHE: memoryKv() };
  const found = await lookupTvdbTranslation("81100", "fra", env, tvdb.fetch);
  assert.equal(found.title, "Une série");
  assert.equal(found.overview, "Résumé.");
  assert.equal(found.attribution, TVDB_ATTRIBUTION);

  await assert.rejects(() => lookupTvdbTranslation("81100", "deu", env, tvdb.fetch), (error) => error.status === 404);
});
