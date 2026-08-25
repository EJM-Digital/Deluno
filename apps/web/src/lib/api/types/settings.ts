export interface ValidationProblem {
  title?: string;
  errors?: Record<string, string[]>;
}

export interface DownloadCleanupPreview {
  clientId: string;
  queueItemId: string;
  releaseName: string;
  matchedPolicy: string;
  reason: string;
  proposedAction: string;
  affectedFiles: string;
  removalAllowed: boolean;
  replacementSearchWillRun: boolean;
  requiresReview: boolean;
  strikeThreshold: number;
  blocksRelease: boolean;
  purgesPayload: boolean;
}

export interface PlatformSettingsSnapshot {
  appInstanceName: string;
  movieRootPath: string | null;
  seriesRootPath: string | null;
  downloadsPath: string | null;
  incompleteDownloadsPath: string | null;
  autoStartJobs: boolean;
  enableNotifications: boolean;
  renameOnImport: boolean;
  useHardlinks: boolean;
  cleanupEmptyFolders: boolean;
  removeCompletedDownloads: boolean;
  unmonitorWhenCutoffMet: boolean;
  movieFolderFormat: string;
  seriesFolderFormat: string;
  episodeFileFormat: string;
  hostBindAddress: string;
  hostPort: number;
  urlBase: string;
  requireAuthentication: boolean;
  uiTheme: string;
  uiDensity: string;
  defaultMovieView: string;
  defaultShowView: string;
  metadataNfoEnabled: boolean;
  metadataArtworkEnabled: boolean;
  metadataCertificationCountry: string;
  metadataLanguage: string;
  metadataProviderMode: "broker" | "hybrid" | "direct" | string;
  metadataBrokerUrl: string;
  metadataBrokerConfigured: boolean;
  metadataTmdbApiKeyConfigured: boolean;
  metadataOmdbApiKeyConfigured: boolean;
  mdbListApiKeyConfigured: boolean;
  releaseNeverGrabPatterns: string;
  searchScoringMode: "hybrid" | "rules-only" | "ml-only" | string;
  downloadHealthStrikeThreshold: number;
  cleanupBlockReleaseAfterThreshold: boolean;
  cleanupQueueReplacementAfterThreshold: boolean;
  cleanupRemoveClientEntryAfterThreshold: boolean;
  cleanupPurgePayloadAfterThreshold: boolean;
  workflowVerified: boolean;
  /** What Deluno does with the download client's copy after import (#288). */
  sharingMode: string;
  sharingForHours: number | null;
  sharingUntilRatio: number | null;
  sharingStuckAction: string;
  sharingStuckAfterDays: number;
  updatedUtc: string;
}

export interface SetupProgressItem {
  lastCompletedStep: number;
  isSkipped: boolean;
  isCompleted: boolean;
  updatedUtc: string;
}

/** Resumable guided-setup values. Connection keys and passwords are intentionally excluded. */
export interface SetupDraftItem {
  mode: "simple" | "advanced" | string;
  mediaIntent: "movies" | "tv" | "both" | string;
  movieRootPath: string;
  seriesRootPath: string;
  downloadsPath: string;
  qualityPreset: "" | "balanced1080p" | "premium4k" | string;
  formatGoal: "" | "simpleClean" | "balanced" | "homeTheater" | "storageSaver" | "anime" | string;
  indexerName: string;
  indexerProtocol: "torznab" | "newznab" | "rss" | string;
  indexerUrl: string;
  clientName: string;
  clientProtocol: string;
  clientHost: string;
  clientPort: string;
  metadataProviderMode: "broker" | "hybrid" | "direct" | string;
  metadataBrokerUrl: string;
  backupEnabled: boolean;
  firstTitleType: "movies" | "tv" | string;
  firstTitle: string;
  firstTitleYear: string;
  firstTitleMonitored: boolean;
  updatedUtc: string;
}

export interface ApiKeyItem {
  id: string;
  name: string;
  prefix: string;
  scopes: string;
  lastUsedUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface CreatedApiKeyResponse {
  item: ApiKeyItem;
  apiKey: string;
}

export const emptyPlatformSettingsSnapshot: PlatformSettingsSnapshot = {
  appInstanceName: "Deluno",
  movieRootPath: null,
  seriesRootPath: null,
  downloadsPath: null,
  incompleteDownloadsPath: null,
  autoStartJobs: true,
  enableNotifications: true,
  renameOnImport: true,
  useHardlinks: false,
  cleanupEmptyFolders: true,
  removeCompletedDownloads: false,
  unmonitorWhenCutoffMet: false,
  movieFolderFormat: "{Movie Title} ({Release Year})",
  seriesFolderFormat: "{Series Title} ({Series Year})",
  episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
  hostBindAddress: "127.0.0.1",
  hostPort: 5099,
  urlBase: "",
  requireAuthentication: true,
  uiTheme: "system",
  uiDensity: "comfortable",
  defaultMovieView: "grid",
  defaultShowView: "grid",
  metadataNfoEnabled: false,
  metadataArtworkEnabled: true,
  metadataCertificationCountry: "US",
  metadataLanguage: "en",
  metadataProviderMode: "direct",
  metadataBrokerUrl: "",
  metadataBrokerConfigured: false,
  metadataTmdbApiKeyConfigured: false,
  metadataOmdbApiKeyConfigured: false,
  mdbListApiKeyConfigured: false,
  releaseNeverGrabPatterns: "cam\ncamrip\ntelesync\ntelecine\nworkprint\nscreener\nsample\ntrailer\nextras",
  searchScoringMode: "hybrid",
  downloadHealthStrikeThreshold: 3,
  cleanupBlockReleaseAfterThreshold: true,
  cleanupQueueReplacementAfterThreshold: true,
  cleanupRemoveClientEntryAfterThreshold: false,
  cleanupPurgePayloadAfterThreshold: false,
  workflowVerified: false,
  sharingMode: "share-then-tidy",
  sharingForHours: 72,
  sharingUntilRatio: null,
  sharingStuckAction: "give-up",
  sharingStuckAfterDays: 14,
  updatedUtc: new Date(0).toISOString()
};
