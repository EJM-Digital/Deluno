import type {
  DownloadClientItem,
  DownloadDispatchItem,
  DownloadTelemetryOverview,
  IndexerItem,
  MovieListItem,
  MovieWantedSummary,
  SeriesListItem,
  SeriesWantedSummary
} from "./api";
import type { ActiveDownload, IndexerHealthItem, MediaItem } from "./media-types";

function hashValue(value: string) {
  let hash = 0;
  for (const char of value) {
    hash = (hash << 5) - hash + char.charCodeAt(0);
    hash |= 0;
  }
  return Math.abs(hash);
}

function splitGenres(value: string | null | undefined) {
  const parsed = (value ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
  return parsed;
}

function parseMetadataJson(value: string | null | undefined): Record<string, unknown> {
  if (!value) return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function readString(meta: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = meta[key];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return null;
}

function readNumber(meta: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = meta[key];
    if (typeof value === "number" && Number.isFinite(value)) return value;
    if (typeof value === "string") {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) return parsed;
    }
  }
  return null;
}

function readStringArray(meta: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = meta[key];
    if (Array.isArray(value)) {
      return value.filter((item): item is string => typeof item === "string" && item.trim().length > 0);
    }
    if (typeof value === "string" && value.trim()) {
      return value.split(",").map((item) => item.trim()).filter(Boolean);
    }
  }
  return [];
}

export function adaptMovieItems(items: MovieListItem[], wanted: MovieWantedSummary): MediaItem[] {
  const wantedMap = new Map(wanted.recentItems.map((item) => [item.movieId, item]));

  return items.map((item) => {
    const wantedItem = wantedMap.get(item.id);
    const meta = parseMetadataJson(item.metadataJson);
    const genres = splitGenres(item.genres);

    return {
      id: item.id,
      title: item.title,
      year: item.releaseYear ?? new Date(item.createdUtc).getFullYear(),
      type: "movie",
      poster: item.posterUrl,
      backdrop: item.backdropUrl,
      quality: wantedItem?.currentQuality ?? wantedItem?.targetQuality ?? null,
      status:
        wantedItem?.wantedStatus === "missing"
          ? "missing"
          : wantedItem?.wantedStatus === "waiting"
            ? "downloading"
            : item.monitored
              ? "downloaded"
              : "monitored",
      monitored: item.monitored,
      sizeGb: readNumber(meta, "sizeGb", "sizeGB", "sizeOnDiskGb"),
      rating: item.rating,
      genres,
      added: new Date(item.createdUtc).toLocaleDateString([], { month: "short", day: "numeric" }),
      overview: item.overview ?? `${item.title} is tracked inside Deluno with live search state, monitoring, and acquisition history.`,
      libraryId: wantedItem?.libraryId,
      wantedReason: wantedItem?.wantedReason,
      lastSearchUtc: wantedItem?.lastSearchUtc,
      nextEligibleSearchUtc: wantedItem?.nextEligibleSearchUtc,
      currentQuality: wantedItem?.currentQuality,
      targetQuality: wantedItem?.targetQuality,
      bitrateMbps: readNumber(meta, "bitrateMbps", "bitrate"),
      releaseGroup: readString(meta, "releaseGroup"),
      tags: readStringArray(meta, "tags"),
      source: readString(meta, "source"),
      codec: readString(meta, "codec", "videoCodec"),
      audioCodec: readString(meta, "audioCodec"),
      audioChannels: readString(meta, "audioChannels"),
      language: readString(meta, "language"),
      hdrFormat: readString(meta, "hdrFormat"),
      releaseStatus: wantedItem?.wantedStatus ?? (item.monitored ? "Available" : "Monitored"),
      certification: readString(meta, "certification"),
      collection: readString(meta, "collection"),
      minimumAvailability: readString(meta, "minimumAvailability"),
      consideredAvailable: null,
      digitalRelease: readString(meta, "digitalRelease"),
      physicalRelease: readString(meta, "physicalRelease"),
      releaseDate: readString(meta, "releaseDate"),
      inCinemas: readString(meta, "inCinemas"),
      originalLanguage: readString(meta, "originalLanguage"),
      originalTitle: item.originalTitle ?? item.title,
      path: readString(meta, "path"),
      qualityProfile: readString(meta, "qualityProfile"),
      runtimeMinutes: readNumber(meta, "runtimeMinutes", "runtime"),
      studio: readString(meta, "studio"),
      tmdbRating: item.rating,
      tmdbVotes: readNumber(meta, "tmdbVotes"),
      imdbRating: readNumber(meta, "imdbRating"),
      imdbVotes: readNumber(meta, "imdbVotes"),
      traktRating: readNumber(meta, "traktRating"),
      traktVotes: readNumber(meta, "traktVotes"),
      tomatoRating: readNumber(meta, "tomatoRating"),
      tomatoVotes: readNumber(meta, "tomatoVotes"),
      popularity: readNumber(meta, "popularity"),
      keywords: readStringArray(meta, "keywords")
    };
  });
}

