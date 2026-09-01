const CACHE_TTL_SECONDS = 60 * 60 * 12;
// A catalogue changes when a broadcaster announces an episode, so it expires
// sooner than a search result. Six hours means a new episode shows up the same
// day without asking TMDb for a season list on every refresh.
const CATALOGUE_TTL_SECONDS = 60 * 60 * 6;
const RATE_WINDOW_SECONDS = 60;
const RATE_LIMIT_PER_WINDOW = 30;
const MAX_RESULTS = 6;
// A series with 10 seasons is 11 upstream calls. Doing that fan-out here rather
// than in the app is the whole point of the gateway: one client request, one
// cached answer, and TMDb sees a single origin it can rate-limit sensibly.
const MAX_SEASONS = 50;
const ARTWORK_CACHE_TTL_SECONDS = 60 * 60 * 24 * 30;
const PERSON_EXTERNAL_IDS_TTL_SECONDS = 60 * 60 * 24 * 365;
const ARTWORK_SIZES = new Set(["w92", "w185", "w342", "w500", "w780", "w1280", "original"]);

// The sizes Deluno actually caches. These must stay in step with
// src/Deluno.Integrations/Metadata/ArtworkSizes.cs — the two build the same URLs
// for the same titles, and a size changed in one and not the other leaves half a
// library at each resolution with nothing on screen to explain it. That file
// carries the reasoning: measured against --library-card-lg at DPR 2, one cached
// size rather than a srcset because artwork is downloaded once and re-served.
const POSTER_SIZE = "w780";
const BACKDROP_SIZE = "original";
const PORTRAIT_SIZE = "w185";
const MAX_CAST = 30;
const MAX_CREW = 20;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method !== "GET") {
      return json({ error: "method_not_allowed" }, 405, { Allow: "GET" });
    }

    if (url.pathname === "/health") {
      return json({ service: "deluno-metadata-gateway", status: "ok" });
    }

    if (url.pathname.startsWith("/artwork/")) {
      return serveArtwork(request, url);
    }

    const route = matchRoute(url.pathname);
    if (!route) {
      return json({ error: "not_found" }, 404);
    }

    const lookup = route.kind === "search" ? parseLookup(url.searchParams) : { value: null };
    if (lookup.error) {
      return json({ error: "invalid_request", message: lookup.error }, 400);
    }

    if (!env.TMDB_API_KEY) {
      return json({ error: "service_unavailable", message: "Metadata service is not configured." }, 503);
    }

    const clientAddress = request.headers.get("CF-Connecting-IP") ?? "unknown";
    const rate = await enforceRateLimit(env.METADATA_CACHE, clientAddress, Date.now());
    if (!rate.allowed) {
      return json(
        { error: "rate_limited", message: "Please try title matching again shortly." },
        429,
        { "Retry-After": String(rate.retryAfterSeconds) });
    }

    if (route.kind === "person-imdb") {
      return servePersonImdbRoute(route, env);
    }

    if (route.kind !== "search") {
      return serveCatalogueRoute(route, env);
    }

    const key = buildCacheKey(lookup.value);
    const cached = await env.METADATA_CACHE.get(key, "json");
    if (cached) {
      return json(cached, 200, { "Cache-Control": "public, max-age=300" });
    }

    try {
      const results = await lookupTmdb(lookup.value, env.TMDB_API_KEY, fetch, url.origin);
      const response = {
        provider: "deluno-broker",
        mode: "broker",
        resultCount: results.length,
        results
      };
      await env.METADATA_CACHE.put(key, JSON.stringify(response), { expirationTtl: CACHE_TTL_SECONDS });
      return json(response, 200, { "Cache-Control": "public, max-age=300" });
    } catch (error) {
      const providerStatus = error instanceof TmdbRequestError ? error.status : 502;
      // A 404 is meaningful only when Deluno asked for one exact identity.
      // Preserve it so the app can keep the title and offer a calm remap or
      // acknowledgement. Fuzzy searches still treat an upstream error as an
      // unavailable provider, never as evidence that a title was deleted.
      if (providerStatus === 404 && lookup.value.providerId) {
        return json(
          { error: "provider_record_missing", provider: "tmdb", providerId: lookup.value.providerId },
          404);
      }
      if (providerStatus === 429) {
        return json(
          { error: "provider_busy", message: "Title matching is busy. Please try again shortly." },
          503,
          { "Retry-After": "60" });
      }

      return json(
        { error: "provider_unavailable", message: "Title matching is temporarily unavailable." },
        503);
    }
  }
};

