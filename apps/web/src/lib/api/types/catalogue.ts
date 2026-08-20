import type { MetadataRatingItem } from "./metadata";

export interface MovieListItem {
  id: string;
  title: string;
  releaseYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  hasFile: boolean;
  metadataProvider: string | null;
  metadataProviderId: string | null;
  originalTitle: string | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  ratings?: MetadataRatingItem[] | null;
  genres: string | null;
  externalUrl: string | null;
  metadataJson: string | null;
  metadataUpdatedUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
  fileSizeBytes?: number | null;
  currentQuality?: string | null;
  filePath?: string | null;
  videoCodec?: string | null;
  audioCodec?: string | null;
  audioChannels?: string | null;
  releaseGroup?: string | null;
  runtimeMinutes?: number | null;
  popularity?: number | null;
  voteCount?: number | null;
  approximateBitrateMbps?: number | null;
}

export interface MovieImportRecoveryCase {
  id: string;
  title: string;
  failureKind: string;
  summary: string;
  recommendedAction: string;
  detailsJson: string | null;
  detectedUtc: string;
}

export interface MovieImportRecoverySummary {
  openCount: number;
  qualityCount: number;
  unmatchedCount: number;
  corruptCount: number;
  downloadFailedCount: number;
  importFailedCount: number;
  recentCases: MovieImportRecoveryCase[];
}

export interface MovieWantedItem {
  movieId: string;
  title: string;
  releaseYear: number | null;
  imdbId: string | null;
  libraryId: string;
  wantedStatus: string;
  wantedReason: string;
  hasFile: boolean;
  currentQuality: string | null;
  targetQuality: string | null;
  qualityCutoffMet: boolean;
  missingSinceUtc: string | null;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
  lastSearchResult: string | null;
  updatedUtc: string;
}

export interface MovieWantedSummary {
  totalWanted: number;
  missingCount: number;
  upgradeCount: number;
  waitingCount: number;
  recentItems: MovieWantedItem[];
}

export interface MovieSearchHistoryItem {
  id: string;
  movieId: string;
  libraryId: string;
  triggerKind: string;
  outcome: string;
  releaseName: string | null;
  indexerName: string | null;
  detailsJson: string | null;
  createdUtc: string;
}

export interface SeriesListItem {
  id: string;
  title: string;
  startYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  hasFile: boolean;
  metadataProvider: string | null;
  metadataProviderId: string | null;
  originalTitle: string | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  ratings?: MetadataRatingItem[] | null;
  genres: string | null;
  externalUrl: string | null;
  metadataJson: string | null;
  metadataUpdatedUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
  fileSizeBytes?: number | null;
  currentQuality?: string | null;
  filePath?: string | null;
  videoCodec?: string | null;
  audioCodec?: string | null;
  audioChannels?: string | null;
  releaseGroup?: string | null;
  runtimeMinutes?: number | null;
  popularity?: number | null;
  voteCount?: number | null;
  approximateBitrateMbps?: number | null;
}

export interface CatalogueFacets {
  all: number;
  monitored: number;
  unmonitored: number;
  downloaded: number;
  missing: number;
  upgrades: number;
}

export interface CataloguePage<T> {
  items: T[];
  nextPageToken: string | null;
  hasMore: boolean;
  totalCount: number | null;
  facets: CatalogueFacets | null;
}

/** Every bounded operational list states whether another page remains. */
export interface ApiPage<T> {
  items: T[];
  nextPageToken: string | null;
  hasMore: boolean;
}

export interface SeriesImportRecoveryCase {
  id: string;
  title: string;
  failureKind: string;
  summary: string;
  recommendedAction: string;
  detailsJson: string | null;
  detectedUtc: string;
}

export interface SeriesImportRecoverySummary {
  openCount: number;
  qualityCount: number;
  unmatchedCount: number;
  corruptCount: number;
  downloadFailedCount: number;
  importFailedCount: number;
  recentCases: SeriesImportRecoveryCase[];
}

export interface SeriesWantedItem {
  seriesId: string;
  title: string;
  startYear: number | null;
  imdbId: string | null;
  libraryId: string;
  wantedStatus: string;
  wantedReason: string;
  hasFile: boolean;
  currentQuality: string | null;
  targetQuality: string | null;
  qualityCutoffMet: boolean;
  missingSinceUtc: string | null;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
  lastSearchResult: string | null;
  updatedUtc: string;
}

export interface SeriesWantedSummary {
  totalWanted: number;
  missingCount: number;
  upgradeCount: number;
  waitingCount: number;
  recentItems: SeriesWantedItem[];
}

export interface SeriesInventorySummary {
  seriesCount: number;
  seasonCount: number;
  episodeCount: number;
  importedEpisodeCount: number;
}

export interface SeriesEpisodeInventoryItem {
  episodeId: string;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  airDateUtc: string | null;
  monitored: boolean;
  hasFile: boolean;
  wantedStatus: string;
  wantedReason: string;
  qualityCutoffMet: boolean;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
  updatedUtc: string;
}

export interface SeriesInventoryDetail {
  seriesId: string;
  title: string;
  startYear: number | null;
  seasonCount: number;
  episodeCount: number;
  importedEpisodeCount: number;
  episodes: SeriesEpisodeInventoryItem[];
}

export interface SeriesUpcomingEpisodeItem {
  seriesId: string;
  title: string;
  startYear: number | null;
  posterUrl: string | null;
  episodeId: string;
  seasonNumber: number;
  episodeNumber: number;
  episodeTitle: string | null;
  airDateUtc: string;
}

export interface SeriesSearchHistoryItem {
  id: string;
  seriesId: string;
  episodeId: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  libraryId: string;
  triggerKind: string;
  outcome: string;
  releaseName: string | null;
  indexerName: string | null;
  detailsJson: string | null;
  createdUtc: string;
}

export interface EpisodeSearchEligibilityItem {
  episodeId: string;
  seriesId: string;
  seasonNumber: number;
  episodeNumber: number;
  title: string;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
}

export interface EpisodeWorkflowDecision {
  episodeId: string;
  status: "wanted" | "archived" | "satisfied";
  targetQuality: string | null;
  currentQuality: string | null;
  reason: string;
}
