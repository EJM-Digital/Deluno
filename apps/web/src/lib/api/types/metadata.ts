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
  crew?: MetadataCrewMember[] | null;
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
  lastFailure?: import("./resources").IntegrationFailure | null;
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
  failure?: import("./resources").IntegrationFailure | null;
}

export interface MetadataLinkIdentity {
  provider: string | null;
  providerId: string | null;
  title: string;
  year: number | null;
  imdbId: string | null;
  context: string | null;
}

export interface MetadataIdentityConflict {
  id: string;
  title: string;
  reason: "provider-id" | "imdb-id" | "title-year" | string;
}

export interface MetadataCatalogueImpact {
  existingEpisodeCount: number;
  importedEpisodeCount: number;
  proposedEpisodeCount: number;
  proposedSeasonCount: number;
  existingEpisodesOutsideProposed: number;
}

export interface MetadataLinkPreview {
  mediaType: "movies" | "tv" | string;
  subjectId: string;
  current: MetadataLinkIdentity;
  proposed: MetadataLinkIdentity;
  changes: string[];
  consequences: string[];
  conflict: MetadataIdentityConflict | null;
  catalogueImpact: MetadataCatalogueImpact | null;
  canApply: boolean;
  blockReason: string | null;
  confirmationToken: string;
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
  personId: string | null;
  imdbUrl: string | null;
}

/** `job` holds every job this person did on the title, joined. */
export interface MetadataCrewMember {
  name: string;
  job: string | null;
  profileUrl: string | null;
  personId: string | null;
  imdbUrl: string | null;
}
