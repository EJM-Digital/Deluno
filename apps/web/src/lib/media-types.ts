export type MediaStatus = "downloaded" | "downloading" | "missing" | "monitored";
export type MediaType = "movie" | "show";

export interface MediaItem {
  id: string;
  title: string;
  year: number | null;
  type: MediaType;
  poster: string | null;
  backdrop: string | null;
  quality: string | null;
  status: MediaStatus;
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
