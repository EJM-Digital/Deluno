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
  movieFolderFormat: string;
  seriesFolderFormat: string;
  movieFileFormat: string;
  episodeFileFormat: string;
  hostBindAddress: string;
  hostPort: number;
  urlBase: string;
  /** A FlareSolverr-compatible helper for trackers behind a Cloudflare check (#628). */
  cloudflareHelperUrl?: string | null;
  requireAuthentication: boolean;
  uiTheme: string;
  uiColorMode: "standard" | "impaired" | string;
  uiLanguage: string;
  calendarFirstDayOfWeek: "monday" | "sunday" | string;
  calendarWeekHeaderFormat: string;
  runtimeFormat: "hoursMinutes" | "minutes" | string;
  shortDateFormat: "dmy" | "mdy" | "iso" | string;
  longDateFormat: "full" | "mdy" | string;
  timeFormat: "12" | "24" | string;
  showRelativeDates: boolean;
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
  /**
   * How often Deluno looks at whether the files it thinks you have are still
   * on disk. The right answer depends on the disk: a local pool can afford
   * hourly; a NAS that spins up to answer should not be woken every hour to be
   * asked.
   */
  libraryFileCheckHours: number;
  /**
   * #659: when a download counts as stuck, before it is struck. Each rule has
   * its own on/off switch; "no progress" is on by default with the minutes
   * Deluno always used, the other two are sharper rules a person opts into.
   */
  downloadHealthNoProgressEnabled: boolean;
  downloadHealthNoProgressMinutes: number;
  downloadHealthMinSpeedEnabled: boolean;
  downloadHealthMinSpeedKbps: number;
  downloadHealthMinSpeedMinutes: number;
  downloadHealthMinPeersEnabled: boolean;
  downloadHealthMinPeers: number;
  downloadHealthMinPeersMinutes: number;
  /** #909: percent free remaining at which a library root's health check warns, but keeps grabbing and importing. */
  libraryFreeSpaceWarnPercent: number;
  /** #909: percent free remaining at or below which Deluno pauses a library's grabs and imports, the same way an unreachable root does. */
  libraryFreeSpacePausePercent: number;
  /** #936: comma-separated automation-notice keys the Dashboard's "Keep off" has dismissed, so the notice stays gone across reloads and devices. */
  dismissedAutomationNotices: string;
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
  // #936: nothing automatic is on until the setup guide's Automation step, or
  // Settings, explicitly turns it on. Matches PlatformSettingsSnapshot's
  // server-side "missing key = off" default for a brand-new install.
  autoStartJobs: false,
  enableNotifications: true,
  renameOnImport: false,
  useHardlinks: false,
  cleanupEmptyFolders: false,
  removeCompletedDownloads: false,
  movieFolderFormat: "{Movie Title} ({Release Year})",
  seriesFolderFormat: "{Series Title} ({Series Year})",
  movieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
  episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}] [{Custom Formats}]",
  hostBindAddress: "127.0.0.1",
  hostPort: 5099,
  urlBase: "",
  cloudflareHelperUrl: null,
  requireAuthentication: true,
  uiTheme: "system",
  uiColorMode: "standard",
  uiLanguage: "en-AU",
  calendarFirstDayOfWeek: "monday",
  calendarWeekHeaderFormat: "ddd d/M",
  runtimeFormat: "hoursMinutes",
  shortDateFormat: "dmy",
  longDateFormat: "full",
  timeFormat: "12",
  showRelativeDates: true,
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
  // #936: off by default (reverses #904), matching PlatformSettingsSnapshot's server-side default.
  cleanupRemoveClientEntryAfterThreshold: false,
  cleanupPurgePayloadAfterThreshold: false,
  workflowVerified: false,
  sharingMode: "share-then-tidy",
  sharingForHours: 72,
  sharingUntilRatio: null,
  sharingStuckAction: "give-up",
  sharingStuckAfterDays: 14,
  libraryFileCheckHours: 6,
  downloadHealthNoProgressEnabled: true,
  downloadHealthNoProgressMinutes: 30,
  downloadHealthMinSpeedEnabled: false,
  downloadHealthMinSpeedKbps: 50,
  downloadHealthMinSpeedMinutes: 30,
  downloadHealthMinPeersEnabled: false,
  downloadHealthMinPeers: 1,
  downloadHealthMinPeersMinutes: 30,
  libraryFreeSpaceWarnPercent: 10,
  libraryFreeSpacePausePercent: 5,
  dismissedAutomationNotices: "",
  updatedUtc: new Date(0).toISOString()
};