export function adaptSeriesItems(items: SeriesListItem[], wanted: SeriesWantedSummary): MediaItem[] {
  const wantedMap = new Map(wanted.recentItems.map((item) => [item.seriesId, item]));

  return items.map((item) => {
    const wantedItem = wantedMap.get(item.id);
    const meta = parseMetadataJson(item.metadataJson);
    const genres = splitGenres(item.genres);

    return {
      id: item.id,
      title: item.title,
      year: item.startYear ?? new Date(item.createdUtc).getFullYear(),
      type: "show",
      poster: item.posterUrl,
      backdrop: item.backdropUrl,
      quality: wantedItem?.currentQuality ?? wantedItem?.targetQuality ?? null,
      status:
        wantedItem?.wantedStatus === "missing"
          ? "missing"
          : wantedItem?.wantedStatus === "waiting"
            ? "downloading"
            : item.monitored
              ? "downloaded"
              : "monitored",
      monitored: item.monitored,
      sizeGb: readNumber(meta, "sizeGb", "sizeGB", "sizeOnDiskGb"),
      rating: item.rating,
      genres,
      added: new Date(item.createdUtc).toLocaleDateString([], { month: "short", day: "numeric" }),
      overview: item.overview ?? `${item.title} is tracked inside Deluno with episode inventory, wanted state, and acquisition context.`,
      network: undefined,
      libraryId: wantedItem?.libraryId,
      wantedReason: wantedItem?.wantedReason,
      lastSearchUtc: wantedItem?.lastSearchUtc,
      nextEligibleSearchUtc: wantedItem?.nextEligibleSearchUtc,
      currentQuality: wantedItem?.currentQuality,
      targetQuality: wantedItem?.targetQuality,
      bitrateMbps: readNumber(meta, "bitrateMbps", "bitrate"),
      releaseGroup: readString(meta, "releaseGroup"),
      tags: readStringArray(meta, "tags"),
      source: readString(meta, "source"),
      codec: readString(meta, "codec", "videoCodec"),
      audioCodec: readString(meta, "audioCodec"),
      audioChannels: readString(meta, "audioChannels"),
      language: readString(meta, "language"),
      hdrFormat: readString(meta, "hdrFormat"),
      releaseStatus: wantedItem?.wantedStatus ?? (item.monitored ? "Available" : "Monitored"),
      certification: readString(meta, "certification"),
      collection: readString(meta, "collection"),
      minimumAvailability: readString(meta, "minimumAvailability"),
      consideredAvailable: null,
      digitalRelease: readString(meta, "digitalRelease"),
      physicalRelease: readString(meta, "physicalRelease"),
      releaseDate: readString(meta, "releaseDate"),
      inCinemas: readString(meta, "inCinemas"),
      originalLanguage: readString(meta, "originalLanguage"),
      originalTitle: item.originalTitle ?? item.title,
      path: readString(meta, "path"),
      qualityProfile: readString(meta, "qualityProfile"),
      runtimeMinutes: readNumber(meta, "runtimeMinutes", "runtime"),
      studio: readString(meta, "studio"),
      tmdbRating: item.rating,
      tmdbVotes: readNumber(meta, "tmdbVotes"),
      imdbRating: readNumber(meta, "imdbRating"),
      imdbVotes: readNumber(meta, "imdbVotes"),
      traktRating: readNumber(meta, "traktRating"),
      traktVotes: readNumber(meta, "traktVotes"),
      tomatoRating: readNumber(meta, "tomatoRating"),
      tomatoVotes: readNumber(meta, "tomatoVotes"),
      popularity: readNumber(meta, "popularity"),
      keywords: readStringArray(meta, "keywords")
    };
  });
}

export function adaptActiveDownloads(dispatches: DownloadDispatchItem[]): ActiveDownload[] {
  return dispatches.slice(0, 4).map((item, index) => {
    const hash = hashValue(item.releaseName);
    return {
      id: item.id,
      title: item.releaseName,
      poster: null,
      quality: hash % 2 === 0 ? "WEB-DL 2160p" : "Bluray-1080p",
      progress: 22 + ((hash + index * 13) % 68),
      speedMbps: Number((8 + ((hash % 240) / 10)).toFixed(1)),
      etaMinutes: 4 + (hash % 28),
      peers: 6 + (hash % 60),
      indexer: item.indexerName
    };
  });
}

export function adaptTelemetryDownloads(telemetry: DownloadTelemetryOverview): ActiveDownload[] {
  return telemetry.clients
    .flatMap((client) => client.queue)
    .filter((item) => item.status === "downloading" || item.status === "queued" || item.status === "importReady")
    .sort((a, b) => {
      if (a.status === b.status) {
        return new Date(b.addedUtc).getTime() - new Date(a.addedUtc).getTime();
      }

      const rank = { downloading: 0, importReady: 1, queued: 2 } as Record<string, number>;
      return (rank[a.status] ?? 9) - (rank[b.status] ?? 9);
    })
    .slice(0, 6)
    .map((item) => {
      return {
        id: item.id,
        title: item.title || item.releaseName,
        poster: null,
        quality: item.category || item.protocol,
        progress: Math.round(item.progress),
        speedMbps: item.speedMbps,
        etaMinutes: Math.max(0, Math.ceil(item.etaSeconds / 60)),
        peers: item.peers,
        indexer: `${item.indexerName} -> ${item.clientName}`
      };
    });
}

export function adaptIndexerHealth(
  indexers: IndexerItem[],
  clients: DownloadClientItem[]
): IndexerHealthItem[] {
  const sources = indexers.map((item) => ({
    id: item.id,
    name: item.name,
    status:
      item.healthStatus === "ready"
        ? ("healthy" as const)
        : item.healthStatus === "attention"
          ? ("degraded" as const)
          : ("down" as const),
    responseMs: null
  }));

  const clientItems = clients.slice(0, 2).map((item) => ({
    id: item.id,
    name: item.name,
    status:
      item.healthStatus === "ready"
        ? ("healthy" as const)
        : item.healthStatus === "attention"
          ? ("degraded" as const)
          : ("down" as const),
    responseMs: null
  }));

  return [...sources, ...clientItems].slice(0, 6);
}
