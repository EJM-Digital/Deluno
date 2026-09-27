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
  movieFolderFormat: string;
  seriesFolderFormat: string;
  movieFileFormat: string;
  episodeFileFormat: string;
  /** #761: used for dated (daily) shows instead of episodeFileFormat. */
  dailyEpisodeFileFormat: string;
  /** #761: used for absolute-numbered (anime) shows instead of episodeFileFormat. */
  animeEpisodeFileFormat: string;
  hostBindAddress: string;
  hostPort: number;
  urlBase: string;
  /** A FlareSolverr-compatible helper for trackers behind a Cloudflare check (#628). */
  cloudflareHelperUrl?: string | null;
  /** The helper's own request timeout, 1-180 seconds, default 60 (#749). */
  cloudflareHelperTimeoutSeconds: number;
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
  /** #860: the IANA zone episode air times convert into. Empty falls back to the browser/OS timezone. */
  timezone: string;
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
  /** How many days an unresolved manual-import (import-recovery) case is kept before it is cleared away unresolved. */
  importRecoveryRetentionDays: number;
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
  /** #815: which tracker-privacy downloads the global sharing policy covers. */
  sharingPrivacyScope: "public" | "private" | "both" | string;
  /** "reclaim" removes the item once the target is met; "stop" just stops seeding and leaves it in the client (#821). */
  sharingCompletionAction: string;
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
  /**
   * #814: one sandbox switch, shared by the strike/blocklist executor and
   * the seeding-cleanup executor. On, both log the action they would have
   * taken and make no real change. Off by default.
   */
  downloadHealthDryRunEnabled: boolean;
  /** #825: how long a download can go without a fresh strike before its strike count clears, independent of any rule's own timer. 1-720 hours. */
  downloadHealthStrikeInactivityHours: number;
  /**
   * #824: a person's own reachability guard, separate from #901's built-in
   * outage detector. Off by default; when on, a failed check skips the
   * strike/blocklist pass, the seeding-reclaim pass, and this poll's own
   * strike evaluation entirely - nothing is struck, blocklisted, or
   * reclaimed while it lasts.
   */
  downloadHealthConnectivityCheckEnabled: boolean;
  /** #824: comma-separated URLs; at least one must answer 2xx to proceed. */
  downloadHealthConnectivityCheckUrls: string;
  /** #823: the pacing window a finding outside the rule engine (client-stalled, and so on) escalates strikes at; a rule-engine finding uses its own timer instead. 1-1440 minutes. */
  downloadHealthDefaultStrikeWindowMinutes: number;
  /** #823: how often the throughput sampler polls telemetry for the speed chart. 1-60 minutes. */
  downloadThroughputSamplerIntervalMinutes: number;
  /** #828: 1-3650 days of Deluno's own event/strike history (Activity rows, download-health strike records) before a background job prunes it. A different concept from a title's own retention, which RetentionService governs. */
  historyRetentionDays: number;
  /** #829: a single hourly search-call ceiling across every indexer combined. Off by default (#936). */
  maxSearchesPerHourEnabled: boolean;
  maxSearchesPerHour: number;
  /** #818: zero seeders, or the tracker itself saying unregistered, past a grace period since the download was added. Off by default. */
  downloadHealthDeadTorrentEnabled: boolean;
  downloadHealthDeadTorrentGraceMinutes: number;
  /** Checkpoint A (#917): opens a finished download and checks it against what its name and Deluno's own records claim, before a processor or the library ever sees it. Off by default (#936). */
  downloadIntegrityCheckEnabled: boolean;
  downloadIntegrityMinimumRuntimePercent: number;
  /** #909: percent free remaining at which a library root's health check warns, but keeps grabbing and importing. */
  libraryFreeSpaceWarnPercent: number;
  /** #909: percent free remaining at or below which Deluno pauses a library's grabs and imports, the same way an unreachable root does. */
  libraryFreeSpacePausePercent: number;
  /**
   * #936: the one global switch for fetching subtitles on its own, asked in
   * the setup guide and set in Settings -> Subtitles. Off by default for a
   * new install. The per-library subtitle-language list still decides which
   * languages are wanted; this decides whether the automatic job that
   * fetches them ever runs at all.
   */
  subtitleFetchAutomaticEnabled: boolean;
  /** #757: Radarr/Sonarr's "Replace Illegal Characters" switch. On (default) turns an illegal filename character into a dash; off removes it instead. */
  replaceIllegalCharacters: boolean;
  /** #759: one of "smart" (default) | "delete" | "dash" | "space-dash" | "space-dash-space", consulted only when replaceIllegalCharacters is on. */
  colonReplacementMode: "smart" | "delete" | "dash" | "space-dash" | "space-dash-space" | string;
  /** #766: matches Radarr/Sonarr's Permissions section. Off by default; only takes effect on import, on non-Windows hosts, when Deluno's process owns the file. */
  setPermissionsOnImport: boolean;
  folderChmod: string | null;
  chownGroup: string | null;
  /** #767: "prefer-and-upgrade" | "do-not-upgrade-automatically" | "do-not-prefer". */
  properRepackAcquisitionMode: string;
  /**
   * #932/#936: whether the "Clean up leftover downloads" system task may
   * delete anything. Off for a new install, asked in the setup guide's
   * Automation step and set here in Media Management -> Cleanup ->
   * Leftover downloads, the same as every other automatic behaviour.
   */
  leftoverDownloadCleanupEnabled: boolean;
  /** #932: how long a leftover must have sat, untouched, before the task deletes it. Only matters once the switch above is on. */
  leftoverDownloadCleanupAgeHours: number;
  /** #762: per-season and specials (season 0) subfolders under the series folder. Blank means no subfolder, matching a movie/series folder format's own blank behaviour. */
  seasonFolderFormat: string;
  specialsFolderFormat: string;
  /** #763: one of "extend" | "duplicate" | "repeat" | "scene" | "range" (default) | "prefixed-range". */
  multiEpisodeStyle: string;
  /** #764: independent of renameOnImport, which only ever governs the file name. Off by default. */
  renameFoldersOnImport: boolean;
  /** #765: minimum free space (MB) the destination volume must keep after an import; matches Radarr/Sonarr's 100MB default floor. */
  minimumFreeSpaceMb: number;
  /** #765: for a filesystem that cannot report free space at all, not a substitute for lowering minimumFreeSpaceMb. */
  skipFreeSpaceCheck: boolean;
  /** #887: off by default. A still-packed archive is always blocklisted (per-reason, on the failure rules console); this decides whether it is also removed and deleted from the download client. */
  removeAndDeletePackedArchivesFromClient: boolean;
  /**
   * #879: the "dangerous or junk file" rule in the download-health engine.
   * Off by default (#936) - #816's content-blocklist check can still surface
   * a warning on its own, but nothing removes, deletes or blocklists a
   * download automatically until this is turned on too.
   */
  downloadHealthDangerousFileEnabled: boolean;
  updatedUtc: string;
}