/**
 * The gateway serves title search, a series' season/episode catalogue, a movie's
 * release dates and collection membership, and lazy person-to-IMDb resolution.
 * These provider-specific operations stay behind one small broker boundary.
 */
export function matchRoute(pathname) {
  if (pathname === "/metadata/search") {
    return { kind: "search" };
  }

  const catalogue = /^\/metadata\/tv\/(\d{1,12})\/catalogue$/.exec(pathname);
  if (catalogue) {
    return { kind: "catalogue", id: catalogue[1] };
  }

  const releaseDates = /^\/metadata\/movie\/(\d{1,12})\/release-dates$/.exec(pathname);
  if (releaseDates) {
    return { kind: "release-dates", id: releaseDates[1] };
  }

  const collection = /^\/metadata\/movie\/(\d{1,12})\/collection$/.exec(pathname);
  if (collection) {
    return { kind: "collection", id: collection[1] };
  }

  const personImdb = /^\/person\/(\d{1,12})\/imdb$/.exec(pathname);
  if (personImdb) {
    return { kind: "person-imdb", id: personImdb[1] };
  }

  return null;
}

async function servePersonImdbRoute(route, env) {
  const key = `person-external-ids:v1:${route.id}`;
  const cached = await env.METADATA_CACHE.get(key, "json");
  if (cached && typeof cached.imdbId === "string") {
    return redirectToImdb(cached.imdbId);
  }
  if (cached && cached.imdbId === null) {
    return json({ error: "not_found" }, 404);
  }

  try {
    const imdbId = await lookupPersonImdbId(route.id, env.TMDB_API_KEY);
    await env.METADATA_CACHE.put(key, JSON.stringify({ imdbId }), {
      expirationTtl: PERSON_EXTERNAL_IDS_TTL_SECONDS
    });
    return imdbId ? redirectToImdb(imdbId) : json({ error: "not_found" }, 404);
  } catch (error) {
    const providerStatus = error instanceof TmdbRequestError ? error.status : 502;
    if (providerStatus === 404) {
      await env.METADATA_CACHE.put(key, JSON.stringify({ imdbId: null }), {
        expirationTtl: PERSON_EXTERNAL_IDS_TTL_SECONDS
      });
      return json({ error: "not_found" }, 404);
    }
    if (providerStatus === 429) {
      return json(
        { error: "provider_busy", message: "IMDb person matching is busy. Please try again shortly." },
        503,
        { "Retry-After": "60" });
    }

    return json({ error: "provider_unavailable", message: "IMDb person matching is temporarily unavailable." }, 503);
  }
}

export async function lookupPersonImdbId(personId, apiKey, request = fetch) {
  if (!/^\d{1,12}$/.test(String(personId ?? ""))) {
    return null;
  }

  const endpoint = new URL(`https://api.themoviedb.org/3/person/${personId}/external_ids`);
  endpoint.searchParams.set("api_key", apiKey);
  const result = await getJson(endpoint, request);
  return typeof result?.imdb_id === "string" && /^nm\d+$/i.test(result.imdb_id.trim())
    ? result.imdb_id.trim()
    : null;
}

function redirectToImdb(imdbId) {
  return new Response(null, {
    status: 302,
    headers: {
      Location: `https://www.imdb.com/name/${imdbId}/`,
      "Cache-Control": "public, max-age=86400"
    }
  });
}

