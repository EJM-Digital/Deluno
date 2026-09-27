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
  downloadHealthNoProgressEnabled?: boolean;
  downloadHealthNoProgressMinutes?: number;
  downloadHealthMinSpeedEnabled?: boolean;
  downloadHealthMinSpeedKbps?: number;
  downloadHealthMinSpeedMinutes?: number;
  downloadHealthMinPeersEnabled?: boolean;
  downloadHealthMinPeers?: number;
  downloadHealthMinPeersMinutes?: number;
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
}
