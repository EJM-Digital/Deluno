import assert from "node:assert/strict";
import test from "node:test";
import {
  buildCacheKey,
  enforceRateLimit,
  lookupMovieReleaseDates,
  lookupSeriesCatalogue,
  lookupTmdb,
  mapTmdbResult,
  pickCertification,
  pickContentRating,
  pickTrailerUrl,
  matchRoute,
  parseLookup
} from "../src/index.js";

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
    credits: { cast: [{ name: "Keanu Reeves", character: "Neo", profile_path: "/neo.jpg" }] }
  }, "movies", "https://metadata.deluno.example");

  assert.equal(result.provider, "tmdb");
  assert.equal(result.imdbId, "tt0133093");
  assert.deepEqual(result.genres, ["Action", "Science Fiction"]);
  assert.equal(result.posterUrl, "https://metadata.deluno.example/artwork/w780/poster.jpg");
  assert.equal(result.backdropUrl, "https://metadata.deluno.example/artwork/original/backdrop.jpg");
  assert.deepEqual(result.cast, [{ name: "Keanu Reeves", character: "Neo", profileUrl: "https://metadata.deluno.example/artwork/w185/neo.jpg" }]);

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
    belongs_to_collection: { name: "The Nolan Collection" },
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
  assert.equal(results[0].posterUrl, "https://image.tmdb.org/t/p/w780/poster.jpg");
  assert.equal(calls.length, 1);
});

test("routes the catalogue and release-date endpoints, and nothing else", () => {
  assert.deepEqual(matchRoute("/metadata/search"), { kind: "search" });
  assert.deepEqual(matchRoute("/metadata/tv/1396/catalogue"), { kind: "catalogue", id: "1396" });
  assert.deepEqual(matchRoute("/metadata/movie/78/release-dates"), { kind: "release-dates", id: "78" });
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

test("bills the whole cast, and folds each crew member into one credit", () => {
  // Twelve billed players and a crew credited the way TMDb actually credits
  // one: the director also produces, the writer is credited twice, and most of
  // the list is jobs nobody reads.
  const cast = Array.from({ length: 12 }, (_, index) => ({
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
    { name: "Denis Villeneuve", job: "Director, Producer", profileUrl: "https://metadata.deluno.example/artwork/w185/dv.jpg" },
    { name: "Ted Chiang", job: "Screenplay, Novel", profileUrl: null },
    { name: "Shawn Levy", job: "Producer", profileUrl: null }
  ]);

  // The single director field the catalogue sorts on is unchanged.
  assert.equal(detail.director, "Denis Villeneuve");
});