async function serveCatalogueRoute(route, env) {
  const key = route.kind === "catalogue"
    ? `catalogue:v1:${route.id}`
    : route.kind === "release-dates"
      ? `release-dates:v1:${route.id}`
      : `collection:v1:${route.id}`;
  const cached = await env.METADATA_CACHE.get(key, "json");
  if (cached) {
    return json(cached, 200, { "Cache-Control": "public, max-age=600" });
  }

  try {
    const payload = route.kind === "catalogue"
      ? await lookupSeriesCatalogue(route.id, env.TMDB_API_KEY)
      : route.kind === "release-dates"
        ? await lookupMovieReleaseDates(route.id, env.TMDB_API_KEY)
        : await lookupMovieCollection(route.id, env.TMDB_API_KEY);

    await env.METADATA_CACHE.put(key, JSON.stringify(payload), { expirationTtl: CATALOGUE_TTL_SECONDS });
    return json(payload, 200, { "Cache-Control": "public, max-age=600" });
  } catch (error) {
    const providerStatus = error instanceof TmdbRequestError ? error.status : 502;
    if (providerStatus === 404) {
      return json({ error: "not_found" }, 404);
    }
    if (providerStatus === 429) {
      return json(
        { error: "provider_busy", message: "Metadata is busy. Please try again shortly." },
        503,
        { "Retry-After": "60" });
    }

    return json({ error: "provider_unavailable", message: "Metadata is temporarily unavailable." }, 503);
  }
}

export async function lookupSeriesCatalogue(providerId, apiKey, request = fetch) {
  const detailUrl = new URL(`https://api.themoviedb.org/3/tv/${providerId}`);
  detailUrl.searchParams.set("api_key", apiKey);
  const detail = await getJson(detailUrl, request);

  const seasonNumbers = Array.isArray(detail?.seasons)
    ? detail.seasons
      .map((season) => season?.season_number)
      .filter((value) => Number.isInteger(value) && value >= 0)
      .sort((left, right) => left - right)
      .slice(0, MAX_SEASONS)
    : [];

  const seasons = [];
  for (const seasonNumber of seasonNumbers) {
    const seasonUrl = new URL(`https://api.themoviedb.org/3/tv/${providerId}/season/${seasonNumber}`);
    seasonUrl.searchParams.set("api_key", apiKey);

    let season;
    try {
      season = await getJson(seasonUrl, request);
    } catch (error) {
      // One unavailable season must not lose the rest of the catalogue.
      if (error instanceof TmdbRequestError && error.status === 404) {
        continue;
      }
      throw error;
    }

    const episodes = Array.isArray(season?.episodes)
      ? season.episodes
        .filter((episode) => Number.isInteger(episode?.episode_number) && episode.episode_number > 0)
        .map((episode) => ({
          episodeNumber: episode.episode_number,
          title: typeof episode.name === "string" ? episode.name.trim() || null : null,
          overview: typeof episode.overview === "string" ? episode.overview.trim() || null : null,
          airDate: normalizeDate(episode.air_date)
        }))
      : [];

    seasons.push({
      seasonNumber,
      name: typeof season?.name === "string" ? season.name.trim() || null : null,
      airDate: normalizeDate(season?.air_date),
      episodeCount: episodes.length,
      episodes
    });
  }

  return {
    provider: "deluno-broker",
    mode: "broker",
    providerId: String(providerId),
    seasonCount: seasons.length,
    episodeCount: seasons.reduce((total, season) => total + season.episodes.length, 0),
    seasons
  };
}

export async function lookupMovieReleaseDates(providerId, apiKey, request = fetch) {
  const url = new URL(`https://api.themoviedb.org/3/movie/${providerId}/release_dates`);
  url.searchParams.set("api_key", apiKey);
  const payload = await getJson(url, request);

  // TMDb reports dates per country and per type. Deluno wants three answers, so
  // take the earliest of each across every region rather than guessing one.
  // 2 limited, 3 theatrical, 4 digital, 5 physical. A premiere (1) is a festival
  // screening, not something anyone can obtain, so it is ignored.
  let inCinemas = null;
  let digital = null;
  let physical = null;

  const entries = Array.isArray(payload?.results) ? payload.results : [];
  for (const country of entries) {
    for (const entry of Array.isArray(country?.release_dates) ? country.release_dates : []) {
      const date = normalizeDate(entry?.release_date);
      if (!date) {
        continue;
      }

      if ((entry.type === 2 || entry.type === 3) && (!inCinemas || date < inCinemas)) {
        inCinemas = date;
      } else if (entry.type === 4 && (!digital || date < digital)) {
        digital = date;
      } else if (entry.type === 5 && (!physical || date < physical)) {
        physical = date;
      }
    }
  }

  return {
    provider: "deluno-broker",
    mode: "broker",
    providerId: String(providerId),
    inCinemas,
    digital,
    physical
  };
}

