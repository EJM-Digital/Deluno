import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import {
  buildCacheKey,
  lookupMovieReleaseDates,
  lookupEpisodeGroups,
  lookupPopular,
  lookupList,
  listQueryFor,
  parsePopularPage,
  lookupMovieCollection,
  lookupPersonImdbId,
  lookupSeriesCatalogue,
  lookupTmdb,
  mapTmdbResult,
  pickCertification,
  pickContentRating,
  pickTrailerUrl,
  lookupTranslations,
  matchRoute,
  buildPersonImdbResolverUrl,
  parseLookup
} from "../src/index.js";
import metadataGateway from "../src/index.js";

/** Minimal TMDb stand-in: answers by URL path, counts the calls it received. */
function stubTmdb(routes) {
  const calls = [];
  return {
    calls,
    fetch: async (url) => {
      const path = new URL(url).pathname;
      calls.push(path);
      const body = routes[path];
      if (body === undefined) {
        return { ok: false, status: 404, json: async () => ({}) };
      }
      if (typeof body === "number") {
        return { ok: false, status: body, json: async () => ({}) };
      }
      return { ok: true, status: 200, json: async () => body };
    }
  };
}

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
    credits: { cast: [{ id: 6384, name: "Keanu Reeves", character: "Neo", profile_path: "/neo.jpg" }] }
  }, "movies", "https://metadata.deluno.example");

  assert.equal(result.provider, "tmdb");
  assert.equal(result.imdbId, "tt0133093");
  assert.deepEqual(result.genres, ["Action", "Science Fiction"]);
  assert.equal(result.posterUrl, "https://metadata.deluno.example/artwork/w780/poster.jpg");
  assert.equal(result.backdropUrl, "https://metadata.deluno.example/artwork/original/backdrop.jpg");
  assert.deepEqual(result.cast, [{
    personId: "6384",
    name: "Keanu Reeves",
    character: "Neo",
    profileUrl: "https://metadata.deluno.example/artwork/w185/neo.jpg",
    imdbUrl: "https://metadata.deluno.example/person/6384/imdb"
  }]);

  // The key Deluno's MetadataRatingItem actually deserialises. It was "votes"
  // and was therefore dropped in transit, leaving every broker-mode library
  // with scores and no counts behind them.
  assert.equal(result.ratings[0].voteCount, result.voteCount);
  assert.ok(result.ratings[0].voteCount > 0);
});

test("carries runtime, popularity and vote count, which Deluno has had columns for since V0012", () => {
  const detail = mapTmdbResult({
    id: 603,
    title: "The Matrix",
    release_date: "1999-03-30",
    runtime: 136,
    popularity: 71.4,
    vote_count: 25000,
    vote_average: 8.2
  }, "movies");

  assert.equal(detail.runtimeMinutes, 136);
  assert.equal(detail.popularity, 71.4);
  assert.equal(detail.voteCount, 25000);
});

test("a search card has no runtime, and says so rather than inventing one", () => {
  // TMDb returns runtime only on a detail lookup. Null here is the truth; the
  // repository COALESCEs it so a later detail refresh fills it in without an
  // earlier search blanking what it found.
  const card = mapTmdbResult({
    id: 603,
    title: "The Matrix",
    release_date: "1999-03-30",
    popularity: 71.4,
    vote_count: 25000
  }, "movies");

  assert.equal(card.runtimeMinutes, null);
  assert.equal(card.popularity, 71.4);
});