/**
 * #816's content-blocklist configuration, kept separately from
 * PlatformSettingsSnapshot the same way download-health rules are - fetched
 * and saved through its own small endpoint rather than living on the big flat
 * settings object.
 */
export interface ContentBlocklistSettings {
  enabled: boolean;
  mode: "blacklist" | "whitelist" | string;
  useBuiltInPatterns: boolean;
  customPatterns: string[];
  remoteUrl: string | null;
  remotePatterns: string[];
  lastRefreshedUtc: string | null;
  /** #822: pushes this blocklist into every enabled qBittorrent client's own "Excluded file names" preference, hourly. Off by default and independent of `enabled`. */
  syncToQbittorrentEnabled: boolean;
  updatedUtc: string;
}

export const emptyContentBlocklistSettings: ContentBlocklistSettings = {
  enabled: false,
  mode: "blacklist",
  useBuiltInPatterns: true,
  customPatterns: [],
  remoteUrl: null,
  remotePatterns: [],
  lastRefreshedUtc: null,
  syncToQbittorrentEnabled: false,
  updatedUtc: "1970-01-01T00:00:00Z",
};

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

/** One address Deluno noticed it could be reached on, and why it's offered (#939). */
export interface DetectedHostCandidate {
  host: string;
  reason: string;
}