function normalizeDate(value) {
  return typeof value === "string" && /^\d{4}-\d{2}-\d{2}/.test(value) ? value.slice(0, 10) : null;
}

export function parseLookup(params) {
  const query = params.get("query")?.trim() ?? "";
  const mediaType = params.get("mediaType")?.trim().toLowerCase() ?? "";
  const providerId = params.get("providerId")?.trim() || null;
  const rawYear = params.get("year")?.trim() ?? "";
  const year = rawYear ? Number.parseInt(rawYear, 10) : null;

  if (mediaType !== "movies" && mediaType !== "tv") {
    return { error: "mediaType must be movies or tv." };
  }
  if (!query || query.length > 160) {
    return { error: "query must contain between 1 and 160 characters." };
  }
  if (providerId && !/^\d{1,12}$/.test(providerId)) {
    return { error: "providerId must be a TMDb numeric identifier." };
  }
  if (rawYear && (!Number.isInteger(year) || year < 1800 || year > 2100)) {
    return { error: "year must be between 1800 and 2100." };
  }

  return { value: { query, mediaType, providerId, year } };
}

export async function enforceRateLimit(cache, clientAddress, now) {
  const window = Math.floor(now / (RATE_WINDOW_SECONDS * 1000));
  const key = `rate:${clientAddress}:${window}`;
  const count = Number.parseInt((await cache.get(key)) ?? "0", 10) || 0;
  const retryAfterSeconds = RATE_WINDOW_SECONDS - Math.floor((now / 1000) % RATE_WINDOW_SECONDS);

  if (count >= RATE_LIMIT_PER_WINDOW) {
    return { allowed: false, retryAfterSeconds };
  }

  await cache.put(key, String(count + 1), { expirationTtl: RATE_WINDOW_SECONDS + 5 });
  return { allowed: true, retryAfterSeconds: 0 };
}

/**
 * The version in this key is the *response shape*, not the query.
 *
 * A cached payload written under an older shape is not a cheaper answer to the
 * same question — it is a different answer, missing whatever the mapping has
 * learnt to send since. v3 held results with no runtime, certification, studio
 * or status, and would have kept serving them for twelve hours after the worker
 * that could produce them went live. Bump this whenever mapTmdbResult starts
 * emitting something new.
 *
 * v5 is the artwork sizes (#326). A payload's poster URL is part of its shape:
 * v4 answers carry w500 and w1280 URLs, and serving one is not a stale field
 * but a title cached at the old resolution for the next thirty days.
 */
export function buildCacheKey({ query, mediaType, providerId, year }) {
  const normalized = `${mediaType}|${query.toLocaleLowerCase("en-US")}|${year ?? ""}|${providerId ?? ""}`;
  return `search:v7:${encodeURIComponent(normalized)}`;
}

export async function lookupTmdb(lookup, apiKey, request = fetch, artworkOrigin = null) {
  if (lookup.providerId) {
    const detail = await getTmdbDetail(lookup.mediaType, lookup.providerId, apiKey, request, artworkOrigin);
    return detail ? [detail] : [];
  }

  const kind = lookup.mediaType === "tv" ? "tv" : "movie";
  const endpoint = new URL(`https://api.themoviedb.org/3/search/${kind}`);
  endpoint.searchParams.set("api_key", apiKey);
  endpoint.searchParams.set("query", lookup.query);
  endpoint.searchParams.set("include_adult", "false");
  if (lookup.year) {
    endpoint.searchParams.set(lookup.mediaType === "tv" ? "first_air_date_year" : "year", String(lookup.year));
  }

  const search = await getJson(endpoint, request);
  const matches = Array.isArray(search.results)
    ? search.results.filter((item) => item?.id && (item.title || item.name)).slice(0, MAX_RESULTS)
    : [];
  // Search cards need a title, year, rating, and artwork immediately. TMDb returns all of
  // those in its search response, so do not make a detail request for every visible card.
  // Deluno requests the full record only after the user selects a specific match.
  return matches
    .map((item) => mapTmdbResult(item, lookup.mediaType, artworkOrigin))
    .filter(Boolean);
}

