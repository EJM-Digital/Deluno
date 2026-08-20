export interface MetadataSearchResult {
  provider: string;
  providerId: string;
  mediaType: string;
  title: string;
  originalTitle: string | null;
  year: number | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  ratings?: MetadataRatingItem[] | null;
  genres: string[];
  cast?: MetadataCastMember[] | null;
  imdbId: string | null;
  externalUrl: string | null;
}

export interface MetadataRatingItem {
  source: string;
  label: string;
  score: number | null;
  maxScore: number | null;
  voteCount: number | null;
  url: string | null;
  kind: string | null;
}

export interface MetadataProviderStatus {
  provider: string;
  isConfigured: boolean;
  mode: "live" | "unconfigured" | string;
  message: string;
  sources: MetadataSourceStatus[];
}

export interface MetadataSourceStatus {
  source: string;
  label: string;
  role: string;
  isConfigured: boolean;
  mode: string;
  message: string;
}

export interface MetadataTestResponse {
  provider: string;
  isConfigured: boolean;
  mode: string;
  message: string;
  resultCount: number;
  sampleResults: MetadataSearchResult[];
}

export interface MetadataRefreshJobsResponse {
  enqueuedCount: number;
  /** How many stale titles are left after this batch. */
  remainingCount: number;
  /** Everything the backfill currently considers stale, including the batch just queued. */
  staleCount: number;
  /** How many entries a "refresh everything" request marked. Zero otherwise. */
  markedForRefreshCount: number;
  /** What happened, phrased for a person. Prefer this over recomputing it. */
  message: string;
}

export interface MetadataCastMember {
  name: string;
  character: string | null;
  profileUrl: string | null;
}