test("carries the fields Deluno's catalogue has declared and never been sent", () => {
  const detail = mapTmdbResult({
    id: 27205,
    title: "Inception",
    release_date: "2010-07-15",
    runtime: 148,
    tagline: "Your mind is the scene of the crime.",
    homepage: "https://www.warnerbros.com/inception",
    original_language: "en",
    status: "Released",
    belongs_to_collection: { id: 645, name: "The Nolan Collection" },
    production_companies: [{ name: "Legendary Pictures" }, { name: "Syncopy" }],
    credits: { crew: [{ job: "Editor", name: "Lee Smith" }, { job: "Director", name: "Christopher Nolan" }] },
    release_dates: { results: [
      { iso_3166_1: "DE", release_dates: [{ certification: "12" }] },
      { iso_3166_1: "US", release_dates: [{ certification: "PG-13" }] }
    ] },
    videos: { results: [{ site: "YouTube", type: "Trailer", official: true, key: "YoHD9XEInc0" }] }
  }, "movies");

  // The library adapters already read certification, collection and language
  // out of the stored metadata blob. Nothing had ever put them there.
  assert.equal(detail.certification, "PG-13");
  assert.equal(detail.collection, "The Nolan Collection");
  assert.equal(detail.collectionProviderId, "645");
  assert.equal(detail.originalLanguage, "en");
  assert.equal(detail.studio, "Legendary Pictures");
  assert.equal(detail.director, "Christopher Nolan");
  assert.equal(detail.status, "Released");
  assert.equal(detail.tagline, "Your mind is the scene of the crime.");
  assert.equal(detail.trailerUrl, "https://www.youtube.com/watch?v=YoHD9XEInc0");
});

test("prefers a certification you recognise, and takes any rather than none", () => {
  // US first, because it is the vocabulary most people read a rating in.
  assert.equal(pickCertification([
    { iso_3166_1: "DE", release_dates: [{ certification: "12" }] },
    { iso_3166_1: "US", release_dates: [{ certification: "R" }] }
  ]), "R");

  // No US entry is not "no rating".
  assert.equal(pickCertification([
    { iso_3166_1: "FR", release_dates: [{ certification: "16" }] }
  ]), "16");

  // A country listed with a blank certification is not a rating.
  assert.equal(pickCertification([
    { iso_3166_1: "US", release_dates: [{ certification: "" }] }
  ]), null);
  assert.equal(pickCertification(undefined), null);
});

test("a show's rating comes from content ratings, and a show says whether it has ended", () => {
  assert.equal(pickContentRating([
    { iso_3166_1: "AU", rating: "MA15+" },
    { iso_3166_1: "US", rating: "TV-MA" }
  ]), "TV-MA");

  const show = mapTmdbResult({
    id: 1396,
    name: "Breaking Bad",
    first_air_date: "2008-01-20",
    status: "Ended",
    networks: [{ name: "AMC" }],
    content_ratings: { results: [{ iso_3166_1: "US", rating: "TV-MA" }] }
  }, "tv");

  // A show that has ended and is missing episodes is a different problem from
  // one that is still airing them.
  assert.equal(show.status, "Ended");
  assert.equal(show.network, "AMC");
  assert.equal(show.certification, "TV-MA");
});

test("links only an official YouTube trailer, and nothing else", () => {
  assert.equal(pickTrailerUrl([{ site: "Vimeo", type: "Trailer", key: "x" }]), null);
  assert.equal(pickTrailerUrl([{ site: "YouTube", type: "Featurette", key: "x" }]), null);
  assert.equal(pickTrailerUrl(undefined), null);
});

test("a show takes its runtime from the episode length", () => {
  const show = mapTmdbResult({
    id: 1396,
    name: "Breaking Bad",
    first_air_date: "2008-01-20",
    episode_run_time: [45, 47]
  }, "tv");

  assert.equal(show.runtimeMinutes, 45);
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
  assert.equal(results[0].posterUrl, "https://image.tmdb.org/t/p/w780/poster.jpg");
  assert.equal(calls.length, 1);
});

test("preserves a confirmed 404 for an exact provider identity", async () => {
  const previousFetch = globalThis.fetch;
  const values = new Map();
  const cache = {
    async get(key, format) {
      const value = values.get(key) ?? null;
      return format === "json" && typeof value === "string" ? JSON.parse(value) : value;
    },
    async put(key, value) { values.set(key, value); }
  };
  globalThis.fetch = async () => ({ ok: false, status: 404, json: async () => ({}) });

  try {
    const response = await metadataGateway.fetch(
      new Request("https://metadata.deluno.test/metadata/search?mediaType=movies&query=Removed&providerId=1603343", {
        headers: { "CF-Connecting-IP": "192.0.2.1" }
      }),
      { TMDB_API_KEY: "test-key", METADATA_CACHE: cache });

    assert.equal(response.status, 404);
    assert.deepEqual(await response.json(), {
      error: "provider_record_missing",
      provider: "tmdb",
      providerId: "1603343"
    });
  } finally {
    globalThis.fetch = previousFetch;
  }
});

