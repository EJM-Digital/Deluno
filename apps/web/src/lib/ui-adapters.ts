import type {
  DownloadClientItem,
  DownloadDispatchItem,
  DownloadTelemetryOverview,
  IndexerItem,
  MovieListItem,
  SeriesListItem
} from "./api";
import { downloadQueueStatuses } from "./download-telemetry";
import { wantedStatusPresentation } from "./media-status-presentation";
import type { ActiveDownload, IndexerHealthItem, MediaItem, MediaStatus } from "./media-types";

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

function readRating(
  item: { ratings?: Array<{ source: string; score: number | null; maxScore: number | null }> | null },
  source: string,
  fallback: number | null
) {
  const rating = item.ratings?.find((entry) => entry.source === source && typeof entry.score === "number");
  if (!rating || rating.score === null) return fallback;
  if (rating.maxScore === 100) return rating.score;
  return rating.score;
}

/**
 * The availability chip on a media card: does Deluno have the file, or not.
 *
 * Nothing else belongs in it. It used to read the wanted status first and show
 * "Downloading" for anything `waiting`, which was wrong three ways over: the
 * server sets `waiting` on a film that *has* a file and is already at or above
 * target quality, this file's own WANTED_STATUS_PRESENTATION describes it as
 * "not searchable yet", and neither of those is downloading. Because it was
 * tested before `hasFile`, a film that had been imported and verified showed on
 * its card as still coming down.
 *
 * Monitoring is likewise a separate automation preference and must never become
 * the visible availability state: a missing monitored title stays Missing.
 *
 * Why no "Downloading" here at all: this adapter is fed the catalogue, which
 * carries no live transfer state. Showing progress on a card needs the download
 * telemetry wired in, not a wanted status pressed into service as a stand-in.
 */
/**
 * What the Release status filter offers, in the words the rest of the UI uses.
 *
 * It used to answer "Downloading" for anything `waiting` — the last of the
 * three places #299 found, and the one it did not fix, because the sibling line
 * above it was the visible symptom. The server sets `waiting` on a title that
 * *has* a file and is at or above target (#300), so filtering for Downloading
 * returned exactly the finished titles.
 *
 * The rest of it mixed vocabularies too: raw engine tokens for a tracked title,
 * human words for an untracked one. Now every value is a label the user has
 * already seen on the title itself, and the fallback claims only what a missing
 * wanted record can support — whether the file is there.
 */
function releaseStatusLabel(wantedStatus: string | undefined, hasFile: boolean) {
  if (wantedStatus) return wantedStatusPresentation(wantedStatus).label;
  return hasFile ? "On disk" : "Missing";
}

function mediaAvailabilityStatus(hasFile: boolean): MediaStatus {
  return hasFile ? "downloaded" : "missing";
}

/**
 * The catalogue page, in the shape the grid draws.
 *
 * It used to take the wanted summary alongside the page and look each title up
 * in it. That summary's `recentItems` is `LIMIT 25`, so in a library of any
 * size the lookup missed: past the first 25 titles every card lost its status,
 * its reason, its target quality and its library and fell back to "is there a
 * file". It looked right on a rig of eleven films and would have gone quietly
 * wrong at twenty thousand. The page carries its own search state now, so the
 * twenty-thousandth card says as much as the first.
 */
export function adaptMovieItems(items: MovieListItem[]): MediaItem[] {
  return items.map((item) => {
    const meta = parseMetadataJson(item.metadataJson);
    const genres = splitGenres(item.genres);

    return {
      id: item.id,
      title: item.title,
      year: item.releaseYear ?? null,
      type: "movie",
      poster: item.posterUrl,
      backdrop: item.backdropUrl,
      quality: item.currentQuality ?? item.targetQuality ?? null,
      status: mediaAvailabilityStatus(item.hasFile),
      monitored: item.monitored,
      sizeGb: item.fileSizeBytes != null ? item.fileSizeBytes / 1024 / 1024 / 1024 : readNumber(meta, "sizeGb", "sizeGB", "sizeOnDiskGb"),
      rating: item.rating,
      ratings: item.ratings ?? [],
      genres,
      added: new Date(item.createdUtc).toLocaleDateString([], { month: "short", day: "numeric" }),
      overview: item.overview ?? `${item.title} is tracked inside Deluno with live search state, monitoring, and acquisition history.`,
      libraryId: item.libraryId ?? undefined,
      wantedReason: item.wantedReason ?? undefined,
      lastSearchUtc: item.lastSearchUtc ?? undefined,
      nextEligibleSearchUtc: item.nextEligibleSearchUtc ?? undefined,
      currentQuality: item.currentQuality ?? undefined,
      targetQuality: item.targetQuality ?? undefined,
      bitrateMbps: item.approximateBitrateMbps ?? readNumber(meta, "bitrateMbps", "bitrate"),
      releaseGroup: item.releaseGroup ?? readString(meta, "releaseGroup"),
      tags: readStringArray(meta, "tags"),
      source: readString(meta, "source"),
      codec: item.videoCodec ?? readString(meta, "codec", "videoCodec"),
      audioCodec: item.audioCodec ?? readString(meta, "audioCodec"),
      audioChannels: item.audioChannels ?? readString(meta, "audioChannels"),
      language: readString(meta, "language"),
      hdrFormat: readString(meta, "hdrFormat"),
      releaseStatus: releaseStatusLabel(item.wantedStatus ?? undefined, item.hasFile),
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
      path: item.filePath ?? readString(meta, "path"),
      qualityProfile: readString(meta, "qualityProfile"),
      runtimeMinutes: item.runtimeMinutes ?? readNumber(meta, "runtimeMinutes", "runtime"),
      studio: readString(meta, "studio"),
      tmdbRating: readRating(item, "tmdb", item.rating),
      tmdbVotes: item.voteCount ?? readNumber(meta, "tmdbVotes"),
      imdbRating: readRating(item, "imdb", readNumber(meta, "imdbRating")),
      imdbVotes: readNumber(meta, "imdbVotes"),
      traktRating: readNumber(meta, "traktRating"),
      traktVotes: readNumber(meta, "traktVotes"),
      tomatoRating: readRating(item, "rotten_tomatoes", readNumber(meta, "tomatoRating")),
      tomatoVotes: readNumber(meta, "tomatoVotes"),
      popularity: item.popularity ?? readNumber(meta, "popularity"),
      keywords: readStringArray(meta, "keywords")
    };
  });
}

