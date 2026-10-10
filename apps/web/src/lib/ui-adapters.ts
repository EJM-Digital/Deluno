import type {
  DownloadClientItem,
  DownloadDispatchItem,
  DownloadTelemetryOverview,
  IndexerItem,
  MovieListItem,
  SeriesListItem
} from "./api";
import { downloadQueueStatuses } from "./download-telemetry";
import { wantedStatusPresentation } from "./status-tones";
import type { ActiveDownload, IndexerHealthItem, MediaItem } from "./media-types";
import { floorProgress } from "./progress-format";

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

/**
 * The stored provider blob, with its keys folded to lower case.
 *
 * **This is a fix, and the defect it fixes was total.** `metadata_json` is
 * `JsonSerializer.Serialize(match)` with default options, so it is
 * *PascalCase* — `"Certification"`, `"Studio"`, `"OriginalLanguage"`. Every
 * reader below asked for camelCase and did an exact-key lookup, so every one of
 * them returned null for every title on every install, always.
 *
 * That is not one field. It is certification, collection, studio, language,
 * path, quality profile, tags, source, HDR format, release dates, and the
 * external ratings and vote counts — a whole family of things the library list
 * and both detail pages display, reading keys that were never in the payload.
 * The columns simply looked like the provider had not sent anything.
 *
 * Folding the case here rather than changing the serializer fixes every blob
 * already on disk without a migration, and keeps working if the server ever
 * does switch to camelCase. Callers still ask in camelCase, which is what the
 * rest of the front end speaks; only the lookup is case-blind.
 */
function parseMetadataJson(value: string | null | undefined): Record<string, unknown> {
  if (!value) return {};
  try {
    const parsed = JSON.parse(value);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {};

    const folded: Record<string, unknown> = {};
    for (const [key, entry] of Object.entries(parsed as Record<string, unknown>)) {
      folded[key.toLowerCase()] = entry;
    }
    return folded;
  } catch {
    return {};
  }
}

function readString(meta: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = meta[key.toLowerCase()];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return null;
}

function readNumber(meta: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = meta[key.toLowerCase()];
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
    const value = meta[key.toLowerCase()];
    if (Array.isArray(value)) {
      return value.filter((item): item is string => typeof item === "string" && item.trim().length > 0);
    }
    if (typeof value === "string" && value.trim()) {
      return value.split(",").map((item) => item.trim()).filter(Boolean);
    }
  }
  return [];
}

/**
 * The region-specific spellings TMDb's alternative_titles endpoint reported
 * for a title, read back out of the stored metadata blob (#861) — the
 * frontend twin of `CatalogueMetadata.ReadAlternateTitles`, which is what
 * `AlternateTitles` rides along inside on the way in. Kept here, one place,
 * on the same `parseMetadataJson`/`readStringArray` pair every other stored
 * field goes through, for the same reason that one is one place: the movie
 * and show detail pages would otherwise each grow their own copy of "parse
 * the blob, read this one field out of it".
 */
export function readAlternateTitles(metadataJson: string | null | undefined): string[] {
  return readStringArray(parseMetadataJson(metadataJson), "alternateTitles");
}

/**
 * A transparent logo/wordmark and extra backdrop stills beyond the poster
 * and backdrop already shown (#864). `bannerUrl` is deliberately not read
 * here: TMDb has never reported one (see the comment on
 * `MetadataSearchResult.BannerUrl`), so there is nothing this would ever
 * show, and a field that always renders empty is worse than one left out.
 */
export function readArtworkExtras(metadataJson: string | null | undefined) {
  const meta = parseMetadataJson(metadataJson);
  return {
    logoUrl: readString(meta, "logoUrl"),
    screenshots: readStringArray(meta, "screenshots")
  };
}

/** One country's release dates and certification, exactly as TMDb reported them (#862). */
export interface CountryReleaseFacts {
  country: string;
  inCinemas: string | null;
  digital: string | null;
  physical: string | null;
  certification: string | null;
}

