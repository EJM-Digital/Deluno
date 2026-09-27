using Deluno.Contracts;

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
    // What happens once the sharing target is met (#821): remove the item
    // from the download client (the long-standing behaviour), or just stop
    // seeding it and leave the entry and its files alone.
    string SharingCompletionAction = SharingPolicy.CompletionReclaim,
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
    // The helper's own request timeout (#749), 1-180 seconds; the helper's
    // hard-coded 60-second SolveBudget before this was user-configurable.
    int CloudflareHelperTimeoutSeconds = 60,
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
    // #818: zero seeders, or the tracker itself saying unregistered, past a
    // grace period since the download was added - off by default (#936),
    // same as every other opt-in rule kind above.
    bool DownloadHealthDeadTorrentEnabled = false,
    int DownloadHealthDeadTorrentGraceMinutes = 60,
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
    string AnimeEpisodeFileFormat = "{Series Title} - {absolute:000} - {Episode Title} [{Quality}] [{Custom Formats}]",
    // #766: Radarr/Sonarr apply chmod/chown to an imported file when the
    // app's own process owns it. Off by default (#936): it changes file
    // ownership/permissions outside Deluno unless explicitly turned on, and
    // it only ever takes effect on non-Windows hosts where Deluno owns the file.
    bool SetPermissionsOnImport = false,
    string? FolderChmod = null,
    string? ChownGroup = null,
    // #767: a real three-way choice instead of Proper/Repack detection always
    // silently scoring. "prefer-and-upgrade" keeps today's behaviour;
    // "do-not-upgrade-automatically" still lets a Proper/Repack win among
    // releases found in the same search but never drives an unattended
    // upgrade of an already-installed file; "do-not-prefer" turns the
    // built-in scoring off entirely, leaving it to the user's own Custom
    // Formats. Read by ReleasePreferencePlanFactory when it compiles a
    // profile's guide-shipped Repack/Proper custom formats into a plan.
    string ProperRepackAcquisitionMode = "prefer-and-upgrade",
    // #905's remaining global settings, alongside the per-rule engine
    // (download-health.rules.v1, in IDownloadHealthRepository): the strike
    // pacing for a finding outside the rule engine (#823), a download's
    // grace period for going quiet before Deluno treats its problem as over
    // rather than merely paused (#825), a sandbox switch for the whole
    // strike/blocklist/seeding-cleanup pipeline (#814), and an internet
    // reachability guard before any cleanup pass runs at all (#824). Off/at
    // their old defaults so an existing install is unaffected (#936).
    bool DownloadHealthDryRunEnabled = false,
    int DownloadHealthDefaultStrikeWindowMinutes = 30,
    int DownloadHealthStrikeInactivityHours = 72,
    bool DownloadHealthConnectivityCheckEnabled = false,
    string DownloadHealthConnectivityCheckUrls = "",
    // #823's other half: the queue-throughput poll cadence, previously a
    // hardcoded PeriodicTimer interval.
    int DownloadThroughputSamplerIntervalMinutes = 1,
    // #860: the IANA zone episode air times are converted into for display
    // (e.g. "Australia/Sydney"). Empty means no zone chosen; every display
    // site falls back to the browser/OS timezone rather than UTC.
    string Timezone = "",
    // #932 (cleanup sheet row M1, decided 27 Sep 2026): a scheduled task that
    // deletes a leftover download once it is old enough and nothing at all is
    // still holding it. The decision sheet's own row said "Default on", but
    // #936 (James's later, standing rule, also 27 Sep 2026) is explicit that
    // every new install starts with every automatic behaviour off and asks
    // the setup guide instead - this is now one of those nine questions
    // ("Automatically delete leftover download files?"), so it defaults off
    // like the rest, and an existing install is never flipped on by this
    // setting shipping. Never touches an item unless every hold condition in
    // LeftoverDownloadCleanupService is met, once turned on.
    bool LeftoverDownloadCleanupEnabled = false,
    int LeftoverDownloadCleanupAgeHours = 24,
    // #762: per-season (and specials, season 0) subfolders under the series
    // folder, the way Sonarr's Season Folder Format and Specials Folder
    // Format split one combined series folder. Blank means no subfolder --
    // today's behaviour for every install that has never opened this
    // setting.
    string SeasonFolderFormat = "",
    string SpecialsFolderFormat = "",
    // #763: how a file spanning more than one episode renders its combined
    // season/episode segment. Range is Sonarr's own default.
    string MultiEpisodeStyle = "range",
    // #764: independent of RenameOnImport, which only ever governs the file
    // name. Off by default (#936): an install that has never opened this
    // setting keeps using the templated movie/series folder name for a new
    // title exactly as it always has -- the "off" behaviour only changes
    // anything when the source Deluno was handed was itself a folder with a
    // name of its own to keep.
    bool RenameFoldersOnImport = false,
    // #828: how long Deluno keeps its own event/strike history - Activity
    // feed rows and download-health strike records - before a background
    // job prunes them. A different concept from RetentionService, which
    // decides when media titles themselves are removed from the library;
    // this never touches a title or its files, only Deluno's own records
    // about what happened. Same pattern as ImportRecoveryRetentionDays.
    int HistoryRetentionDays = 365,
    /// <summary>#815: which tracker-privacy downloads the global sharing policy covers ("public" | "private" | "both"). "both" is every existing install's value, so nothing narrows until it is edited.</summary>
    string SharingPrivacyScope = DownloadHealthPrivacyScopes.Both,
    // #829: a single hourly search-call ceiling across every indexer
    // combined, alongside a library's own MaxItemsPerRun. Off by default
    // (#936); when on, the search scheduler defers a query once the
    // instance's rolling-hour total reaches this number.
    bool MaxSearchesPerHourEnabled = false,
    int MaxSearchesPerHour = 200,
    // Checkpoint A (#917): a brand-new check that can reject a release
    // outright, so off by default for every install (#936) until a person
    // turns it on. MinimumRuntimePercent is the "much shorter than known"
    // threshold's own setting, with the sensible default the issue names.
    bool DownloadIntegrityCheckEnabled = false,
    int DownloadIntegrityMinimumRuntimePercent = 80,
    // #765: Radarr/Sonarr's import-time free-space guard. The default
    // matches their 100MB floor. SkipFreeSpaceCheck exists for a filesystem
    // that cannot report free space at all (some network mounts and FUSE
    // layers return nothing usable) rather than for someone who merely
    // disagrees with the floor -- lower MinimumFreeSpaceMb for that instead.
    int MinimumFreeSpaceMb = 100,
    bool SkipFreeSpaceCheck = false,
    // #887: a still-packed archive is always blocklisted (governed by the
    // existing per-reason failure table, Settings -> Failure and blocklist),
    // but removing it from the download client and deleting its files was
    // never anybody's choice -- always "no". This is that choice, off by
    // default so an existing install's disk behaves exactly as before.
    bool RemoveAndDeletePackedArchivesFromClient = false,
    // #888 (cleanup sheet E5): what Deluno does with a file it downloaded but
    // could not match to a title or episode. "hold" is today's only
    // behaviour -- kept on Transfers for manual import, nothing else touched
    // -- so it stays the default for every existing and new install. The
    // same setting governs G4 (a processed file that no longer matches).
    string UnmatchedImportPolicy = UnmatchedImportPolicies.Hold,
    // How many days "give-up-after-days" waits before Deluno removes the
    // download, deletes the file, blocklists the release and searches again.
    int UnmatchedImportGiveUpDays = 7,
    // #879 (cleanup sheet row C6): the dangerous-or-junk-file rule kind in
    // the #905 engine, migrated the same way DownloadHealthNoProgressEnabled
    // and its siblings are - a flat legacy toggle an installation reads until
    // it saves its own rule through the (still-to-come) rule editor. Off by
    // default (#936): #816's content-blocklist check can surface a finding
    // on its own once turned on, but nothing strikes, removes, deletes or
    // blocklists on its own until this is explicitly turned on too.
    bool DownloadHealthDangerousFileEnabled = false);

/// <summary>The choices behind <see cref="PlatformSettingsSnapshot.UnmatchedImportPolicy"/> (#888).</summary>
public static class UnmatchedImportPolicies
{
    /// <summary>Today's only behaviour: hold it on Transfers for manual import.</summary>
    public const string Hold = "hold";

    /// <summary>Retry the match once, then treat it like any other bad release.</summary>
    public const string RetryOnceThenBad = "retry-once-then-bad";

    /// <summary>
    /// After <see cref="PlatformSettingsSnapshot.UnmatchedImportGiveUpDays"/>
    /// with nobody acting on it: remove the download, delete the file,
    /// blocklist the release, and search again.
    /// </summary>
    public const string GiveUpAfterDays = "give-up-after-days";

    public static bool IsKnown(string value) => value is Hold or RetryOnceThenBad or GiveUpAfterDays;
}
