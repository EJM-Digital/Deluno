export interface PlatformSettingsPatch {
  appInstanceName?: string;
  movieRootPath?: string;
  seriesRootPath?: string;
  downloadsPath?: string;
  incompleteDownloadsPath?: string;
  autoStartJobs?: boolean;
  enableNotifications?: boolean;
  renameOnImport?: boolean;
  useHardlinks?: boolean;
  cleanupEmptyFolders?: boolean;
  movieFolderFormat?: string;
  seriesFolderFormat?: string;
  movieFileFormat?: string;
  episodeFileFormat?: string;
  /** #761: used for dated (daily) shows instead of episodeFileFormat. */
  dailyEpisodeFileFormat?: string;
  /** #761: used for absolute-numbered (anime) shows instead of episodeFileFormat. */
  animeEpisodeFileFormat?: string;
  hostBindAddress?: string;
  hostPort?: number;
  urlBase?: string;
  cloudflareHelperUrl?: string;
  /** The helper's own request timeout, 1-180 seconds (#749). */
  cloudflareHelperTimeoutSeconds?: number;
  /** Which helper answers at cloudflareHelperUrl: "auto" (default), "flaresolverr" or "byparr" (#1029). */
  cloudflareHelperKind?: string;
  requireAuthentication?: boolean;
  uiTheme?: string;
  uiColorMode?: string;
  uiLanguage?: string;
  calendarFirstDayOfWeek?: string;
  calendarWeekHeaderFormat?: string;
  runtimeFormat?: string;
  shortDateFormat?: string;
  longDateFormat?: string;
  timeFormat?: string;
  showRelativeDates?: boolean;
  /** #860: the IANA zone episode air times convert into; empty clears it back to the browser/OS timezone. */
  timezone?: string;
  defaultMovieView?: string;
  defaultShowView?: string;
  metadataCertificationCountry?: string;
  metadataLanguage?: string;
  metadataProviderMode?: string;
  metadataBrokerUrl?: string;
  metadataTmdbApiKey?: string;
  metadataOmdbApiKey?: string;
  releaseNeverGrabPatterns?: string;
  searchScoringMode?: string;
  importRecoveryRetentionDays?: number;
  libraryFileCheckHours?: number;
  mdbListApiKey?: string;
  downloadHealthStrikeThreshold?: number;
  cleanupBlockReleaseAfterThreshold?: boolean;
  cleanupQueueReplacementAfterThreshold?: boolean;
  cleanupRemoveClientEntryAfterThreshold?: boolean;
  cleanupPurgePayloadAfterThreshold?: boolean;
  sharingMode?: string;
  /** Null clears that half of the rule; undefined leaves it as it is. */
  sharingForHours?: number | null;
  sharingUntilRatio?: number | null;
  sharingStuckAction?: string;
  sharingStuckAfterDays?: number;
  /** #815: which tracker-privacy downloads the global sharing policy covers. */
  sharingPrivacyScope?: string;
  sharingCompletionAction?: string;
  downloadHealthNoProgressEnabled?: boolean;
  downloadHealthNoProgressMinutes?: number;
  downloadHealthMinSpeedEnabled?: boolean;
  downloadHealthMinSpeedKbps?: number;
  downloadHealthMinSpeedMinutes?: number;
  downloadHealthMinPeersEnabled?: boolean;
  downloadHealthMinPeers?: number;
  downloadHealthMinPeersMinutes?: number;
  /** #814: one sandbox switch, shared by the strike/blocklist executor and the seeding-cleanup executor. */
  downloadHealthDryRunEnabled?: boolean;
  /** #825: hours a download can go without a fresh strike before its strike count clears. 1-720. */
  downloadHealthStrikeInactivityHours?: number;
  /** #824: a person's own reachability guard, separate from #901's built-in outage detector. */
  downloadHealthConnectivityCheckEnabled?: boolean;
  /** #824: comma-separated URLs; at least one must answer 2xx to proceed. */
  downloadHealthConnectivityCheckUrls?: string;
  /** #823: the pacing window a finding outside the rule engine escalates strikes at, in minutes. 1-1440. */
  downloadHealthDefaultStrikeWindowMinutes?: number;
  /** #823: how often the throughput sampler polls telemetry for the speed chart, in minutes. 1-60. */
  downloadThroughputSamplerIntervalMinutes?: number;
  /** #828: 1-3650 days of Deluno's own event/strike history before a background job prunes it. */
  historyRetentionDays?: number;
  /** #829: a single hourly search-call ceiling across every indexer combined. Off by default (#936). */
  maxSearchesPerHourEnabled?: boolean;
  maxSearchesPerHour?: number;
  /** #818: zero seeders, or the tracker itself saying unregistered, past a grace period since the download was added. Off by default. */
  downloadHealthDeadTorrentEnabled?: boolean;
  downloadHealthDeadTorrentGraceMinutes?: number;
  /** Checkpoint A (#917): opens a finished download and checks it before a processor or the library ever sees it. Off by default (#936). */
  downloadIntegrityCheckEnabled?: boolean;
  downloadIntegrityMinimumRuntimePercent?: number;
  /** #909: percent free remaining at which a library root's health check warns, but keeps grabbing and importing. */
  libraryFreeSpaceWarnPercent?: number;
  /** #909: percent free remaining at or below which Deluno pauses a library's grabs and imports, the same way an unreachable root does. */
  libraryFreeSpacePausePercent?: number;
  /** #936: the one global switch for fetching subtitles on its own. */
  subtitleFetchAutomaticEnabled?: boolean;
  /** #757: on (default) turns an illegal filename character into a dash; off removes it instead. */
  replaceIllegalCharacters?: boolean;
  /** #759: one of "smart" (default) | "delete" | "dash" | "space-dash" | "space-dash-space". */
  colonReplacementMode?: string;
  /** #766: matches Radarr/Sonarr's Permissions section. Off by default; only takes effect on import, on non-Windows hosts, when Deluno's process owns the file. */
  setPermissionsOnImport?: boolean;
  folderChmod?: string | null;
  chownGroup?: string | null;
  properRepackAcquisitionMode?: string;
  /** #932/#936: off by default; the "Clean up leftover downloads" system task deletes nothing while this is off. */
  leftoverDownloadCleanupEnabled?: boolean;
  /** #932: how long a leftover must have sat before the task deletes it. Only matters once the switch above is on. */
  leftoverDownloadCleanupAgeHours?: number;
  /** #762: blank clears the subfolder. */
  seasonFolderFormat?: string;
  specialsFolderFormat?: string;
  /** #763: one of "extend" | "duplicate" | "repeat" | "scene" | "range" (default) | "prefixed-range". */
  multiEpisodeStyle?: string;
  /** #764: independent of renameOnImport. */
  renameFoldersOnImport?: boolean;
  /** #765: minimum free space (MB) the destination volume must keep after an import. */
  minimumFreeSpaceMb?: number;
  /** #765: for a filesystem that cannot report free space at all. */
  skipFreeSpaceCheck?: boolean;
  /** #887: off by default. A still-packed archive is always blocklisted (governed separately, per-reason); this decides whether it is also removed and deleted from the download client. */
  removeAndDeletePackedArchivesFromClient?: boolean;
  /** #879: the "dangerous or junk file" download-health rule. Off by default. */
  downloadHealthDangerousFileEnabled?: boolean;
  /** #888/#949: one of "hold" (default) | "give-up-after-days" | "retry-once-then-bad". */
  unmatchedImportPolicy?: string;
  /** #888/#949: only consulted when unmatchedImportPolicy is "give-up-after-days". */
  unmatchedImportGiveUpDays?: number;
  /** #1046: off by default. Tags a title that has left every import list with offListTag. */
  tagTitlesOffEveryList?: boolean;
  offListTag?: string;
}