/**
 * Every country TMDb answered for, read back out of the stored metadata blob
 * (#862) — the frontend twin of the backend's `MetadataCountryFacts`. The top
 * level of the blob is case-folded by `parseMetadataJson`, but the objects
 * inside this array are not (that fold only walks one level), so the field
 * names within each entry are read directly, PascalCase, the same as every
 * other nested shape `JsonSerializer.Serialize` produced them in.
 */
export function readReleaseFactsByCountry(metadataJson: string | null | undefined): CountryReleaseFacts[] {
  const meta = parseMetadataJson(metadataJson);
  const raw = meta["releasefactsbycountry"];
  if (!Array.isArray(raw)) return [];

  const text = (entry: Record<string, unknown>, key: string) => {
    const value = entry[key];
    return typeof value === "string" && value.trim() ? value.trim() : null;
  };

  return raw
    .map((entry) => (entry && typeof entry === "object" ? (entry as Record<string, unknown>) : null))
    .filter((entry): entry is Record<string, unknown> => entry !== null)
    .map((entry) => ({
      country: text(entry, "Country") ?? "",
      inCinemas: text(entry, "InCinemas"),
      digital: text(entry, "Digital"),
      physical: text(entry, "Physical"),
      certification: text(entry, "Certification")
    }))
    .filter((entry) => entry.country.length > 0);
}

/**
 * Which of the stored countries' facts to surface for a chosen certification
 * country (#862) — a pure pick over data already on hand, so changing the
 * setting changes what is shown without re-fetching TMDb. Falls back to US
 * the same way the backend's own certification resolution does, then to
 * whatever was stored first rather than showing nothing.
 */
