// TheTVDB v4 served through the gateway exactly like TMDb: the API key (and the
// optional subscriber PIN) live only in this Worker's secrets, so a Deluno
// install never holds them.
//
// Terms this file is written to (https://thetvdb.com/api-information and the
// v4-api repository README, read 29 Sep 2026):
//  - Attribution: "Attribution with a direct link to TheTVDB.com must be
//    displayed to end users viewing metadata from our API." Every payload below
//    carries `attribution`, and Deluno shows it wherever TV metadata shows.
//  - Caching: TheTVDB recommends "a caching proxy" and to "set time to live for
//    each endpoint based on the needs of your users" - series/episode records
//    should stay fresh, rarely-changing data may be kept "a week or even
//    longer". There is no fixed maximum, so the TTLs below are our own choice.
//  - A user-supported key needs the subscriber's PIN at /login (TVDB_PIN,
//    optional); a negotiated/licensed key sends none. The bearer token lasts
//    one month.
//  - Rates and fees may change "at any point without notice"; nothing here
//    hard-codes a limit beyond the gateway's own per-IP limiter.

export const TVDB_API = "https://api4.thetvdb.com/v4";
export const TVDB_ATTRIBUTION = {
  text: "TV metadata provided by TheTVDB",
  url: "https://thetvdb.com"
};

// Search results and series records: an episode announcement or a new season
// should show up the same day, so keep these short.
export const TVDB_SEARCH_TTL_SECONDS = 60 * 60 * 12;
export const TVDB_SERIES_TTL_SECONDS = 60 * 60 * 6;
export const TVDB_CATALOGUE_TTL_SECONDS = 60 * 60 * 6;
// Alternate orders (DVD, absolute) change far less often than the aired list.
export const TVDB_ORDERS_TTL_SECONDS = 60 * 60 * 24;
export const TVDB_TRANSLATION_TTL_SECONDS = 60 * 60 * 24 * 7;
export const TVDB_REMOTE_ID_TTL_SECONDS = 60 * 60 * 24 * 7;
// The bearer token is valid for a month; renew a few days early.
const TOKEN_TTL_SECONDS = 60 * 60 * 24 * 25;
const TOKEN_KEY = "tvdb-token:v1";
const MAX_RESULTS = 8;
const MAX_PAGES = 20;
const MAX_SCREENSHOTS = 12;

// TVDB artwork type ids (the /artwork/types endpoint's own numbering).
const ART_BANNER = 1;
const ART_POSTER = 2;
const ART_BACKGROUND = 3;
const ART_CLEARLOGO = 23;

export class TvdbRequestError extends Error {
  constructor(status) {
    super(`TheTVDB request failed with HTTP ${status}`);
    this.status = status;
  }
}

export function matchTvdbRoute(pathname) {
  if (pathname === "/metadata/tvdb/search") {
    return { kind: "tvdb-search" };
  }
  const series = /^\/metadata\/tvdb\/tv\/(\d{1,12})$/.exec(pathname);
  if (series) {
    return { kind: "tvdb-series", id: series[1] };
  }
  const catalogue = /^\/metadata\/tvdb\/tv\/(\d{1,12})\/catalogue$/.exec(pathname);
  if (catalogue) {
    return { kind: "tvdb-catalogue", id: catalogue[1] };
  }
  const groups = /^\/metadata\/tvdb\/tv\/(\d{1,12})\/episode-groups$/.exec(pathname);
  if (groups) {
    return { kind: "tvdb-episode-groups", id: groups[1] };
  }
  const translation = /^\/metadata\/tvdb\/tv\/(\d{1,12})\/translations\/([a-z]{3})$/.exec(pathname);
  if (translation) {
    return { kind: "tvdb-translation", id: translation[1], language: translation[2] };
  }
  const remote = /^\/metadata\/tvdb\/remote\/(tt\d{1,12})$/.exec(pathname);
  if (remote) {
    return { kind: "tvdb-remote", id: remote[1] };
  }
  return null;
}