test("resolves a TMDb person to IMDb without putting the provider id in the browser", async () => {
  const tmdb = stubTmdb({
    "/3/person/6384/external_ids": { imdb_id: "nm0000206" }
  });

  assert.equal(await lookupPersonImdbId("6384", "secret", tmdb.fetch), "nm0000206");
  assert.deepEqual(tmdb.calls, ["/3/person/6384/external_ids"]);
  assert.equal(buildPersonImdbResolverUrl("6384", "https://metadata.deluno.example"), "https://metadata.deluno.example/person/6384/imdb");
  assert.equal(buildPersonImdbResolverUrl("6384", null), null);
});

test("does not invent an IMDb person id when TMDb has none", async () => {
  const tmdb = stubTmdb({ "/3/person/6384/external_ids": { imdb_id: null } });
  assert.equal(await lookupPersonImdbId("6384", "secret", tmdb.fetch), null);
});

test("routes the catalogue and release-date endpoints, and nothing else", () => {
  assert.deepEqual(matchRoute("/metadata/search"), { kind: "search" });
  assert.deepEqual(matchRoute("/metadata/tv/1396/catalogue"), { kind: "catalogue", id: "1396" });
  assert.deepEqual(matchRoute("/metadata/movie/78/release-dates"), { kind: "release-dates", id: "78" });
  assert.deepEqual(matchRoute("/metadata/movie/645/collection"), { kind: "collection", id: "645" });
  assert.deepEqual(matchRoute("/person/6384/imdb"), { kind: "person-imdb", id: "6384" });
  assert.deepEqual(matchRoute("/metadata/movie/78/translations"), { kind: "translations", mediaKind: "movie", id: "78" });
  assert.deepEqual(matchRoute("/metadata/tv/1396/translations"), { kind: "translations", mediaKind: "tv", id: "1396" });
  assert.equal(matchRoute("/metadata/tv/1396/translations/fr"), null);
  assert.equal(matchRoute("/metadata/tv/abc/catalogue"), null);
  assert.equal(matchRoute("/metadata/tv/1396/catalogue/extra"), null);
  assert.equal(matchRoute("/metadata/anything"), null);
});

test("builds a season/episode catalogue so an episode is known before a file is", async () => {
  const tmdb = stubTmdb({
    "/3/tv/1396": { seasons: [{ season_number: 0 }, { season_number: 1 }, { season_number: 2 }] },
    "/3/tv/1396/season/0": { name: "Specials", air_date: "2009-02-17", episodes: [] },
    "/3/tv/1396/season/1": {
      name: "Season 1",
      air_date: "2008-01-20",
      episodes: [
        { episode_number: 1, name: "Pilot", overview: "  A teacher.  ", air_date: "2008-01-20" },
        { episode_number: 2, name: "  ", overview: null, air_date: null }
      ]
    },
    "/3/tv/1396/season/2": { name: "Season 2", air_date: "2009-03-08", episodes: [{ episode_number: 1, name: "Seven Thirty-Seven", air_date: "2009-03-08" }] }
  });

  const result = await lookupSeriesCatalogue("1396", "key", tmdb.fetch);

  assert.equal(result.seasonCount, 3);
  assert.equal(result.episodeCount, 3);
  const [, first] = result.seasons;
  assert.equal(first.seasonNumber, 1);
  assert.equal(first.episodes[0].title, "Pilot");
  assert.equal(first.episodes[0].overview, "A teacher.");
  assert.equal(first.episodes[0].airDate, "2008-01-20");
  // A blank title and a missing air date become null rather than empty strings.
  assert.equal(first.episodes[1].title, null);
  assert.equal(first.episodes[1].airDate, null);
  // One request per season plus the series itself, done here rather than by the app.
  assert.equal(tmdb.calls.length, 4);
});

