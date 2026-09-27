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
  removeCompletedDownloads?: boolean;
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
  metadataNfoEnabled?: boolean;
  metadataArtworkEnabled?: boolean;
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
}
