/**
 * A title has no lifecycle status of its own.
 *
 * `MediaStatus` used to live here — eleven values, of which a `MediaItem` could
 * only ever hold two: `downloaded` and `missing`, both derived from `hasFile`
 * alone. The other nine described a *transfer*, not a title, and nothing ever
 * set them on one. Between them and `MEDIA_STATUS_PRESENTATION` they were a
 * second colouring of states the one table already answers for, and they
 * coloured a missing title amber — the signal that is supposed to mean a person
 * is needed (#302).
 *
 * What a title is doing now reads from `wantedStatus` and the episode counts
 * below, through `titleMark()` in `lib/status-tones.ts`. What a transfer is
 * doing reads from `STATUS_PRESENTATION`. Neither borrows the other's words.
 */
export type MediaType = "movie" | "show";

export interface MediaItem {
  id: string;
  title: string;
  year: number | null;
  type: MediaType;
  poster: string | null;
  backdrop: string | null;
  quality: string | null;
  monitored: boolean;
  sizeGb: number | null;
  rating: number | null;
  ratings?: Array<{
    source: string;
    label: string;
    score: number | null;
    maxScore: number | null;
    voteCount: number | null;
    url: string | null;
    kind: string | null;
  }>;
  genres: string[];
  added: string;
  overview: string;
  network?: string;
  libraryId?: string;
  wantedReason?: string;
  lastSearchUtc?: string | null;
  nextEligibleSearchUtc?: string | null;
  currentQuality?: string | null;
  targetQuality?: string | null;
  bitrateMbps?: number | null;
  releaseGroup?: string | null;
  tags?: string[];
  source?: string | null;
  codec?: string | null;
  audioCodec?: string | null;
  audioChannels?: string | null;
  language?: string | null;
  hdrFormat?: string | null;
  releaseStatus?: string | null;
  /**
   * The stored wanted status — `missing`, `upgrade`, `covered` or `upcoming`.
   * The mark on the poster is derived from this and the episode counts below,
   * never from `status`, which only says whether a file exists.
   */
  wantedStatus?: string | null;
  /**
   * What a show's episodes add up to, counted over what has aired.
   *
   * These decide a show's **dot** — the lowest rung any aired episode is on —
   * and `airedWithFileCount` also says how many files the show has, which is
   * what its subtitle bar is measured over. They are not drawn on a poster
   * themselves; the show's own page carries the counts. A movie leaves them
   * undefined.
   */
  episodeCount?: number;
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
  airedUpgradableCount?: number;
  nextAirDateUtc?: string | null;
  /**
   * Whether the title itself is on disk.
   *
   * Not a state — the mark says the state. It is here because the subtitle bar
   * is measured over the files a title actually has, and a movie with no file
   * holds no subtitles to be short of.
   */
  hasFile?: boolean;
  /** Languages asked for **per file**, and how many are held across them all. */
  subtitleLanguagesWanted?: number;
  subtitleLanguagesHeld?: number;
  certification?: string | null;
  collection?: string | null;
  minimumAvailability?: string | null;
  consideredAvailable?: boolean | null;
  digitalRelease?: string | null;
  physicalRelease?: string | null;
  releaseDate?: string | null;
  inCinemas?: string | null;
  originalLanguage?: string | null;
  originalTitle?: string | null;
  path?: string | null;
  qualityProfile?: string | null;
  runtimeMinutes?: number | null;
  studio?: string | null;
  tmdbRating?: number | null;
  tmdbVotes?: number | null;
  imdbRating?: number | null;
  imdbVotes?: number | null;
  traktRating?: number | null;
  traktVotes?: number | null;
  tomatoRating?: number | null;
  tomatoVotes?: number | null;
  popularity?: number | null;
  keywords?: string[];
}

export interface ActiveDownload {
  id: string;
  title: string;
  poster: string | null;
  quality: string | null;
  progress: number;
  speedMbps: number;
  etaMinutes: number;
  peers: number;
  indexer: string;
}

export interface IndexerHealthItem {
  id: string;
  name: string;
  status: "healthy" | "degraded" | "down";
  responseMs: number | null;
}