export function parseTvdbSearch(params) {
  const query = params.get("query")?.trim() ?? "";
  const rawYear = params.get("year")?.trim() ?? "";
  const year = rawYear ? Number.parseInt(rawYear, 10) : null;
  if (!query || query.length > 160) {
    return { error: "query must contain between 1 and 160 characters." };
  }
  if (rawYear && (!Number.isInteger(year) || year < 1800 || year > 2100)) {
    return { error: "year must be between 1800 and 2100." };
  }
  return { value: { query, year } };
}

export function tvdbCacheKey(kind, id) {
  // Bump the version when a mapping starts emitting something new: an older
  // cached payload is a different answer, not a cheaper one.
  return `tvdb:v1:${kind}:${encodeURIComponent(String(id).toLocaleLowerCase("en-US"))}`;
}

/** The bearer token, cached in KV so one login serves every Worker instance. */
export async function getTvdbToken(env, request = fetch, { force = false } = {}) {
  if (!force) {
    const cached = await env.METADATA_CACHE.get(TOKEN_KEY);
    if (cached) {
      return cached;
    }
  }

  const body = { apikey: env.TVDB_API_KEY };
  // A user-supported key needs the subscriber PIN; a licensed key must send none.
  if (env.TVDB_PIN) {
    body.pin = env.TVDB_PIN;
  }
  const response = await request(`${TVDB_API}/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    throw new TvdbRequestError(response.status === 401 ? 401 : 502);
  }
  const token = (await response.json())?.data?.token;
  if (typeof token !== "string" || !token) {
    throw new TvdbRequestError(502);
  }
  await env.METADATA_CACHE.put(TOKEN_KEY, token, { expirationTtl: TOKEN_TTL_SECONDS });
  return token;
}

async function tvdbGet(env, path, params, request) {
  const url = new URL(`${TVDB_API}${path}`);
  for (const [key, value] of Object.entries(params ?? {})) {
    if (value !== undefined && value !== null && value !== "") {
      url.searchParams.set(key, String(value));
    }
  }

  for (let attempt = 0; attempt < 2; attempt++) {
    const token = await getTvdbToken(env, request, { force: attempt > 0 });
    const response = await request(url, {
      headers: { Accept: "application/json", Authorization: `Bearer ${token}` }
    });
    if (response.status === 401 && attempt === 0) {
      continue; // token expired or revoked: log in again once
    }
    if (!response.ok) {
      throw new TvdbRequestError(response.status);
    }
    return response.json();
  }
  throw new TvdbRequestError(401);
}

const text = (value) => (typeof value === "string" && value.trim() ? value.trim() : null);
const date = (value) => (typeof value === "string" && /^\d{4}-\d{2}-\d{2}/.test(value) ? value.slice(0, 10) : null);
const imageUrl = (value) => {
  const trimmed = text(value);
  if (!trimmed) return null;
  if (/^https?:\/\//i.test(trimmed)) return trimmed;
  return `https://artworks.thetvdb.com${trimmed.startsWith("/") ? "" : "/"}${trimmed}`;
};

function remoteIds(list) {
  const result = { imdbId: null, tmdbId: null };
  for (const entry of Array.isArray(list) ? list : []) {
    const id = text(entry?.id);
    const source = String(entry?.sourceName ?? "").toLowerCase();
    if (!id) continue;
    if (source.includes("imdb") && /^tt\d+$/i.test(id)) {
      result.imdbId ??= id;
    } else if (source.includes("themoviedb")) {
      // TheTVDB stores a series' TMDb id as plain digits or as "tv/1399"; a
      // "movie/..." id belongs to a film and is not a series identity.
      const match = /^(?:tv\/)?(\d{1,12})$/i.exec(id);
      if (match) result.tmdbId ??= match[1];
    }
  }
  return result;
}

function statusName(status) {
  return text(status?.name ?? status);
}

/** A search hit or a series record in Deluno's TVDB series shape. */
export function mapTvdbSeries(record, extras = {}) {
  const tvdbId = record?.tvdb_id ?? record?.id;
  if (!tvdbId) return null;
  const ids = remoteIds(record.remote_ids ?? record.remoteIds);
  const title = extras.translation?.name ?? text(record.name_translated) ?? text(record.name) ?? text(record.title);
  if (!title) return null;
  const yearText = text(record.year) ?? text(record.firstAired) ?? text(record.first_air_time);
  const year = /^\d{4}/.test(yearText ?? "") ? Number.parseInt(yearText.slice(0, 4), 10) : null;
  const aliases = [
    ...(Array.isArray(record.aliases) ? record.aliases : []).map((alias) => (typeof alias === "string" ? alias : alias?.name)),
    extras.translation?.aliases ?? []
  ].flat().map(text).filter(Boolean);
  const originalTitle = text(record.name);
  const genres = (Array.isArray(record.genres) ? record.genres : [])
    .map((genre) => (typeof genre === "string" ? genre : genre?.name))
    .map(text)
    .filter(Boolean);
  const network = text(record.network) ?? text(record.latestNetwork?.name) ?? text(record.originalNetwork?.name);
  const artworks = Array.isArray(record.artworks) ? record.artworks : [];
  const bestArt = (type) => artworks
    .filter((art) => art?.type === type && imageUrl(art.image))
    .sort((a, b) => (b.score ?? 0) - (a.score ?? 0))[0];
  const backgrounds = artworks
    .filter((art) => art?.type === ART_BACKGROUND && imageUrl(art.image))
    .sort((a, b) => (b.score ?? 0) - (a.score ?? 0));
  const contentRating = (Array.isArray(record.contentRatings) ? record.contentRatings : [])
    .find((rating) => text(rating?.name) && (rating.country === "usa" || rating.country === "US"))
    ?? (Array.isArray(record.contentRatings) ? record.contentRatings.find((rating) => text(rating?.name)) : null);

  return {
    provider: "tvdb",
    tvdbId: String(tvdbId),
    tmdbId: ids.tmdbId,
    imdbId: ids.imdbId,
    title,
    originalTitle: originalTitle && originalTitle !== title ? originalTitle : null,
    year,
    overview: extras.translation?.overview ?? text(record.overview_translated?.[0]) ?? text(record.overview),
    posterUrl: imageUrl(record.image_url ?? record.image ?? record.poster ?? bestArt(ART_POSTER)?.image),
    backdropUrl: imageUrl(bestArt(ART_BACKGROUND)?.image),
    bannerUrl: imageUrl(bestArt(ART_BANNER)?.image),
    logoUrl: imageUrl(bestArt(ART_CLEARLOGO)?.image),
    screenshots: backgrounds.slice(1, 1 + MAX_SCREENSHOTS).map((art) => imageUrl(art.image)),
    genres,
    network,
    status: statusName(record.status),
    runtimeMinutes: Number.isInteger(record.averageRuntime) && record.averageRuntime > 0 ? record.averageRuntime : null,
    originalLanguage: text(record.originalLanguage) ?? text(record.primary_language),
    certification: text(contentRating?.name),
    aliases: [...new Set(aliases)].slice(0, 25),
    externalUrl: `https://thetvdb.com/dereferrer/series/${tvdbId}`,
    defaultSeasonType: Number.isInteger(record.defaultSeasonType) ? record.defaultSeasonType : null
  };
}

export async function searchTvdb(lookup, env, request = fetch) {
  const payload = await tvdbGet(env, "/search", {
    query: lookup.query,
    type: "series",
    year: lookup.year ?? undefined,
    limit: MAX_RESULTS
  }, request);
  const results = (Array.isArray(payload?.data) ? payload.data : [])
    .map((item) => mapTvdbSeries(item))
    .filter(Boolean)
    .slice(0, MAX_RESULTS);
  return { provider: "tvdb", resultCount: results.length, results, attribution: TVDB_ATTRIBUTION };
}

/**
 * One series' name and overview in one language (TheTVDB's three-letter code).
 * A language TheTVDB has no translation for is a 404, which the app reads as
 * "fall back to TMDb, then the original".
 */
export async function lookupTvdbTranslation(id, language, env, request = fetch) {
  const payload = await tvdbGet(env, `/series/${id}/translations/${language}`, {}, request);
  const title = text(payload?.data?.name);
  const overview = text(payload?.data?.overview);
  if (!title && !overview) {
    throw new TvdbRequestError(404);
  }
  return { provider: "tvdb", language, title, overview, attribution: TVDB_ATTRIBUTION };
}

export async function lookupTvdbSeries(id, env, request = fetch) {
  const payload = await tvdbGet(env, `/series/${id}/extended`, { short: "false" }, request);
  const record = payload?.data;
  if (!record?.id) {
    throw new TvdbRequestError(404);
  }
  // The base record's name is the show's original-language name. The English
  // translation is what a Deluno title and its release names use; a series
  // that has none simply keeps the base name.
  let translation = null;
  try {
    const translated = await tvdbGet(env, `/series/${id}/translations/eng`, {}, request);
    translation = {
      name: text(translated?.data?.name),
      overview: text(translated?.data?.overview),
      aliases: Array.isArray(translated?.data?.aliases) ? translated.data.aliases : []
    };
  } catch (error) {
    if (!(error instanceof TvdbRequestError) || error.status !== 404) throw error;
  }
  return { provider: "tvdb", series: mapTvdbSeries(record, { translation }), attribution: TVDB_ATTRIBUTION };
}

export async function lookupTvdbRemote(imdbId, env, request = fetch) {
  const payload = await tvdbGet(env, `/search/remoteid/${imdbId}`, {}, request);
  const series = (Array.isArray(payload?.data) ? payload.data : []).find((entry) => entry?.series?.id)?.series;
  return {
    provider: "tvdb",
    imdbId,
    tvdbId: series?.id ? String(series.id) : null,
    attribution: TVDB_ATTRIBUTION
  };
}

/** Every episode of one season type, following TheTVDB's own pagination. */
async function fetchEpisodes(env, id, seasonType, request, language) {
  const episodes = [];
  for (let page = 0; page < MAX_PAGES; page++) {
    const path = language
      ? `/series/${id}/episodes/${seasonType}/${language}`
      : `/series/${id}/episodes/${seasonType}`;
    const payload = await tvdbGet(env, path, { page }, request);
    const batch = payload?.data?.episodes;
    if (Array.isArray(batch)) {
      episodes.push(...batch);
    }
    if (!payload?.links?.next) break;
  }
  return episodes;
}

function isEpisode(episode) {
  return Number.isInteger(episode?.number) && episode.number > 0
    && Number.isInteger(episode?.seasonNumber) && episode.seasonNumber >= 0;
}

/**
 * The aired ("official") season/episode list with the absolute number TheTVDB
 * keeps for each episode - what anime release names are numbered by.
 */
export async function lookupTvdbCatalogue(id, env, request = fetch) {
  let episodes;
  try {
    episodes = await fetchEpisodes(env, id, "official", request, "eng");
  } catch (error) {
    // An English translation TheTVDB does not have is not a missing series.
    if (!(error instanceof TvdbRequestError) || error.status !== 404) throw error;
    episodes = await fetchEpisodes(env, id, "official", request, null);
  }

  const bySeason = new Map();
  for (const episode of episodes.filter(isEpisode)) {
    const list = bySeason.get(episode.seasonNumber) ?? [];
    list.push({
      episodeNumber: episode.number,
      title: text(episode.name),
      overview: text(episode.overview),
      airDate: date(episode.aired),
      absoluteNumber: Number.isInteger(episode.absoluteNumber) && episode.absoluteNumber > 0 ? episode.absoluteNumber : null,
      runtimeMinutes: Number.isInteger(episode.runtime) && episode.runtime > 0 ? episode.runtime : null
    });
    bySeason.set(episode.seasonNumber, list);
  }

  const seasons = [...bySeason.entries()]
    .sort(([left], [right]) => left - right)
    .map(([seasonNumber, list]) => {
      list.sort((left, right) => left.episodeNumber - right.episodeNumber);
      const firstAired = list.map((entry) => entry.airDate).filter(Boolean).sort()[0] ?? null;
      return { seasonNumber, name: null, airDate: firstAired, episodeCount: list.length, episodes: list };
    });

  return {
    provider: "tvdb",
    mode: "broker",
    providerId: String(id),
    seasonCount: seasons.length,
    episodeCount: seasons.reduce((total, season) => total + season.episodes.length, 0),
    seasons,
    attribution: TVDB_ATTRIBUTION
  };
}

/**
 * The alternate orders TheTVDB publishes, in the same shape as TMDb's episode
 * groups (type 3 = DVD, type 2 = absolute) so Deluno reads both one way.
 * Episodes are matched back to the aired list by TheTVDB's episode id.
 */
export async function lookupTvdbEpisodeGroups(id, env, request = fetch) {
  const aired = (await fetchEpisodes(env, id, "official", request, null)).filter(isEpisode);
  const airedById = new Map(aired.map((episode) => [episode.id, episode]));
  const groups = [];

  const orders = [
    { seasonType: "dvd", type: 3, name: "DVD order" },
    { seasonType: "absolute", type: 2, name: "Absolute order" }
  ];
  for (const order of orders) {
    let episodes;
    try {
      episodes = (await fetchEpisodes(env, id, order.seasonType, request, null)).filter(isEpisode);
    } catch (error) {
      if (error instanceof TvdbRequestError && error.status === 404) continue;
      throw error;
    }
    const mapped = episodes
      .map((episode) => {
        const original = airedById.get(episode.id);
        return original
          ? {
              originalSeasonNumber: original.seasonNumber,
              originalEpisodeNumber: original.number,
              groupSeasonNumber: episode.seasonNumber,
              groupEpisodeNumber: episode.number
            }
          : null;
      })
      .filter(Boolean);
    if (mapped.length > 0) {
      groups.push({ type: order.type, providerGroupId: `tvdb:${id}:${order.seasonType}`, name: order.name, episodes: mapped });
    }
  }

  return { provider: "tvdb", providerId: String(id), groups, attribution: TVDB_ATTRIBUTION };
}

/** Serve a TVDB route; returns a Response. `json` is the gateway's own helper. */
export async function serveTvdbRoute(route, url, env, cache, json, request = fetch) {
  if (!env.TVDB_API_KEY) {
    return json({ error: "service_unavailable", message: "TheTVDB is not configured on this metadata service." }, 503);
  }

  let key;
  let ttl;
  let load;
  switch (route.kind) {
    case "tvdb-search": {
      const lookup = parseTvdbSearch(url.searchParams);
      if (lookup.error) {
        return json({ error: "invalid_request", message: lookup.error }, 400);
      }
      key = tvdbCacheKey("search", `${lookup.value.query}|${lookup.value.year ?? ""}`);
      ttl = TVDB_SEARCH_TTL_SECONDS;
      load = () => searchTvdb(lookup.value, env, request);
      break;
    }
    case "tvdb-series":
      key = tvdbCacheKey("series", route.id);
      ttl = TVDB_SERIES_TTL_SECONDS;
      load = () => lookupTvdbSeries(route.id, env, request);
      break;
    case "tvdb-translation":
      key = tvdbCacheKey("translation", `${route.id}|${route.language}`);
      ttl = TVDB_TRANSLATION_TTL_SECONDS;
      load = () => lookupTvdbTranslation(route.id, route.language, env, request);
      break;
    case "tvdb-catalogue":
      key = tvdbCacheKey("catalogue", route.id);
      ttl = TVDB_CATALOGUE_TTL_SECONDS;
      load = () => lookupTvdbCatalogue(route.id, env, request);
      break;
    case "tvdb-episode-groups":
      key = tvdbCacheKey("orders", route.id);
      ttl = TVDB_ORDERS_TTL_SECONDS;
      load = () => lookupTvdbEpisodeGroups(route.id, env, request);
      break;
    default:
      key = tvdbCacheKey("remote", route.id);
      ttl = TVDB_REMOTE_ID_TTL_SECONDS;
      load = () => lookupTvdbRemote(route.id, env, request);
  }

  const cached = await cache.get(key, "json");
  if (cached) {
    return json(cached, 200, { "Cache-Control": "public, max-age=600" });
  }

  try {
    const payload = await load();
    await cache.put(key, JSON.stringify(payload), { expirationTtl: ttl });
    return json(payload, 200, { "Cache-Control": "public, max-age=600" });
  } catch (error) {
    const status = error instanceof TvdbRequestError ? error.status : 502;
    if (status === 404) {
      return json({ error: "not_found" }, 404);
    }
    if (status === 429) {
      return json({ error: "provider_busy", message: "TV metadata is busy. Please try again shortly." }, 503, { "Retry-After": "60" });
    }
    return json({ error: "provider_unavailable", message: "TV metadata is temporarily unavailable." }, 503);
  }
}