export function selectCountryFacts(facts: CountryReleaseFacts[], country: string): CountryReleaseFacts | null {
  const wanted = country.trim().toUpperCase();
  return (
    facts.find((entry) => entry.country.toUpperCase() === wanted) ??
    facts.find((entry) => entry.country.toUpperCase() === "US") ??
    facts[0] ??
    null
  );
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

/**
 * The catalogue page, in the shape the grid draws.
 *
 * It used to take the wanted summary alongside the page and look each title up
 * in it. That summary's `recentItems` is `LIMIT 25`, so in a library of any
 * size the lookup missed: past the first 25 titles every card lost its status,
 * its reason, its target quality and its library and fell back to "is there a
 * file". It looked right on a rig of eleven movies and would have gone quietly
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
      monitored: item.monitored,
      sizeGb: item.fileSizeBytes != null ? item.fileSizeBytes / 1024 / 1024 / 1024 : readNumber(meta, "sizeGb", "sizeGB", "sizeOnDiskGb"),
      rating: item.rating,
      ratings: item.ratings ?? [],
      genres,
      added: new Date(item.createdUtc).toLocaleDateString([], { month: "short", day: "numeric" }),
      addedUtc: item.createdUtc,
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
      language: readString(meta, "originalLanguage", "language"),
      hdrFormat: readString(meta, "hdrFormat"),
      releaseStatus: releaseStatusLabel(item.wantedStatus ?? undefined, item.hasFile),
      wantedStatus: item.wantedStatus ?? undefined,
      // The subtitle bar is measured over the files a title has, so whether
      // there is one has to travel with it. It is not a state — the mark is.
      hasFile: item.hasFile,
      subtitleLanguagesWanted: item.subtitleLanguagesWanted,
      subtitleLanguagesHeld: item.subtitleLanguagesHeld,
      subtitleLanguagesSettled: item.subtitleLanguagesSettled,
      certification: readString(meta, "certification"),
      collection: readString(meta, "collection"),
      minimumAvailability: readString(meta, "minimumAvailability"),
      consideredAvailable: null,
      // From the item's own columns, with the blob only as a fallback. These
      // read the metadata blob alone until now, and the catalogue page projects
      // NULL for it deliberately — so all four were empty on every shelf, the
      // same shape as the ratings beside them. The columns have existed since
      // V0007.
      digitalRelease: item.digitalReleaseDate ?? readString(meta, "digitalRelease"),
      physicalRelease: item.physicalReleaseDate ?? readString(meta, "physicalRelease"),
      releaseDate: readString(meta, "releaseDate"),
      inCinemas: item.inCinemasDate ?? readString(meta, "inCinemas"),
      originalLanguage: readString(meta, "originalLanguage"),
      originalTitle: item.originalTitle ?? item.title,
      localizedTitle: readString(meta, "localizedTitle"),
      path: item.filePath ?? readString(meta, "path"),
      qualityProfile: readString(meta, "qualityProfile"),
      runtimeMinutes: item.runtimeMinutes ?? readNumber(meta, "runtimeMinutes", "runtime"),
      studio: readString(meta, "studio"),
      tmdbRating: readRating(item, "tmdb", item.rating),
      tmdbVotes: item.voteCount ?? readNumber(meta, "tmdbVotes"),
      imdbRating: readRating(item, "imdb", readNumber(meta, "imdbRating")),
      imdbVotes: readNumber(meta, "imdbVotes"),
      tomatoRating: readRating(item, "rotten_tomatoes", readNumber(meta, "tomatoRating")),
      tomatoVotes: readNumber(meta, "tomatoVotes"),
      metacriticRating: readRating(item, "metacritic", readNumber(meta, "metacriticRating")),
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
      monitored: item.monitored,
      sizeGb: item.fileSizeBytes != null ? item.fileSizeBytes / 1024 / 1024 / 1024 : readNumber(meta, "sizeGb", "sizeGB", "sizeOnDiskGb"),
      rating: item.rating,
      ratings: item.ratings ?? [],
      genres,
      added: new Date(item.createdUtc).toLocaleDateString([], { month: "short", day: "numeric" }),
      addedUtc: item.createdUtc,
      overview: item.overview ?? `${item.title} is tracked inside Deluno with episode inventory, wanted state, and acquisition context.`,
      // Hardcoded to undefined because nothing ever sent one. The broker does
      // now — it is what Sonarr lets you organise a library by.
      network: readString(meta, "network") ?? undefined,
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
      language: readString(meta, "originalLanguage", "language"),
      hdrFormat: readString(meta, "hdrFormat"),
      releaseStatus: releaseStatusLabel(item.wantedStatus ?? undefined, item.hasFile),
      wantedStatus: item.wantedStatus ?? undefined,
      hasFile: item.hasFile,
      // A show's mark is the lowest rung any *aired* episode is on, so the
      // counts travel with it. `airedWithFileCount` is also how many files the
      // show has, which is what its subtitle bar is measured over.
      episodeCount: item.episodeCount,
      airedEpisodeCount: item.airedEpisodeCount,
      airedWithFileCount: item.airedWithFileCount,
      airedUpgradableCount: item.airedUpgradableCount,
      nextAirDateUtc: item.nextAirDateUtc ?? null,
      subtitleLanguagesWanted: item.subtitleLanguagesWanted,
      subtitleLanguagesHeld: item.subtitleLanguagesHeld,
      subtitleLanguagesSettled: item.subtitleLanguagesSettled,
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
      localizedTitle: readString(meta, "localizedTitle"),
      path: item.filePath ?? readString(meta, "path"),
      qualityProfile: readString(meta, "qualityProfile"),
      runtimeMinutes: item.runtimeMinutes ?? readNumber(meta, "runtimeMinutes", "runtime"),
      studio: readString(meta, "studio"),
      tmdbRating: readRating(item, "tmdb", item.rating),
      tmdbVotes: item.voteCount ?? readNumber(meta, "tmdbVotes"),
      imdbRating: readRating(item, "imdb", readNumber(meta, "imdbRating")),
      imdbVotes: readNumber(meta, "imdbVotes"),
      tomatoRating: readRating(item, "rotten_tomatoes", readNumber(meta, "tomatoRating")),
      tomatoVotes: readNumber(meta, "tomatoVotes"),
      metacriticRating: readRating(item, "metacritic", readNumber(meta, "metacriticRating")),
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
        progress: floorProgress(item.progress),
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