/** System -> Security -> Allowed Hosts: what's saved, what's detected, and the address answering this request. */
export interface AllowedHostsOverview {
  enabled: boolean;
  allowedHosts: string[];
  detectedHosts: DetectedHostCandidate[];
  currentHost: string;
}

export interface UpdateAllowedHostsRequest {
  enabled: boolean;
  allowedHosts: string[];
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
  movieFolderFormat: "{Movie Title} ({Release Year})",
  seriesFolderFormat: "{Series Title} ({Series Year})",
  movieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
  episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}] [{Custom Formats}]",
  dailyEpisodeFileFormat: "{Series Title} - {Air-Date} - {Episode Title} [{Quality}] [{Custom Formats}]",
  animeEpisodeFileFormat: "{Series Title} - {absolute:000} - {Episode Title} [{Quality}] [{Custom Formats}]",
  hostBindAddress: "127.0.0.1",
  hostPort: 5099,
  urlBase: "",
  cloudflareHelperUrl: null,
  cloudflareHelperTimeoutSeconds: 60,
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
  timezone: "",
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
  importRecoveryRetentionDays: 30,
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
  sharingPrivacyScope: "both",
  sharingCompletionAction: "reclaim",
  libraryFileCheckHours: 6,
  downloadHealthNoProgressEnabled: true,
  downloadHealthNoProgressMinutes: 30,
  downloadHealthMinSpeedEnabled: false,
  downloadHealthMinSpeedKbps: 50,
  downloadHealthMinSpeedMinutes: 30,
  downloadHealthMinPeersEnabled: false,
  downloadHealthMinPeers: 1,
  downloadHealthMinPeersMinutes: 30,
  downloadHealthDryRunEnabled: false,
  downloadHealthStrikeInactivityHours: 72,
  downloadHealthConnectivityCheckEnabled: false,
  downloadHealthConnectivityCheckUrls: "",
  downloadHealthDefaultStrikeWindowMinutes: 30,
  downloadThroughputSamplerIntervalMinutes: 1,
  historyRetentionDays: 365,
  maxSearchesPerHourEnabled: false,
  maxSearchesPerHour: 200,
  downloadHealthDeadTorrentEnabled: false,
  downloadHealthDeadTorrentGraceMinutes: 60,
  downloadIntegrityCheckEnabled: false,
  downloadIntegrityMinimumRuntimePercent: 80,
  libraryFreeSpaceWarnPercent: 10,
  libraryFreeSpacePausePercent: 5,
  // #936: off by default, matching PlatformSettingsSnapshot's server-side default.
  subtitleFetchAutomaticEnabled: false,
  replaceIllegalCharacters: true,
  colonReplacementMode: "smart",
  setPermissionsOnImport: false,
  folderChmod: null,
  chownGroup: null,
  properRepackAcquisitionMode: "prefer-and-upgrade",
  // #932/#936: off by default, matching PlatformSettingsSnapshot's server-side default.
  leftoverDownloadCleanupEnabled: false,
  leftoverDownloadCleanupAgeHours: 24,
  seasonFolderFormat: "",
  specialsFolderFormat: "",
  multiEpisodeStyle: "range",
  renameFoldersOnImport: false,
  minimumFreeSpaceMb: 100,
  skipFreeSpaceCheck: false,
  removeAndDeletePackedArchivesFromClient: false,
  // #936: off by default, matching PlatformSettingsSnapshot's server-side default.
  downloadHealthDangerousFileEnabled: false,
  updatedUtc: new Date(0).toISOString()
};