test("keeps the rest of a catalogue when one season is unavailable", async () => {
  const tmdb = stubTmdb({
    "/3/tv/9": { seasons: [{ season_number: 1 }, { season_number: 2 }] },
    "/3/tv/9/season/1": { name: "Season 1", episodes: [{ episode_number: 1, name: "One", air_date: "2020-01-01" }] },
    "/3/tv/9/season/2": 404
  });

  const result = await lookupSeriesCatalogue("9", "key", tmdb.fetch);
  assert.equal(result.seasonCount, 1);
  assert.equal(result.seasons[0].episodes[0].title, "One");
});

test("takes the earliest date of each release type and ignores festival premieres", async () => {
  const tmdb = stubTmdb({
    "/3/movie/78/release_dates": {
      results: [
        { iso_3166_1: "US", release_dates: [
          { type: 1, release_date: "1982-08-01T00:00:00.000Z" },
          { type: 3, release_date: "1982-09-09T00:00:00.000Z" },
          { type: 5, release_date: "2017-11-13T00:00:00.000Z" }
        ] },
        { iso_3166_1: "GB", release_dates: [
          { type: 3, release_date: "1982-09-03T00:00:00.000Z" },
          { type: 4, release_date: "2007-12-18T00:00:00.000Z" }
        ] }
      ]
    }
  });

  const result = await lookupMovieReleaseDates("78", "key", tmdb.fetch);

  assert.equal(result.inCinemas, "1982-09-03");
  assert.equal(result.digital, "2007-12-18");
  assert.equal(result.physical, "2017-11-13");
});

test("reports no dates rather than guessing when TMDb has none", async () => {
  const tmdb = stubTmdb({ "/3/movie/1/release_dates": { results: [] } });
  const result = await lookupMovieReleaseDates("1", "key", tmdb.fetch);
  assert.deepEqual([result.inCinemas, result.digital, result.physical], [null, null, null]);
});

test("returns the complete movie membership for a TMDb collection", async () => {
  const tmdb = stubTmdb({
    "/3/collection/645": {
      id: 645,
      name: "The Nolan Collection",
      overview: "Christopher Nolan films.",
      poster_path: "/collection.jpg",
      parts: [
        { id: 27205, title: "Inception", release_date: "2010-07-15", poster_path: "/inception.jpg" },
        { id: 157336, title: "Interstellar", release_date: "2014-11-07", poster_path: null }
      ]
    }
  });

  const result = await lookupMovieCollection("645", "key", tmdb.fetch);

  assert.equal(result.providerId, "645");
  assert.equal(result.name, "The Nolan Collection");
  assert.equal(result.movies.length, 2);
  assert.equal(result.movies[0].year, 2010);
  assert.equal(result.movies[0].posterUrl, "https://image.tmdb.org/t/p/w780/inception.jpg");
  assert.equal(result.movies[1].posterUrl, null);
  assert.deepEqual(tmdb.calls, ["/3/collection/645"]);
});