async function getTmdbDetail(mediaType, providerId, apiKey, request, artworkOrigin) {
  const kind = mediaType === "tv" ? "tv" : "movie";
  const endpoint = new URL(`https://api.themoviedb.org/3/${kind}/${providerId}`);
  endpoint.searchParams.set("api_key", apiKey);
  // Everything a detail lookup can carry in one round trip, which is the whole
  // point of a gateway: the app makes one request, TMDb sees one origin.
  //
  // `release_dates` and `content_ratings` are where a certification lives — the
  // field Deluno's catalogue has declared and never filled — and `videos` is the
  // trailer. Appending them costs nothing extra; asking for them separately
  // would be four requests per title.
  //
  // `keywords` is what a library is actually organised by beyond genre: "space
  // travel" and "time loop" are questions Genre cannot ask, because a film is
  // Science Fiction either way. It is the last field #306 listed that Deluno
  // had no way to fetch.
  endpoint.searchParams.set(
    "append_to_response",
    "external_ids,credits,release_dates,content_ratings,videos,keywords");
  const item = await getJson(endpoint, request);
  return mapTmdbResult(item, mediaType, artworkOrigin);
}

async function getJson(url, request) {
  const response = await request(url, { headers: { Accept: "application/json" } });
  if (!response.ok) {
    throw new TmdbRequestError(response.status);
  }
  return response.json();
}

