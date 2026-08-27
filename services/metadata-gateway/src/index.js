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
const ARTWORK_SIZES = new Set(["w92", "w185", "w342", "w500", "w780", "w1280", "original"]);

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
 * The gateway serves three things: title search, a series' season/episode
 * catalogue, and a movie's release dates. The last two are what let Deluno know
 * an episode exists before a file for it does, and when a film is obtainable.
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

  return null;
}

async function serveCatalogueRoute(route, env) {
  const key = route.kind === "catalogue" ? `catalogue:v1:${route.id}` : `release-dates:v1:${route.id}`;
  const cached = await env.METADATA_CACHE.get(key, "json");
  if (cached) {
    return json(cached, 200, { "Cache-Control": "public, max-age=600" });
  }

  try {
    const payload = route.kind === "catalogue"
      ? await lookupSeriesCatalogue(route.id, env.TMDB_API_KEY)
      : await lookupMovieReleaseDates(route.id, env.TMDB_API_KEY);

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

export function buildCacheKey({ query, mediaType, providerId, year }) {
  const normalized = `${mediaType}|${query.toLocaleLowerCase("en-US")}|${year ?? ""}|${providerId ?? ""}`;
  return `search:v3:${encodeURIComponent(normalized)}`;
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
  endpoint.searchParams.set(
    "append_to_response",
    "external_ids,credits,release_dates,content_ratings,videos");
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
    posterUrl: imageUrl(item.poster_path, "w500", artworkOrigin),
    backdropUrl: imageUrl(item.backdrop_path, "w1280", artworkOrigin),
    rating,
    ratings: rating === null ? [] : [{
      source: "tmdb",
      label: "TMDb",
      score: rating,
      maxScore: 10,
      votes,
      url: externalUrl,
      kind: "community"
    }],
    genres: Array.isArray(item.genres)
      ? item.genres.map((genre) => genre?.name).filter(Boolean)
      : [],
    cast: Array.isArray(item.credits?.cast)
      ? item.credits.cast
        .filter((person) => typeof person?.name === "string" && person.name.trim())
        .slice(0, 10)
        .map((person) => ({
          name: person.name.trim(),
          character: typeof person.character === "string" ? person.character.trim() || null : null,
          profileUrl: imageUrl(person.profile_path, "w185", artworkOrigin)
        }))
      : [],
    imdbId: item.external_ids?.imdb_id ?? null,
    externalUrl,
    certification,
    studio,
    network,
    collection,
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