test("bills the whole cast, and folds each crew member into one credit", () => {
  // Twelve billed players and a crew credited the way TMDb actually credits
  // one: the director also produces, the writer is credited twice, and most of
  // the list is jobs nobody reads.
  const cast = Array.from({ length: 12 }, (_, index) => ({
    id: 901 + index,
    name: `Player ${index + 1}`,
    character: `Role ${index + 1}`,
    profile_path: `/p${index + 1}.jpg`
  }));

  const detail = mapTmdbResult({
    id: 329865,
    title: "Arrival",
    credits: {
      cast,
      crew: [
        { id: 1, job: "Producer", name: "Shawn Levy" },
        { id: 2, job: "Best Boy Electric", name: "Nobody Reads This" },
        { id: 3, job: "Novel", name: "Ted Chiang" },
        { id: 4, job: "Director", name: "Denis Villeneuve", profile_path: "/dv.jpg" },
        { id: 3, job: "Screenplay", name: "Ted Chiang" },
        { id: 4, job: "Producer", name: "Denis Villeneuve", profile_path: "/dv.jpg" }
      ]
    }
  }, "movies", "https://metadata.deluno.example");

  // Ten used to be the cut, so a film with a real ensemble looked like it had
  // ten people in it.
  assert.equal(detail.cast.length, 12);
  assert.equal(detail.cast.at(-1).name, "Player 12");

  // Title-card order, not TMDb's; one row per person; the unrecognised job is
  // gone; and a person credited twice reads as one entry with both jobs.
  assert.deepEqual(detail.crew, [
    { personId: "4", name: "Denis Villeneuve", job: "Director, Producer", profileUrl: "https://metadata.deluno.example/artwork/w185/dv.jpg", imdbUrl: "https://metadata.deluno.example/person/4/imdb" },
    { personId: "3", name: "Ted Chiang", job: "Screenplay, Novel", profileUrl: null, imdbUrl: "https://metadata.deluno.example/person/3/imdb" },
    { personId: "1", name: "Shawn Levy", job: "Producer", profileUrl: null, imdbUrl: "https://metadata.deluno.example/person/1/imdb" }
  ]);

  // The person id is what a credit links to and what following a filmography
  // would key on. A name cannot do either job: names collide.
  assert.equal(detail.cast[0].personId, "901");

  // The single director field the catalogue sorts on is unchanged.
  assert.equal(detail.director, "Denis Villeneuve");
});

test("lists each language's title, overview and tagline once per title, leaving out empty ones", async () => {
  const tmdb = stubTmdb({
    "/3/movie/603/translations": {
      translations: [
        { iso_639_1: "fr", iso_3166_1: "FR", data: { title: "Matrix", overview: "Un pirate.", tagline: "Bienvenue." } },
        { iso_639_1: "pt", iso_3166_1: "BR", data: { title: "Matrix", overview: "", tagline: "" } },
        { iso_639_1: "de", iso_3166_1: "DE", data: { title: "", overview: "  ", tagline: "" } }
      ]
    },
    "/3/tv/1396/translations": {
      translations: [{ iso_639_1: "es", iso_3166_1: "ES", data: { name: "Breaking Bad", overview: "Un profesor." } }]
    }
  });

  const movie = await lookupTranslations("movie", "603", "key", tmdb.fetch);
  assert.deepEqual(movie.translations.map((entry) => entry.language + "-" + entry.region), ["fr-FR", "pt-BR"]);
  assert.equal(movie.translations[0].tagline, "Bienvenue.");
  assert.equal(movie.translations[1].overview, null);

  const show = await lookupTranslations("tv", "1396", "key", tmdb.fetch);
  assert.equal(show.translations[0].title, "Breaking Bad");
});

test("routes episode groups and popular lists", () => {
  assert.deepEqual(matchRoute("/metadata/tv/113962/episode-groups"), { kind: "episode-groups", id: "113962" });
  assert.deepEqual(matchRoute("/metadata/movies/popular"), { kind: "popular", mediaType: "movies" });
  assert.deepEqual(matchRoute("/metadata/movie/popular"), { kind: "popular", mediaType: "movies" });
  assert.deepEqual(matchRoute("/metadata/tv/popular"), { kind: "popular", mediaType: "tv" });
  assert.equal(matchRoute("/metadata/movie/1/episode-groups"), null);
  assert.equal(matchRoute("/metadata/tv/abc/episode-groups"), null);
  assert.equal(matchRoute("/metadata/tv/popular/extra"), null);
});