export function mapTmdbResult(item, mediaType, artworkOrigin = null) {
  if (!item?.id) {
    return null;
  }

  const title = item.title ?? item.name;
  if (!title) {
    return null;
  }

  const releaseDate = mediaType === "tv" ? item.first_air_date : item.release_date;
  const year = /^\d{4}/.test(releaseDate ?? "") ? Number.parseInt(releaseDate.slice(0, 4), 10) : null;
  const kind = mediaType === "tv" ? "tv" : "movie";
  const rating = Number.isFinite(item.vote_average) ? item.vote_average : null;
  const votes = Number.isInteger(item.vote_count) ? item.vote_count : null;
  const externalUrl = `https://www.themoviedb.org/${kind}/${item.id}`;

  // Runtime, popularity and vote count. Deluno has had columns for all three
  // since V0012 — "the facts the library list has always displayed but never
  // had" — and on a broker install they have been null for every title ever
  // added, because this mapping dropped them. The columns are indexed, the
  // repository writes them and the API accepts them; only this end was missing,
  // so half the fix shipped and nothing said so.
  //
  // TMDb returns popularity and vote_count on a *search* result and runtime only
  // on a *detail* one, which is why runtime is conditional rather than absent:
  // a search card legitimately has none, and sending null there is honest.
  const runtimeMinutes = Number.isInteger(item.runtime) && item.runtime > 0
    ? item.runtime
    : Array.isArray(item.episode_run_time)
      ? item.episode_run_time.find((minutes) => Number.isInteger(minutes) && minutes > 0) ?? null
      : null;
  const popularity = Number.isFinite(item.popularity) ? item.popularity : null;

  // The rest of what TMDb already had and this mapping was throwing away.
  //
  // Deluno's catalogue has *declared* several of these for a long time —
  // `certification`, `collection`, `language` are read straight out of the
  // stored metadata blob by the library adapters — and nothing has ever put a
  // value in them, so the columns read empty on every install. They are not new
  // features; they are the other half of features already on screen.
  //
  // Studio and network are what Radarr and Sonarr let you filter a library by,
  // and status is the difference between a show that has ended and one still
  // running, which is the single most useful thing to know about a series that
  // is missing episodes.
  const studio = Array.isArray(item.production_companies)
    ? item.production_companies.map((company) => company?.name).find(Boolean) ?? null
    : null;
  const network = Array.isArray(item.networks)
    ? item.networks.map((entry) => entry?.name).find(Boolean) ?? null
    : null;
  const collection = item.belongs_to_collection?.name ?? null;
  const collectionProviderId = item.belongs_to_collection?.id != null
    ? String(item.belongs_to_collection.id)
    : null;
  const crew = Array.isArray(item.credits?.crew) ? item.credits.crew : [];
  const director = crew.find((person) => person?.job === "Director")?.name ?? null;
  const trailerUrl = pickTrailerUrl(item.videos?.results);
  const certification = mediaType === "tv"
    ? pickContentRating(item.content_ratings?.results)
    : pickCertification(item.release_dates?.results);

  return {
    provider: "tmdb",
    providerId: String(item.id),
    mediaType,
    runtimeMinutes,
    popularity,
    voteCount: votes,
    title,
    originalTitle: item.original_title ?? item.original_name ?? title,
    year,
    overview: item.overview ?? null,
    posterUrl: imageUrl(item.poster_path, POSTER_SIZE, artworkOrigin),
    backdropUrl: imageUrl(item.backdrop_path, BACKDROP_SIZE, artworkOrigin),
    rating,
    ratings: rating === null ? [] : [{
      source: "tmdb",
      label: "TMDb",
      score: rating,
      maxScore: 10,
      // voteCount, not votes. Deluno deserialises this array straight into
      // MetadataRatingItem, whose property is VoteCount, so a key named
      // "votes" was silently dropped: every broker-mode library had a TMDb
      // score with no count behind it, and #319's "IMDb above 7.5 with more
      // than ten thousand votes" could not be asked at all.
      voteCount: votes,
      url: externalUrl,
      kind: "community"
    }],
    genres: Array.isArray(item.genres)
      ? item.genres.map((genre) => genre?.name).filter(Boolean)
      : [],
    // TMDb answers `keywords.keywords` for a film and `keywords.results` for a
    // show — the same data under two names, which is the sort of thing that
    // silently returns an empty list for half a library if only one is read.
    keywords: readKeywords(item),
    cast: readCast(item, artworkOrigin),
    crew: readCrew(crew, artworkOrigin),
    imdbId: item.external_ids?.imdb_id ?? null,
    externalUrl,
    certification,
    studio,
    network,
    collection,
    collectionProviderId,
    director,
    trailerUrl,
    tagline: item.tagline?.trim() || null,
    homepage: item.homepage?.trim() || null,
    originalLanguage: item.original_language ?? null,
    // "Released" / "In Production" for a film; "Returning Series" / "Ended" /
    // "Canceled" for a show. A show that has ended and is missing episodes is a
    // different problem from one that is still airing them.
    status: item.status ?? null
  };
}

/** Return every movie TMDb currently associates with a collection. */
export async function lookupMovieCollection(providerId, apiKey, request = fetch) {
  const url = new URL(`https://api.themoviedb.org/3/collection/${providerId}`);
  url.searchParams.set("api_key", apiKey);
  const payload = await getJson(url, request);
  const parts = Array.isArray(payload?.parts) ? payload.parts : [];

  return {
    provider: "deluno-broker",
    mode: "broker",
    providerId: String(payload?.id ?? providerId),
    name: typeof payload?.name === "string" ? payload.name.trim() || null : null,
    overview: typeof payload?.overview === "string" ? payload.overview.trim() || null : null,
    posterUrl: imageUrl(payload?.poster_path, POSTER_SIZE, null),
    backdropUrl: imageUrl(payload?.backdrop_path, BACKDROP_SIZE, null),
    movies: parts
      .filter((movie) => Number.isInteger(movie?.id) && movie.id > 0 && typeof movie?.title === "string" && movie.title.trim())
      .map((movie) => ({
        providerId: String(movie.id),
        title: movie.title.trim(),
        year: /^\d{4}/.test(movie.release_date ?? "") ? Number.parseInt(movie.release_date.slice(0, 4), 10) : null,
        overview: typeof movie.overview === "string" ? movie.overview.trim() || null : null,
        posterUrl: imageUrl(movie.poster_path, POSTER_SIZE, null),
        backdropUrl: imageUrl(movie.backdrop_path, BACKDROP_SIZE, null),
        externalUrl: `https://www.themoviedb.org/movie/${movie.id}`,
        imdbId: null
      }))
  };
}

