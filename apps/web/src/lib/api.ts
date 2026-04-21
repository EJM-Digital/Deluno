export interface DatabaseDescriptor {
  key: string;
  fileName: string;
  purpose: string;
}

export interface ModuleDescriptor {
  name: string;
  purpose: string;
}

export interface SystemManifest {
  app: string;
  storageRoot: string;
  modules: ModuleDescriptor[];
  databases: DatabaseDescriptor[];
}

export interface MovieListItem {
  id: string;
  title: string;
  releaseYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface MovieImportRecoveryCase {
  id: string;
  title: string;
  failureKind: string;
  summary: string;
  recommendedAction: string;
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

export interface SeriesListItem {
  id: string;
  title: string;
  startYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface SeriesImportRecoveryCase {
  id: string;
  title: string;
  failureKind: string;
  summary: string;
  recommendedAction: string;
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

export interface ValidationProblem {
  title?: string;
  errors?: Record<string, string[]>;
}

export interface PlatformSettingsSnapshot {
  appInstanceName: string;
  movieRootPath: string | null;
  seriesRootPath: string | null;
  downloadsPath: string | null;
  incompleteDownloadsPath: string | null;
  autoStartJobs: boolean;
  enableNotifications: boolean;
  updatedUtc: string;
}

export interface LibraryItem {
  id: string;
  name: string;
  mediaType: string;
  purpose: string;
  rootPath: string;
  downloadsPath: string | null;
  qualityProfileId: string | null;
  qualityProfileName: string | null;
  cutoffQuality: string | null;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalHours: number;
  retryDelayHours: number;
  maxItemsPerRun: number;
  automationStatus: string;
  searchRequested: boolean;
  lastSearchedUtc: string | null;
  nextSearchUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface QualityProfileItem {
  id: string;
  name: string;
  mediaType: string;
  cutoffQuality: string;
  allowedQualities: string;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface ConnectionItem {
  id: string;
  name: string;
  connectionKind: string;
  role: string;
  endpointUrl: string | null;
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface IndexerItem {
  id: string;
  name: string;
  protocol: string;
  privacy: string;
  baseUrl: string;
  priority: number;
  categories: string;
  tags: string;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface DownloadClientItem {
  id: string;
  name: string;
  protocol: string;
  endpointUrl: string | null;
  categoryTemplate: string | null;
  priority: number;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface JobQueueItem {
  id: string;
  jobType: string;
  source: string;
  status: string;
  payloadJson: string | null;
  attempts: number;
  createdUtc: string;
  scheduledUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  leasedUntilUtc: string | null;
  workerId: string | null;
  lastError: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
}

export interface ActivityEventItem {
  id: string;
  category: string;
  message: string;
  detailsJson: string | null;
  relatedJobId: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
  createdUtc: string;
}

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init);
  if (!response.ok) {
    throw new Error(`Request failed for ${path} with status ${response.status}.`);
  }

  return (await response.json()) as T;
}

export async function readValidationProblem(
  response: Response
): Promise<ValidationProblem | null> {
  try {
    return (await response.json()) as ValidationProblem;
  } catch {
    return null;
  }
}
