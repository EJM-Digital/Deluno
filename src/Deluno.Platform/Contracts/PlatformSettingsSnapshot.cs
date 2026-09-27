namespace Deluno.Platform.Contracts;

public sealed record PlatformSettingsSnapshot(
    string AppInstanceName,
    string? MovieRootPath,
    string? SeriesRootPath,
    string? DownloadsPath,
    string? IncompleteDownloadsPath,
    bool AutoStartJobs,
    bool EnableNotifications,
    bool RenameOnImport,
    bool UseHardlinks,
    bool CleanupEmptyFolders,
    bool RemoveCompletedDownloads,
    string MovieFolderFormat,
    string SeriesFolderFormat,
    string MovieFileFormat,
    string EpisodeFileFormat,
    string HostBindAddress,
    int HostPort,
    string UrlBase,
    bool RequireAuthentication,
    string UiTheme,
    string DefaultMovieView,
    string DefaultShowView,
    bool MetadataNfoEnabled,
    bool MetadataArtworkEnabled,
    string MetadataCertificationCountry,
    string MetadataLanguage,
    string MetadataProviderMode,
    string MetadataBrokerUrl,
    bool MetadataBrokerConfigured,
    bool MetadataTmdbApiKeyConfigured,
    bool MetadataOmdbApiKeyConfigured,
    string ReleaseNeverGrabPatterns,
    string SearchScoringMode,
    int ImportRecoveryRetentionDays,
    DateTimeOffset UpdatedUtc,
    bool MdbListApiKeyConfigured = false,
    int DownloadHealthStrikeThreshold = 3,
    bool CleanupBlockReleaseAfterThreshold = true,
    bool CleanupQueueReplacementAfterThreshold = true,
    // #936 (27 Sep 2026): off by default. #904 (commit 2e9f2125) had tried to
    // make these on by default, but that change never shipped in a release —
    // v1.0.0-rc.22 already read a missing key as off for both — so there is
    // no existing install anywhere whose behaviour depended on it, and
    // nothing needs freezing at the migration level for either key. This
    // default is only what a snapshot built without reading the database
    // (e.g. a test fixture) gets.
    bool CleanupRemoveClientEntryAfterThreshold = false,
    bool CleanupPurgePayloadAfterThreshold = false,
    bool WorkflowVerified = false,
    // What Deluno does with the download client's copy once a title is safely
    // in the library (#288). Sits with the cleanup settings above because it is
    // the same question — what happens on its own, without being asked.
    string SharingMode = SharingPolicy.ModeShareThenTidy,
    int? SharingForHours = 72,
    double? SharingUntilRatio = null,
    string SharingStuckAction = SharingPolicy.StuckGiveUp,
    int SharingStuckAfterDays = 14,
    string UiColorMode = "standard",
    string UiLanguage = "en-AU",
    string CalendarFirstDayOfWeek = "monday",
    string CalendarWeekHeaderFormat = "ddd d/M",
    string RuntimeFormat = "hoursMinutes",
    string ShortDateFormat = "dmy",
    string LongDateFormat = "full",
    string TimeFormat = "12",
    bool ShowRelativeDates = true,
    // How often Deluno looks at whether the files it thinks you have are still
    // on disk. Configurable because the right answer depends on the disk: a
    // local pool can afford hourly; a NAS that spins up to answer should not be
    // woken every hour to be asked. DESIGN-007, the console's third section.
    int LibraryFileCheckHours = 6,
    // A FlareSolverr-compatible service that gets trackers past a Cloudflare
    // check (#628). Deluno does not solve those checks itself.
    string? CloudflareHelperUrl = null,
    // Download health (#659): configurable in the same spirit as Cleanuparr's
    // thresholds. Defaults match what #659 originally hard-coded, so an
    // install that never opens the settings screen behaves the same as
    // before. Only "no progress" starts on; the other two are sharper rules a
    // person opts into once they know their own swarms.
    bool DownloadHealthNoProgressEnabled = true,
    int DownloadHealthNoProgressMinutes = 30,
    bool DownloadHealthMinSpeedEnabled = false,
    int DownloadHealthMinSpeedKbps = 50,
    int DownloadHealthMinSpeedMinutes = 30,
    bool DownloadHealthMinPeersEnabled = false,
    int DownloadHealthMinPeers = 1,
    int DownloadHealthMinPeersMinutes = 30,
    // A library root running low on space fails title by title, at the worst
    // possible moment -- mid-import -- with no single place saying why (#909).
    // Warn is informational; at or below Pause, Deluno stops grabbing and
    // importing into that library the same way it does for an unreachable
    // root, and says so once, not per title.
    int LibraryFreeSpaceWarnPercent = 10,
    int LibraryFreeSpacePausePercent = 5,
    // #936: the one global switch for fetching subtitles on its own, asked in
    // the setup guide's "What should Deluno do by itself?" step and set here
    // in Settings -> Subtitles. Off by default for a new install; an install
    // that existed before this switch shipped keeps behaving as it always
    // did, because V0076SubtitleFetchDefaultsExisting writes an explicit
    // "true" for any such install that had not already stored a value — the
    // same technique V0073AutomationDefaultsOff used for renaming on import.
    // The per-library subtitle-language list still decides *which* languages
    // are wanted; this decides whether the automatic job that fetches them
    // is ever planned at all.
    bool SubtitleFetchAutomaticEnabled = false,
    // #757: Radarr/Sonarr's "Replace Illegal Characters" switch. On (the
    // historical behaviour) turns an illegal filename character into a dash;
    // off removes it instead. Default true so an install that has never
    // opened this setting keeps today's rendering.
    bool ReplaceIllegalCharacters = true,
    // #759: one of ColonReplacementModes, consulted only when
    // ReplaceIllegalCharacters is on. Smart Replace is Radarr/Sonarr's own
    // default.
    string ColonReplacementMode = ColonReplacementModes.SmartReplace,
    // #761: dated (daily) and absolute-numbered (anime) shows get their own
    // episode file template, the way Sonarr does. Falls back to
    // EpisodeFileFormat for a standard-numbered show.
    string DailyEpisodeFileFormat = "{Series Title} - {Air-Date} - {Episode Title} [{Quality}] [{Custom Formats}]",
    string AnimeEpisodeFileFormat = "{Series Title} - {absolute:000} - {Episode Title} [{Quality}] [{Custom Formats}]");