/** Build a gateway URL without spending one TMDb call per person in a title. */
export function buildPersonImdbResolverUrl(personId, artworkOrigin) {
  if (!artworkOrigin || !/^\d{1,12}$/.test(String(personId ?? ""))) {
    return null;
  }

  return new URL(`/person/${personId}/imdb`, artworkOrigin).toString();
}

/**
 * A certification is per country and TMDb returns every country it knows.
 * US first because it is the vocabulary most people recognise, then GB and AU,
 * then whatever else carries one — better a rating from somewhere than none.
 */
export function pickCertification(results) {
  if (!Array.isArray(results)) return null;
  const preferred = ["US", "GB", "AU"];
  const ordered = [
    ...preferred.map((code) => results.find((entry) => entry?.iso_3166_1 === code)),
    ...results
  ].filter(Boolean);

  for (const entry of ordered) {
    const value = Array.isArray(entry.release_dates)
      ? entry.release_dates.map((release) => release?.certification).find((cert) => cert)
      : null;
    if (value) return value;
  }

  return null;
}

/** The television twin: `content_ratings` rather than `release_dates`. */
export function pickContentRating(results) {
  if (!Array.isArray(results)) return null;
  const preferred = ["US", "GB", "AU"];
  const ordered = [
    ...preferred.map((code) => results.find((entry) => entry?.iso_3166_1 === code)),
    ...results
  ].filter(Boolean);

  return ordered.map((entry) => entry.rating).find((rating) => rating) ?? null;
}

/** The official YouTube trailer, if TMDb has one. Nothing else is worth linking. */
export function pickTrailerUrl(videos) {
  if (!Array.isArray(videos)) return null;
  const trailer =
    videos.find((video) => video?.site === "YouTube" && video?.type === "Trailer" && video?.official) ??
    videos.find((video) => video?.site === "YouTube" && video?.type === "Trailer");
  return trailer?.key ? `https://www.youtube.com/watch?v=${trailer.key}` : null;
}

/**
 * The keywords TMDb attaches to a title, under either of the two names it uses.
 *
 * Capped, because a popular film can carry sixty of them and the whole point of
 * the gateway is that one cached answer is small enough to be worth caching.
 */
function readKeywords(item) {
  const list = item.keywords?.keywords ?? item.keywords?.results;
  if (!Array.isArray(list)) return [];
  return list
    .map((keyword) => (typeof keyword?.name === "string" ? keyword.name.trim() : null))
    .filter(Boolean)
    .slice(0, 25);
}

/**
 * The billed cast, not the first ten of it.
 *
 * Ten was the whole ensemble of a small film and the opening titles of a big
 * one — Arrival's page stopped at Frank Schorpion and never reached the rest,
 * so the section read as if the film had a cast of ten. TMDb bills them in
 * order, so the cut can simply be made much later; thirty is past the point
 * where a name means anything to a viewer, and it is still one small array.
 */
function readCast(item, artworkOrigin) {
  const cast = Array.isArray(item.credits?.cast) ? item.credits.cast : [];
  return cast
    .filter((person) => typeof person?.name === "string" && person.name.trim())
    .slice(0, MAX_CAST)
    .map((person) => ({
      // The TMDb person id, passed through rather than dropped. It is what a
      // credit links to, and what following a person's filmography would key
      // on — neither is possible from a name alone, because names collide.
      personId: typeof person.id === "number" ? String(person.id) : null,
      name: person.name.trim(),
      character: typeof person.character === "string" ? person.character.trim() || null : null,
      profileUrl: imageUrl(person.profile_path, PORTRAIT_SIZE, artworkOrigin),
      imdbUrl: buildPersonImdbResolverUrl(person.id, artworkOrigin)
    }));
}