/** The series half of {@link adaptMovieItems}, and the same reason for it. */
export function adaptSeriesItems(items: SeriesListItem[]): MediaItem[] {
  return items.map((item) => {
    const meta = parseMetadataJson(item.metadataJson);
    const genres = splitGenres(item.genres);

    return {
      id: item.id,
      title: item.title,
      year: item.startYear ?? null,
      type: "show",
      poster: item.posterUrl,
      backdrop: item.backdropUrl,
      quality: item.currentQuality ?? item.targetQuality ?? null,
      status: mediaAvailabilityStatus(item.hasFile),
      monitored: item.monitored,
      sizeGb: item.fileSizeBytes != null ? item.fileSizeBytes / 1024 / 1024 / 1024 : readNumber(meta, "sizeGb", "sizeGB", "sizeOnDiskGb"),
      rating: item.rating,
      ratings: item.ratings ?? [],
      genres,
      added: new Date(item.createdUtc).toLocaleDateString([], { month: "short", day: "numeric" }),
      overview: item.overview ?? `${item.title} is tracked inside Deluno with episode inventory, wanted state, and acquisition context.`,
      network: undefined,
      libraryId: item.libraryId ?? undefined,
      wantedReason: item.wantedReason ?? undefined,
      lastSearchUtc: item.lastSearchUtc ?? undefined,
      nextEligibleSearchUtc: item.nextEligibleSearchUtc ?? undefined,
      currentQuality: item.currentQuality ?? undefined,
      targetQuality: item.targetQuality ?? undefined,
      bitrateMbps: item.approximateBitrateMbps ?? readNumber(meta, "bitrateMbps", "bitrate"),
      releaseGroup: item.releaseGroup ?? readString(meta, "releaseGroup"),
      tags: readStringArray(meta, "tags"),
      source: readString(meta, "source"),
      codec: item.videoCodec ?? readString(meta, "codec", "videoCodec"),
      audioCodec: item.audioCodec ?? readString(meta, "audioCodec"),
      audioChannels: item.audioChannels ?? readString(meta, "audioChannels"),
      language: readString(meta, "language"),
      hdrFormat: readString(meta, "hdrFormat"),
      releaseStatus: releaseStatusLabel(item.wantedStatus ?? undefined, item.hasFile),
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
      path: item.filePath ?? readString(meta, "path"),
      qualityProfile: readString(meta, "qualityProfile"),
      runtimeMinutes: item.runtimeMinutes ?? readNumber(meta, "runtimeMinutes", "runtime"),
      studio: readString(meta, "studio"),
      tmdbRating: readRating(item, "tmdb", item.rating),
      tmdbVotes: item.voteCount ?? readNumber(meta, "tmdbVotes"),
      imdbRating: readRating(item, "imdb", readNumber(meta, "imdbRating")),
      imdbVotes: readNumber(meta, "imdbVotes"),
      traktRating: readNumber(meta, "traktRating"),
      traktVotes: readNumber(meta, "traktVotes"),
      tomatoRating: readRating(item, "rotten_tomatoes", readNumber(meta, "tomatoRating")),
      tomatoVotes: readNumber(meta, "tomatoVotes"),
      popularity: item.popularity ?? readNumber(meta, "popularity"),
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
    .filter((item) => item.status === downloadQueueStatuses.downloading || item.status === downloadQueueStatuses.queued || item.status === downloadQueueStatuses.importReady)
    .sort((a, b) => {
      if (a.status === b.status) {
        return new Date(b.addedUtc).getTime() - new Date(a.addedUtc).getTime();
      }

      const rank = {
        [downloadQueueStatuses.downloading]: 0,
        [downloadQueueStatuses.importReady]: 1,
        [downloadQueueStatuses.queued]: 2
      } as Record<string, number>;
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
    status: normalizeIntegrationHealth(item.healthStatus),
    responseMs: item.lastHealthLatencyMs ?? null
  }));

  const clientItems = clients.map((item) => ({
    id: item.id,
    name: item.name,
    status: normalizeIntegrationHealth(item.healthStatus),
    responseMs: item.lastHealthLatencyMs ?? null
  }));

  // Dashboard health totals must account for every configured connection.
  // Individual panels choose how many rows to display; the aggregate must not
  // quietly ignore clients beyond an arbitrary limit.
  return [...sources, ...clientItems];
}

function normalizeIntegrationHealth(status: string): "healthy" | "degraded" | "down" {
  if (status === "healthy") return "healthy";
  if (status === "degraded" || status === "untested") return "degraded";
  return "down";
}
