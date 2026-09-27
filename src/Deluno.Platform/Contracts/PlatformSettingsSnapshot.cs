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
    // #936: the Dashboard shows a plain-words notice for every automatic
    // behaviour that reads off, each with "Turn on" and "Keep off". Keep off
    // must stick, so it is stored here rather than in the browser — a
    // comma-separated list of the notice keys the person has dismissed
    // (see AUTOMATION_QUESTIONS keys in setup-guide-page.tsx).
    string DismissedAutomationNotices = "");