/**
 * Who made it, beyond the one director already read.
 *
 * <p>TMDb's crew list is exhaustive — every runner and assistant — and ordered
 * by nothing useful, so it cannot be taken as it comes. Two rules make it
 * readable: keep only the jobs a viewer recognises, in the order a title card
 * would list them, and fold each person into a single entry. The same person is
 * routinely credited three times (Villeneuve directs and produces; a writer is
 * "Screenplay" and "Novel"), and three identical portraits in a row reads as a
 * bug rather than as a fuller credit.</p>
 */
const CREW_JOBS = [
  "Director", "Screenplay", "Writer", "Story", "Novel", "Characters",
  "Producer", "Executive Producer", "Original Music Composer", "Music",
  "Director of Photography", "Editor", "Production Design", "Art Direction",
  "Costume Design", "Casting", "Visual Effects Supervisor"
];

function readCrew(crew, artworkOrigin) {
  const byPerson = new Map();

  for (const job of CREW_JOBS) {
    for (const person of crew) {
      if (person?.job !== job) continue;
      const name = typeof person.name === "string" ? person.name.trim() : "";
      if (!name) continue;

      const key = person.id ?? name;
      const existing = byPerson.get(key);
      if (existing) {
        if (!existing.jobs.includes(job)) existing.jobs.push(job);
        continue;
      }

      byPerson.set(key, {
        personId: typeof person.id === "number" ? String(person.id) : null,
        name,
        jobs: [job],
        profileUrl: imageUrl(person.profile_path, PORTRAIT_SIZE, artworkOrigin),
        imdbUrl: buildPersonImdbResolverUrl(person.id, artworkOrigin)
      });
    }
  }

  return [...byPerson.values()]
    .slice(0, MAX_CREW)
    .map((person) => ({
      personId: person.personId,
      name: person.name,
      job: person.jobs.join(", "),
      profileUrl: person.profileUrl,
      imdbUrl: person.imdbUrl
    }));
}

function imageUrl(path, size, artworkOrigin) {
  if (typeof path !== "string" || !path.startsWith("/")) {
    return null;
  }

  if (artworkOrigin) {
    return new URL(`/artwork/${size}${path}`, artworkOrigin).toString();
  }

  return `https://image.tmdb.org/t/p/${size}${path}`;
}

async function serveArtwork(request, url) {
  const pathParts = url.pathname.split("/").filter(Boolean);
  const [, size, filename] = pathParts;
  if (pathParts.length !== 3 || !ARTWORK_SIZES.has(size) || !/^[A-Za-z0-9._-]{1,255}$/.test(filename ?? "")) {
    return json({ error: "not_found" }, 404);
  }

  const cache = caches.default;
  const cacheKey = new Request(url.toString(), request);
  const cached = await cache.match(cacheKey);
  if (cached) {
    return cached;
  }

  const upstream = await fetch(`https://image.tmdb.org/t/p/${size}/${filename}`);
  if (!upstream.ok) {
    return json({ error: "artwork_unavailable" }, upstream.status === 404 ? 404 : 503);
  }

  const headers = new Headers();
  headers.set("Cache-Control", `public, max-age=${ARTWORK_CACHE_TTL_SECONDS}, immutable`);
  headers.set("Content-Type", upstream.headers.get("Content-Type") ?? "image/jpeg");
  // Readable by the page that draws it, not just displayable.
  //
  // A detail page's hero scrim is solved from the backdrop's own brightness —
  // Arrival's is a pale fog plate and drowns light text at the same scrim
  // strength that a dark plate needs. Measuring it means drawing the image to a
  // canvas, and a canvas tainted by a cross-origin image cannot be read back.
  // This is public artwork already served to anyone who asks; the header lets
  // the browser hand the pixels to the page that is displaying them anyway.
  headers.set("Access-Control-Allow-Origin", "*");
  const response = new Response(upstream.body, { status: 200, headers });
  await cache.put(cacheKey, response.clone());
  return response;
}

function json(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...headers }
  });
}

class TmdbRequestError extends Error {
  constructor(status) {
    super(`TMDb request failed with ${status}`);
    this.status = status;
  }
}