test("builds DVD and absolute orderings from TMDb episode groups, and names the rest", async () => {
  const tmdb = stubTmdb({
    "/3/tv/1396/episode_groups": {
      results: [
        { id: "dvd-small", name: "DVD small", type: 3, episode_count: 1 },
        { id: "dvd-big", name: "DVD Order", type: 3, episode_count: 3 },
        { id: "abs", name: "Absolute", type: 2, episode_count: 3 },
        { id: "arc", name: "Story arcs", type: 5, episode_count: 3 }
      ]
    },
    "/3/tv/episode_group/dvd-big": {
      groups: [
        { episodes: [{ season_number: 1, episode_number: 2 }, { season_number: 1, episode_number: 1 }] },
        { episodes: [{ season_number: 2, episode_number: 1 }] }
      ]
    },
    "/3/tv/episode_group/abs": {
      groups: [{ episodes: [{ season_number: 1, episode_number: 1 }, { season_number: 2, episode_number: 1 }] }]
    }
  });

  const result = await lookupEpisodeGroups("1396", "key", tmdb.fetch);

  assert.equal(result.groups.length, 3);
  const dvd = result.groups.find((group) => group.type === 3);
  assert.equal(dvd.providerGroupId, "dvd-big");
  assert.deepEqual(dvd.episodes[0], { originalSeasonNumber: 1, originalEpisodeNumber: 2, groupSeasonNumber: 1, groupEpisodeNumber: 1 });
  assert.deepEqual(dvd.episodes[2], { originalSeasonNumber: 2, originalEpisodeNumber: 1, groupSeasonNumber: 2, groupEpisodeNumber: 1 });
  const absolute = result.groups.find((group) => group.type === 2);
  assert.deepEqual(absolute.episodes[1], { originalSeasonNumber: 2, originalEpisodeNumber: 1, groupSeasonNumber: 1, groupEpisodeNumber: 2 });
  const arc = result.groups.find((group) => group.type === 5);
  assert.deepEqual(arc, { type: 5, providerGroupId: "arc", name: "Story arcs", episodes: [] });
  assert.ok(!tmdb.calls.includes("/3/tv/episode_group/dvd-small"), "only the fullest group of a type is fetched");
});

test("a show with no episode groups answers an empty list, and an unavailable group keeps the rest", async () => {
  const none = await lookupEpisodeGroups("5", "key", stubTmdb({ "/3/tv/5/episode_groups": { results: [] } }).fetch);
  assert.deepEqual(none.groups, []);

  // TMDb's own answer for Lioness (113962): 404 on the groups list of a show that exists.
  const tmdbSaysNone = await lookupEpisodeGroups("113962", "key", stubTmdb({ "/3/tv/113962/episode_groups": 404 }).fetch);
  assert.deepEqual(tmdbSaysNone.groups, []);

  const partial = await lookupEpisodeGroups("6", "key", stubTmdb({
    "/3/tv/6/episode_groups": { results: [{ id: "gone", name: "DVD", type: 3, episode_count: 2 }] },
    "/3/tv/episode_group/gone": 404
  }).fetch);
  assert.equal(partial.groups.length, 1);
  assert.deepEqual(partial.groups[0].episodes, []);
});

test("a popular list keeps TMDb's field names and drops entries with no title", async () => {
  const tmdb = stubTmdb({
    "/3/tv/popular": {
      results: [
        { id: 1, name: "Show", original_name: "Show", poster_path: "/p.jpg", first_air_date: "2020-01-01", vote_average: 8.1, popularity: 99.5, junk: "x" },
        { id: 2 }
      ]
    }
  });

  const result = await lookupPopular("tv", 3, "key", tmdb.fetch);

  assert.equal(result.page, 3);
  assert.equal(result.results.length, 1);
  assert.equal(result.results[0].name, "Show");
  assert.equal(result.results[0].poster_path, "/p.jpg");
  assert.equal(result.results[0].junk, undefined);
  assert.equal(parsePopularPage(new URLSearchParams("page=9999")), 500);
  assert.equal(parsePopularPage(new URLSearchParams("page=abc")), 1);
  assert.equal(parsePopularPage(new URLSearchParams("page=0")), 1);
});

function memoryKv() {
  const store = new Map();
  return {
    get: async (key, type) => {
      const value = store.get(key);
      if (value === undefined) return null;
      return type === "json" ? JSON.parse(value) : value;
    },
    put: async (key, value) => { store.set(key, value); }
  };
}

async function viaGateway(path, tmdb, env = {}) {
  const realFetch = globalThis.fetch;
  globalThis.fetch = tmdb.fetch;
  try {
    return await metadataGateway.fetch(new Request(`https://gw.example${path}`), { TMDB_API_KEY: "k", METADATA_CACHE: memoryKv(), ...env });
  } finally {
    globalThis.fetch = realFetch;
  }
}

