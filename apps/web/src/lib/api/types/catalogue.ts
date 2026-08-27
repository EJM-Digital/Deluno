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
  /**
   * When the movie is out, and when Deluno may start looking. A release year
   * cannot say "in cinemas but not yet obtainable"; these can.
   */
  inCinemasDate?: string | null;
  digitalReleaseDate?: string | null;
  physicalReleaseDate?: string | null;
  minimumAvailability?: string | null;
  isAvailable?: boolean | null;
  /**
   * The search state Deluno holds, carried by the page itself.
   *
   * These used to be read from the wanted summary, whose `recentItems` is
   * capped at 25 — so past the first 25 titles every card lost its status and
   * fell back to "is there a file". Null throughout means Deluno is not
   * tracking the title in any library, which is not the same as a state of no.
   */
  libraryId?: string | null;
  wantedStatus?: string | null;
  wantedReason?: string | null;
  targetQuality?: string | null;
  qualityCutoffMet?: boolean | null;
  lastSearchUtc?: string | null;
  nextEligibleSearchUtc?: string | null;
  /**
   * The bar under the poster. Zero until Subber (#301) fills subtitle languages
   * in; the contract is here now so the mark does not have to be redesigned
   * around it later.
   */
  subtitleLanguagesWanted?: number;
  subtitleLanguagesHeld?: number;
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
  /** Titles that have what the profile asked for. `waitingCount` until #300. */
  coveredCount: number;
  /** Titles that are not out yet, so there is nothing to look for. */
  upcomingCount: number;
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
  /**
   * The search state Deluno holds, carried by the page itself.
   *
   * These used to be read from the wanted summary, whose `recentItems` is
   * capped at 25 — so past the first 25 titles every card lost its status and
   * fell back to "is there a file". Null throughout means Deluno is not
   * tracking the title in any library, which is not the same as a state of no.
   */
  libraryId?: string | null;
  wantedStatus?: string | null;
  wantedReason?: string | null;
  targetQuality?: string | null;
  qualityCutoffMet?: boolean | null;
  lastSearchUtc?: string | null;
  nextEligibleSearchUtc?: string | null;
  /**
   * What the show's episodes add up to: the rung its dot sits on, and how many
   * files it has for the subtitle bar to be measured over.
   *
   * Counted over what has aired, never over what will exist: an ongoing show
   * measured against its eventual episode count reads permanently unfinished,
   * which is true of every ongoing show and so tells you nothing.
   *
   * The poster no longer draws these. A show's bar is subtitles, exactly as a
   * movie's is, so the two shelves ask the same question; the episode counts
   * live on the show's own page.
   */
  episodeCount?: number;
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
  airedUpgradableCount?: number;
  nextAirDateUtc?: string | null;
  /**
   * The bar under the poster, on the same terms as a movie's.
   *
   * `Wanted` is the languages asked for **per episode**; `Held` is how many are
   * actually present, **summed across the episodes the show has**. So a show
   * holding 13 episodes with two languages asked for of each has 26 slots, and
   * a bar that is 22/26 green when four of them are short one language.
   *
   * Measured only over episodes on disk: counting the ones you are missing
   * would drag the bar down for a reason that is not about subtitles, and the
   * dot above it already says the show is Missing.
   *
   * Zero until Subber (#301) fills these in.
   */
  subtitleLanguagesWanted?: number;
  subtitleLanguagesHeld?: number;
}

export interface CatalogueFacets {
  all: number;
  monitored: number;
  unmonitored: number;
  /**
   * Has a file, whatever its quality. Still a filter, no longer a number worth
   * printing: a movie below its target is downloaded too, so the word could never
   * tell you which titles still had work outstanding.
   */
  downloaded: number;
  missing: number;
  upgrades: number;
  /** Has what the profile asked for. Deluno has stopped looking. */
  covered: number;
  /** Not out yet, so its absence is not a shortfall. */
  upcoming: number;
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
  /** Titles that have what the profile asked for. `waitingCount` until #300. */
  coveredCount: number;
  /** Titles that are not out yet, so there is nothing to look for. */
  upcomingCount: number;
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
