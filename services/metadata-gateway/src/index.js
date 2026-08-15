const CACHE_TTL_SECONDS = 60 * 60 * 12;
const RATE_WINDOW_SECONDS = 60;
const RATE_LIMIT_PER_WINDOW = 30;
const MAX_RESULTS = 6;
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

    if (url.pathname !== "/metadata/search") {
      return json({ error: "not_found" }, 404);
    }

    const lookup = parseLookup(url.searchParams);
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
  endpoint.searchParams.set("append_to_response", "external_ids,credits");
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

  return {
    provider: "tmdb",
    providerId: String(item.id),
    mediaType,
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
    externalUrl
  };
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