test("the gateway serves episode groups and popular lists, caches them, and keeps the error codes", async () => {
  const tmdb = stubTmdb({
    "/3/tv/1396/episode_groups": { results: [{ id: "a", name: "Arcs", type: 5 }] },
    "/3/movie/popular": { results: [{ id: 7, title: "Film" }] }
  });
  const kv = memoryKv();

  const groups = await viaGateway("/metadata/tv/1396/episode-groups", tmdb, { METADATA_CACHE: kv });
  assert.equal(groups.status, 200);
  assert.equal((await groups.json()).groups[0].name, "Arcs");
  await viaGateway("/metadata/tv/1396/episode-groups", tmdb, { METADATA_CACHE: kv });
  assert.equal(tmdb.calls.filter((path) => path === "/3/tv/1396/episode_groups").length, 1, "second answer comes from the cache");

  const popular = await viaGateway("/metadata/movies/popular?page=2", tmdb);
  assert.equal(popular.status, 200);
  assert.equal((await popular.json()).results[0].title, "Film");

  // TMDb answers 404 on the groups list both for a show with no groups and for
  // an id it doesn't know; Deluno only asks about shows it already has, so
  // either way the answer is "no other orderings".
  const none = await viaGateway("/metadata/tv/9/episode-groups", tmdb);
  assert.equal(none.status, 200);
  assert.deepEqual((await none.json()).groups, []);
  const busy = await viaGateway("/metadata/tv/popular", stubTmdb({ "/3/tv/popular": 429 }));
  assert.equal(busy.status, 503);
  assert.equal(busy.headers.get("Retry-After"), "60");
  const unavailable = await viaGateway("/metadata/tv/popular", stubTmdb({ "/3/tv/popular": 500 }));
  assert.equal((await unavailable.json()).error, "provider_unavailable");
  const unconfigured = await viaGateway("/metadata/tv/popular", tmdb, { TMDB_API_KEY: "" });
  assert.equal(unconfigured.status, 503);
});

// TMDb's trending, top rated, upcoming, now playing and discover lists answer in the same list shape as its popular list,
// so the recorded popular reply (tests/fixtures/recorded/tmdb) is the upstream body these tests feed the gateway.
const recordedPopular = JSON.parse(
  readFileSync(new URL("../../../tests/fixtures/recorded/tmdb/2026-10-18/popular-movies.json", import.meta.url), "utf8")
).exchanges[0].reply.body;

test("routes the recommendation lists and discover for films and shows (#1429)", () => {
  assert.deepEqual(matchRoute("/metadata/movies/trending"), { kind: "list", list: "trending", mediaType: "movies" });
  assert.deepEqual(matchRoute("/metadata/movie/top-rated"), { kind: "list", list: "top-rated", mediaType: "movies" });
  assert.deepEqual(matchRoute("/metadata/movies/upcoming"), { kind: "list", list: "upcoming", mediaType: "movies" });
  assert.deepEqual(matchRoute("/metadata/tv/now-playing"), { kind: "list", list: "now-playing", mediaType: "tv" });
  assert.deepEqual(matchRoute("/metadata/tv/discover"), { kind: "list", list: "discover", mediaType: "tv" });
  assert.equal(matchRoute("/metadata/tv/upcoming"), null, "TMDb has no upcoming list for shows");
  assert.equal(matchRoute("/metadata/movies/discover/extra"), null);
});

test("a discover query keeps only the filters TMDb lists offer and plain values", () => {
  const query = listQueryFor("discover", new URLSearchParams(
    "with_genres=28,12&vote_average.gte=7.5&primary_release_date.gte=2020-01-01&api_key=steal&include_adult=true&sort_by=popularity.desc&with_keywords=a%20b&region=%26x"));
  assert.equal(query.get("with_genres"), "28,12");
  assert.equal(query.get("vote_average.gte"), "7.5");
  assert.equal(query.get("primary_release_date.gte"), "2020-01-01");
  assert.equal(query.get("sort_by"), "popularity.desc");
  assert.equal(query.get("api_key"), null);
  assert.equal(query.get("include_adult"), null);
  assert.equal(query.get("with_keywords"), null, "a value with a space is dropped");
  assert.equal(query.get("region"), null);
  assert.equal(listQueryFor("trending", new URLSearchParams("window=day")).get("window"), "day");
  assert.equal(listQueryFor("trending", new URLSearchParams("window=year")).get("window"), "week");
  assert.equal([...listQueryFor("top-rated", new URLSearchParams("with_genres=28"))].length, 0, "only discover takes filters");
});

test("each list is read from its own TMDb path, with the filters, and trims like the popular list", async () => {
  const cases = [
    ["movies", "trending", "/3/trending/movie/week"],
    ["movies", "top-rated", "/3/movie/top_rated"],
    ["movies", "upcoming", "/3/movie/upcoming"],
    ["movies", "now-playing", "/3/movie/now_playing"],
    ["tv", "top-rated", "/3/tv/top_rated"],
    ["tv", "now-playing", "/3/tv/on_the_air"],
    ["tv", "trending", "/3/trending/tv/week"],
    ["movies", "discover", "/3/discover/movie"],
    ["tv", "discover", "/3/discover/tv"]
  ];
  for (const [mediaType, list, path] of cases) {
    const tmdb = stubTmdb({ [path]: recordedPopular });
    const result = await lookupList(mediaType, list, 2, listQueryFor(list, new URLSearchParams()), "key", tmdb.fetch);
    assert.deepEqual(tmdb.calls, [path]);
    assert.equal(result.page, 2);
    assert.equal(result.results.length, recordedPopular.results.length);
    assert.equal(result.results[0].id, recordedPopular.results[0].id);
  }

  let asked;
  await lookupList("movies", "discover", 1, listQueryFor("discover", new URLSearchParams("with_genres=28&vote_average.gte=7")), "key", async (url) => {
    asked = new URL(url);
    return { ok: true, status: 200, json: async () => recordedPopular };
  });
  assert.equal(asked.searchParams.get("with_genres"), "28");
  assert.equal(asked.searchParams.get("vote_average.gte"), "7");
  await assert.rejects(() => lookupList("tv", "upcoming", 1, new URLSearchParams(), "key", stubTmdb({}).fetch));
});

test("the gateway serves the lists and discover, caches each ask on its own, and keeps the error codes", async () => {
  const tmdb = stubTmdb({ "/3/trending/movie/week": recordedPopular, "/3/discover/movie": recordedPopular, "/3/trending/movie/day": { results: [{ id: 5, title: "Day" }] } });
  const kv = memoryKv();

  const week = await viaGateway("/metadata/movies/trending", tmdb, { METADATA_CACHE: kv });
  assert.equal(week.status, 200);
  assert.equal((await week.json()).results[0].id, recordedPopular.results[0].id);
  await viaGateway("/metadata/movies/trending", tmdb, { METADATA_CACHE: kv });
  assert.equal(tmdb.calls.filter((path) => path === "/3/trending/movie/week").length, 1, "the second ask comes from the cache");
  const day = await viaGateway("/metadata/movies/trending?window=day", tmdb, { METADATA_CACHE: kv });
  assert.equal((await day.json()).results[0].title, "Day", "another window is another cache entry");

  await viaGateway("/metadata/movies/discover?with_genres=28", tmdb, { METADATA_CACHE: kv });
  await viaGateway("/metadata/movies/discover?with_genres=35", tmdb, { METADATA_CACHE: kv });
  assert.equal(tmdb.calls.filter((path) => path === "/3/discover/movie").length, 2, "different filters are not shared");

  assert.equal((await viaGateway("/metadata/tv/top-rated", stubTmdb({ "/3/tv/top_rated": 429 }))).status, 503);
  const gone = await viaGateway("/metadata/tv/top-rated", stubTmdb({ "/3/tv/top_rated": 404 }));
  assert.equal(gone.status, 404);
  const down = await viaGateway("/metadata/movies/upcoming", stubTmdb({ "/3/movie/upcoming": 500 }));
  assert.equal((await down.json()).error, "provider_unavailable");
});
