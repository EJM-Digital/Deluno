using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using Deluno.Security;
using Deluno.Security.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Platform.Data;

public sealed class SqlitePlatformSettingsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : IPlatformSettingsRepository
{
    // Normal Deluno installs use the managed metadata gateway. Direct provider
    // credentials remain a legacy/operator compatibility path, never a first-run
    // requirement for a media-library user.
    private const string ManagedMetadataBrokerUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev";

    public async Task<PlatformSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<SetupProgressItem> GetSetupProgressAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        return CreateSetupProgress(settings);
    }

    public async Task<SetupProgressItem> SaveSetupProgressAsync(
        UpdateSetupProgressRequest request,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();
        // #1034 split the guide into two stages, so it now has 12 steps
        // (indices 0-11) and the clamp moved from 6 to 11, and the Start step made it 13 (0-12).
        var lastCompletedStep = Math.Clamp(request.LastCompletedStep, 0, 12);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertSettingAsync(connection, transaction, "setup.lastCompletedStep", lastCompletedStep.ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "setup.isSkipped", request.IsSkipped ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "setup.isCompleted", request.IsCompleted ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "setup.updatedUtc", updatedUtc.ToString("O"), updatedUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SetupProgressItem(lastCompletedStep, request.IsSkipped, request.IsCompleted, updatedUtc);
    }

    public async Task<SetupDraftItem> GetSetupDraftAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        if (!settings.TryGetValue("setup.draft.v1", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return new SetupDraftItem();
        }

        try
        {
            return JsonSerializer.Deserialize<SetupDraftItem>(json) ?? new SetupDraftItem();
        }
        catch (JsonException)
        {
            return new SetupDraftItem();
        }
    }

    public async Task<SetupDraftItem> SaveSetupDraftAsync(
        UpdateSetupDraftRequest request,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();
        var draft = new SetupDraftItem(
            Mode: NormalizeSetupChoice(request.Mode, "simple", "simple", "advanced"),
            MediaIntent: NormalizeSetupChoice(request.MediaIntent, "both", "movies", "tv", "both"),
            MovieRootPath: NormalizeSetupText(request.MovieRootPath),
            SeriesRootPath: NormalizeSetupText(request.SeriesRootPath),
            DownloadsPath: NormalizeSetupText(request.DownloadsPath),
            MovieDownloadsPath: NormalizeSetupText(request.MovieDownloadsPath),
            SeriesDownloadsPath: NormalizeSetupText(request.SeriesDownloadsPath),
            QualityPreset: NormalizeSetupChoice(request.QualityPreset, "", "", "balanced1080p", "premium4k"),
            FormatGoal: NormalizeSetupChoice(request.FormatGoal, "", "", "simpleClean", "balanced", "homeTheater", "storageSaver", "anime", "archive"),
            IndexerName: NormalizeSetupText(request.IndexerName),
            IndexerProtocol: NormalizeSetupChoice(request.IndexerProtocol, "torznab", "torznab", "newznab", "rss"),
            IndexerUrl: NormalizeSetupText(request.IndexerUrl),
            ClientName: NormalizeSetupText(request.ClientName),
            ClientProtocol: NormalizeSetupChoice(request.ClientProtocol, "qbittorrent", "qbittorrent", "sabnzbd", "transmission", "deluge", "nzbget", "utorrent"),
            ClientHost: NormalizeSetupText(request.ClientHost),
            ClientPort: NormalizeSetupText(request.ClientPort),
            MetadataProviderMode: NormalizeSetupChoice(request.MetadataProviderMode, "direct", "broker", "hybrid", "direct"),
            MetadataBrokerUrl: NormalizeSetupText(request.MetadataBrokerUrl),
            BackupEnabled: request.BackupEnabled,
            FirstTitleType: NormalizeSetupChoice(request.FirstTitleType, "movies", "movies", "tv"),
            FirstTitle: NormalizeSetupText(request.FirstTitle),
            FirstTitleYear: NormalizeSetupText(request.FirstTitleYear),
            FirstTitleMonitored: request.FirstTitleMonitored,
            SkipSignInLocalNetwork: request.SkipSignInLocalNetwork,
            UpdateAutomatically: request.UpdateAutomatically,
            Automation: request.Automation is null
                ? null
                : request.Automation
                    .Where(pair => pair.Key.Length is > 0 and <= 64)
                    .Take(64)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
            UpdatedUtc: updatedUtc);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, "setup.draft.v1", JsonSerializer.Serialize(draft), updatedUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return draft;
    }

    public async Task ClearSetupDraftAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", "setup.draft.v1");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformSettingsSnapshot> SaveAsync(
        UpdatePlatformSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertSettingAsync(connection, transaction, "jobs.autoStart", request.AutoStartJobs ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "notifications.enabled", request.EnableNotifications ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.renameOnImport", request.RenameOnImport ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.useHardlinks", request.UseHardlinks ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.cleanupEmptyFolders", request.CleanupEmptyFolders ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.movieFolderFormat", NormalizeName(request.MovieFolderFormat) ?? "{Movie Title} ({Release Year})", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.seriesFolderFormat", NormalizeName(request.SeriesFolderFormat) ?? "{Series Title} ({Series Year})", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.movieFileFormat", NormalizeName(request.MovieFileFormat) ?? "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.episodeFileFormat", NormalizeName(request.EpisodeFileFormat) ?? "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}] [{Custom Formats}]", updatedUtc, cancellationToken);
        // #757: "unless false" like UseHardlinks -- a brand-new install with no
        // stored key keeps today's replace-with-dash rendering.
        await UpsertSettingAsync(connection, transaction, "media.replaceIllegalCharacters", request.ReplaceIllegalCharacters is false ? "false" : "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.colonReplacementMode", ColonReplacementModes.Normalize(request.ColonReplacementMode), updatedUtc, cancellationToken);
        // #761: dated and absolute-numbered shows get their own episode
        // template, falling back to the standard one above.
        await UpsertSettingAsync(connection, transaction, "media.dailyEpisodeFileFormat", NormalizeName(request.DailyEpisodeFileFormat) ?? "{Series Title} - {Air-Date} - {Episode Title} [{Quality}] [{Custom Formats}]", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.animeEpisodeFileFormat", NormalizeName(request.AnimeEpisodeFileFormat) ?? "{Series Title} - {absolute:000} - {Episode Title} [{Quality}] [{Custom Formats}]", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.bindAddress", NormalizeName(request.HostBindAddress) ?? "127.0.0.1", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.port", NormalizePositiveValue(request.HostPort, 5099).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.urlBase", NormalizeName(request.UrlBase) ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "security.requireAuthentication", "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.theme", NormalizeUiTheme(request.UiTheme), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.colorMode", NormalizeUiColorMode(request.UiColorMode), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.language", NormalizeUiLanguage(request.UiLanguage), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.calendarFirstDay", NormalizeCalendarFirstDay(request.CalendarFirstDayOfWeek), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.calendarWeekHeader", NormalizeCalendarWeekHeader(request.CalendarWeekHeaderFormat), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.runtimeFormat", NormalizeRuntimeFormat(request.RuntimeFormat), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.shortDateFormat", NormalizeShortDateFormat(request.ShortDateFormat), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.longDateFormat", NormalizeLongDateFormat(request.LongDateFormat), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.timeFormat", NormalizeTimeFormat(request.TimeFormat), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.relativeDates", request.ShowRelativeDates is false ? "false" : "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.defaultMovieView", NormalizeUiView(request.DefaultMovieView), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.defaultShowView", NormalizeUiView(request.DefaultShowView), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.certificationCountry", NormalizeName(request.MetadataCertificationCountry) ?? "US", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.language", NormalizeName(request.MetadataLanguage) ?? "en", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.providerMode", NormalizeMetadataProviderMode(request.MetadataProviderMode), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.brokerUrl", NormalizeMetadataBrokerUrl(request.MetadataBrokerUrl) ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "search.neverGrabPatterns", NormalizeNeverGrabPatterns(request.ReleaseNeverGrabPatterns), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "search.scoringMode", SearchScoringModes.Normalize(request.SearchScoringMode), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.importRecoveryRetentionDays", NormalizePositiveValue(request.ImportRecoveryRetentionDays ?? 30, 30).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // Clamped to the same 1..168 hours SystemTasks.IntervalForHours accepts,
        // so a value that survives being saved is a value the scheduler will
        // actually use. Two different bounds would mean the screen could show a
        // cadence Deluno never runs at.
        await UpsertSettingAsync(connection, transaction, "search.cloudflareHelperUrl", NormalizeName(request.CloudflareHelperUrl)?.TrimEnd('/') ?? string.Empty, updatedUtc, cancellationToken);
        // #749: the helper's own SolveBudget, previously a hard-coded 60
        // seconds. Clamped the same way on save and on read, so a value that
        // survives being saved is a value the helper will actually use.
        await UpsertSettingAsync(connection, transaction, "search.cloudflareHelperTimeoutSeconds", Math.Clamp(request.CloudflareHelperTimeoutSeconds ?? 60, 1, 180).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #1029: which helper answers at that address -- FlareSolverr, Byparr
        // (which silently ignores sessions, POST bodies and a per-request
        // proxy), or "auto" so Deluno tells them apart itself by probing it.
        await UpsertSettingAsync(connection, transaction, "search.cloudflareHelperKind", CloudflareHelperKinds.Normalize(request.CloudflareHelperKind), updatedUtc, cancellationToken);
        // #766: matches Radarr/Sonarr's Permissions section. Off by default
        // (#936); the chmod/chown themselves only ever run on a non-Windows
        // host, in ImportPipelineService, and only when Deluno's own process
        // owns the imported file.
        await UpsertSettingAsync(connection, transaction, "media.setPermissionsOnImport", request.SetPermissionsOnImport == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.folderChmod", NormalizeChmod(request.FolderChmod) ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.chownGroup", NormalizeName(request.ChownGroup) ?? string.Empty, updatedUtc, cancellationToken);
        // #767: a real Proper/Repack acquisition choice, read by
        // ReleasePreferencePlanFactory when it compiles a profile's plan.
        await UpsertSettingAsync(connection, transaction, "search.properRepackAcquisitionMode", NormalizeProperRepackAcquisitionMode(request.ProperRepackAcquisitionMode), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "library.fileCheckHours", Math.Clamp(request.LibraryFileCheckHours ?? 6, 1, 168).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.strikeThreshold", Math.Clamp(request.DownloadHealthStrikeThreshold ?? 3, 1, 100).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.blockReleaseAfterThreshold", request.CleanupBlockReleaseAfterThreshold is true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.queueReplacementAfterThreshold", request.CleanupQueueReplacementAfterThreshold is true ? "true" : "false", updatedUtc, cancellationToken);
        // Left as it is when a caller does not send it, so an older screen saving everything else cannot flip a choice.
        if (request.CheckConnectionsRegularly is { } checkRegularly)
        {
            await UpsertSettingAsync(connection, transaction, "connections.checkRegularly", checkRegularly ? "true" : "false", updatedUtc, cancellationToken);
        }

        if (request.KeepComputerAwake is { } keepAwake)
        {
            await UpsertSettingAsync(connection, transaction, "machine.keepAwake", keepAwake ? "true" : "false", updatedUtc, cancellationToken);
        }

        await UpsertSettingAsync(connection, transaction, "cleanup.removeClientEntryAfterThreshold", request.CleanupRemoveClientEntryAfterThreshold == true ? "true" : "false", updatedUtc, cancellationToken);
        // #659: each rule's own on/off switch plus its threshold, mirroring
        // the strike threshold above. Minutes are clamped to the same 1..1440
        // (a day) DownloadHealthRulesSettings itself clamps to, so a value
        // that survives being saved is one the shared rule will actually use.
        await UpsertSettingAsync(connection, transaction, "download-health.noProgress.enabled", request.DownloadHealthNoProgressEnabled is true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.noProgress.minutes", Math.Clamp(request.DownloadHealthNoProgressMinutes ?? 30, 1, 1440).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.minSpeed.enabled", request.DownloadHealthMinSpeedEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.minSpeed.kbps", Math.Clamp(request.DownloadHealthMinSpeedKbps ?? 50, 1, 1_000_000).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.minSpeed.minutes", Math.Clamp(request.DownloadHealthMinSpeedMinutes ?? 30, 1, 1440).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.minPeers.enabled", request.DownloadHealthMinPeersEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.minPeers.count", Math.Clamp(request.DownloadHealthMinPeers ?? 1, 0, 1000).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.minPeers.minutes", Math.Clamp(request.DownloadHealthMinPeersMinutes ?? 30, 1, 1440).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #818: zero seeders, or the tracker itself saying unregistered, past
        // a grace period since the download was added. Off by default (#936).
        await UpsertSettingAsync(connection, transaction, "download-health.deadTorrent.enabled", request.DownloadHealthDeadTorrentEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.deadTorrent.graceMinutes", Math.Clamp(request.DownloadHealthDeadTorrentGraceMinutes ?? 60, 0, 1440).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #905's remaining global settings: the per-rule engine itself lives
        // in IDownloadHealthRepository (download-health.rules.v1), these are
        // what sits alongside it. Off/at their old defaults (#936).
        await UpsertSettingAsync(connection, transaction, "download-health.dryRunEnabled", request.DownloadHealthDryRunEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.defaultStrikeWindowMinutes", Math.Clamp(request.DownloadHealthDefaultStrikeWindowMinutes ?? 5, 1, 1440).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.strikeInactivityHours", Math.Clamp(request.DownloadHealthStrikeInactivityHours ?? 72, 1, 720).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.connectivityCheck.enabled", request.DownloadHealthConnectivityCheckEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.connectivityCheck.urls", request.DownloadHealthConnectivityCheckUrls ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-health.throughputSampler.intervalMinutes", Math.Clamp(request.DownloadThroughputSamplerIntervalMinutes ?? 1, 1, 60).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #860: an IANA zone id, or empty to fall back to the browser/OS
        // timezone at display time. Validated loosely (TimeZoneInfo does the
        // real check) so a typo clears to "unset" rather than storing garbage
        // silently mistaken for a real zone.
        await UpsertSettingAsync(connection, transaction, "ui.timezone", NormalizeTimezone(request.Timezone), updatedUtc, cancellationToken);
        // #932/#936: off by default, the same as the other setup-guide
        // automation questions - a missing key reads as off.
        await UpsertSettingAsync(connection, transaction, "downloads.leftoverCleanup.enabled", request.LeftoverDownloadCleanupEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "downloads.leftoverCleanup.ageHours", Math.Clamp(request.LeftoverDownloadCleanupAgeHours ?? 24, 1, 8760).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #1001: a skip list and the age an entry needs before it counts at all.
        // A missing key reads as no skip list and the 2 hours it always had.
        await UpsertSettingAsync(connection, transaction, "downloads.leftoverCleanup.excludePatterns", (request.LeftoverExcludePatterns ?? string.Empty).Trim(), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "downloads.leftoverCleanup.minAgeHours", Math.Clamp(request.LeftoverMinAgeHours ?? 2, 1, 8760).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #1013: off unless the key says "true"; a missing key reads as off.
        await UpsertSettingAsync(connection, transaction, "imports.forceImport.enabled", request.ForceImportEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "imports.forceImport.maxTries", Math.Clamp(request.ForceImportMaxTries ?? 3, 1, 25).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "imports.forceImport.minimumMegabytes", Math.Clamp(request.ForceImportMinimumMegabytes is > 0 ? request.ForceImportMinimumMegabytes.Value : 100, 1, 100000).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #762: blank means no season/specials subfolder, the same "off"
        // value a brand-new install already reads for every other naming
        // format below.
        await UpsertSettingAsync(connection, transaction, "media.seasonFolderFormat", request.SeasonFolderFormat ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.specialsFolderFormat", request.SpecialsFolderFormat ?? string.Empty, updatedUtc, cancellationToken);
        // #763: one of Deluno.Series.Contracts.MultiEpisodeStyles. Not
        // referenced directly -- Deluno.Platform does not depend on
        // Deluno.Series -- so this repeats that small, stable value set the
        // same way ColonReplacementMode's own normalizer does not reach into
        // another module either.
        await UpsertSettingAsync(connection, transaction, "media.multiEpisodeStyle", NormalizeMultiEpisodeStyle(request.MultiEpisodeStyle), updatedUtc, cancellationToken);
        // #764: off by default (#936), independent of media.renameOnImport.
        await UpsertSettingAsync(connection, transaction, "media.renameFoldersOnImport", request.RenameFoldersOnImport == true ? "true" : "false", updatedUtc, cancellationToken);
        // #828: Deluno's own event/strike history, not RetentionService's title-removal marks.
        await UpsertSettingAsync(connection, transaction, "history.retentionDays", Math.Clamp(request.HistoryRetentionDays ?? 365, 1, 3650).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "sharing.privacyScope", DownloadHealthPrivacyScopes.IsKnown(request.SharingPrivacyScope ?? string.Empty) ? request.SharingPrivacyScope! : DownloadHealthPrivacyScopes.Both, updatedUtc, cancellationToken);
        // #829: off by default (#936); the search scheduler only reads the
        // number when the switch is on.
        await UpsertSettingAsync(connection, transaction, "search.maxSearchesPerHour.enabled", request.MaxSearchesPerHourEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "search.maxSearchesPerHour", Math.Clamp(request.MaxSearchesPerHour ?? 200, 1, 100_000).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // Checkpoint A (#917): off by default (#936).
        await UpsertSettingAsync(connection, transaction, "download-integrity.checkEnabled", request.DownloadIntegrityCheckEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "download-integrity.minimumRuntimePercent", Math.Clamp(request.DownloadIntegrityMinimumRuntimePercent ?? 80, 1, 100).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #765: matches Radarr/Sonarr's import-time free-space guard (100MB
        // default floor). SkipFreeSpaceCheck is for a filesystem that cannot
        // report free space at all, not a substitute for lowering the floor.
        await UpsertSettingAsync(connection, transaction, "import.minimumFreeSpaceMb", Math.Max(request.MinimumFreeSpaceMb ?? 100, 0).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "import.skipFreeSpaceCheck", request.SkipFreeSpaceCheck == true ? "true" : "false", updatedUtc, cancellationToken);
        // #887: off by default -- a still-packed archive is always blocklisted,
        // but removing/deleting it from the client was never anybody's choice.
        await UpsertSettingAsync(connection, transaction, "cleanup.removeAndDeletePackedArchivesFromClient", request.RemoveAndDeletePackedArchivesFromClient == true ? "true" : "false", updatedUtc, cancellationToken);
        // #888: "hold" is today's only behaviour, so it is every install's
        // default until somebody chooses otherwise.
        await UpsertSettingAsync(connection, transaction, "import.unmatchedPolicy", UnmatchedImportPolicies.IsKnown(request.UnmatchedImportPolicy ?? string.Empty) ? request.UnmatchedImportPolicy! : UnmatchedImportPolicies.Hold, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "import.unmatchedGiveUpDays", Math.Clamp(request.UnmatchedImportGiveUpDays ?? 7, 1, 365).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #879: off by default (#936); migrated into the #905 rule engine by
        // IDownloadHealthRepository.GetDownloadHealthRulesAsync the same way
        // the older download-health.* legacy toggles already are.
        await UpsertSettingAsync(connection, transaction, "download-health.dangerousFile.enabled", request.DownloadHealthDangerousFileEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        // #1046: off by default (#936), for existing installs too: a missing key reads as off.
        await UpsertSettingAsync(connection, transaction, "lists.tagTitlesOffEveryList", request.TagTitlesOffEveryList == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "lists.offListTag", (request.OffListTag ?? string.Empty).Trim(), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.showAbsoluteEpisodeNumbers", request.ShowAbsoluteEpisodeNumbers == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "search.pauseWhenQueueLong.enabled", request.PauseSearchWhenQueueLongEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "search.pauseWhenQueueLong.threshold", Math.Clamp(request.PauseSearchWhenQueueLongThreshold ?? 20, 1, 10_000).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #909: warn and pause both read as "percent free remaining", so pause
        // must be the smaller number -- clamped here rather than merely
        // validated, so a stored value can never be read back the wrong way
        // round even if validation is ever bypassed.
        await UpsertSettingAsync(connection, transaction, "library.freeSpace.warnPercent", Math.Clamp(request.LibraryFreeSpaceWarnPercent ?? 10, 1, 50).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "library.freeSpace.pausePercent", Math.Clamp(Math.Min(request.LibraryFreeSpacePausePercent ?? 5, request.LibraryFreeSpaceWarnPercent ?? 10), 1, 50).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        // #936: the one global switch for automatic subtitle fetching, set in
        // Settings -> Subtitles. Off unless a value has been stored — see
        // SubtitleFetchAutomaticEnabled on PlatformSettingsSnapshot for why a
        // missing value here does not mean the same thing for every existing
        // install.
        await UpsertSettingAsync(connection, transaction, "subtitles.fetchAutomatically", request.SubtitleFetchAutomaticEnabled == true ? "true" : "false", updatedUtc, cancellationToken);
        // The sharing rule is written only when the caller is actually setting
        // it. Writing it on every settings PATCH meant that saving anything
        // else — a metadata key, a rename format — silently cleared both
        // targets, because "absent" and "deliberately cleared" are stored the
        // same way and an untouched PATCH carries neither. Mode is the marker:
        // the whole rule is submitted together or not at all.
        if (!string.IsNullOrWhiteSpace(request.SharingMode))
        {
            await UpsertSettingAsync(connection, transaction, "sharing.mode", SharingPolicy.NormalizeMode(request.SharingMode), updatedUtc, cancellationToken);
            await UpsertSettingAsync(connection, transaction, "sharing.forHours", request.SharingForHours?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, updatedUtc, cancellationToken);
            await UpsertSettingAsync(connection, transaction, "sharing.untilRatio", request.SharingUntilRatio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, updatedUtc, cancellationToken);
            await UpsertSettingAsync(connection, transaction, "sharing.stuckAction", SharingPolicy.NormalizeStuckAction(request.SharingStuckAction), updatedUtc, cancellationToken);
            await UpsertSettingAsync(connection, transaction, "sharing.stuckAfterDays", (request.SharingStuckAfterDays is > 0 ? request.SharingStuckAfterDays.Value : SharingPolicy.Default.StuckAfterDays).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        }
        // Independent of the rule above (#821): whether reaching a target
        // reclaims the download or just stops seeding it can be changed on
        // its own, without resubmitting the whole sharing rule.
        if (!string.IsNullOrWhiteSpace(request.SharingCompletionAction))
        {
            await UpsertSettingAsync(connection, transaction, "sharing.completionAction", SharingPolicy.NormalizeCompletionAction(request.SharingCompletionAction), updatedUtc, cancellationToken);
        }
        await UpsertSettingAsync(connection, transaction, "cleanup.purgePayloadAfterThreshold", request.CleanupPurgePayloadAfterThreshold == true ? "true" : "false", updatedUtc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.MetadataTmdbApiKey))
        {
            await UpsertSettingAsync(
                connection,
                transaction,
                "metadata.tmdbApiKey",
                secretProtector.Protect("metadata:tmdb", request.MetadataTmdbApiKey.Trim()),
                updatedUtc,
                cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(request.MetadataOmdbApiKey))
        {
            await UpsertSettingAsync(
                connection,
                transaction,
                "metadata.omdbApiKey",
                secretProtector.Protect("metadata:omdb", request.MetadataOmdbApiKey.Trim()),
                updatedUtc,
                cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(request.MdbListApiKey))
        {
            await UpsertSettingAsync(
                connection,
                transaction,
                "intake.mdblistApiKey",
                secretProtector.Protect("intake:mdblist", request.MdbListApiKey.Trim()),
                updatedUtc,
                cancellationToken);
        }

        await UpsertRootAsync(connection, transaction, "movies", NormalizePath(request.MovieRootPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "series", NormalizePath(request.SeriesRootPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "downloads", NormalizePath(request.DownloadsPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "downloads.incomplete", NormalizePath(request.IncompleteDownloadsPath), updatedUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<PlatformSettingsSnapshot> SetGlobalAutomationEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, "jobs.autoStart", isEnabled ? "true" : "false", updatedUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<PlatformSettingsSnapshot> MarkWorkflowVerifiedAsync(
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, "setup.workflowVerified", "true", updatedUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<string?> GetMetadataProviderSecretAsync(string provider, CancellationToken cancellationToken)
    {
        var settingKey = provider.Trim().ToLowerInvariant() switch
        {
            "tmdb" => "metadata.tmdbApiKey",
            "omdb" => "metadata.omdbApiKey",
            "mdblist" => "intake.mdblistApiKey",
            _ => null
        };

        if (settingKey is null)
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @settingKey;";
        AddParameter(command, "@settingKey", settingKey);
        var stored = await command.ExecuteScalarAsync(cancellationToken) as string;
        var purpose = string.Equals(provider, "mdblist", StringComparison.OrdinalIgnoreCase) ? "intake:mdblist" : $"metadata:{provider.Trim().ToLowerInvariant()}";
        // A key saved on another computer cannot be read here: no key, never a failure (#1094).
        return secretProtector.TryUnprotect(purpose, stored).Value;
    }

    public async Task<IReadOnlyList<TagItem>> ListTagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<TagItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, color, description, created_utc, updated_utc
            FROM tags
            ORDER BY name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadTag(reader));
        }

        return items;
    }

    public async Task<TagItem> CreateTagAsync(
        CreateTagRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new TagItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New tag",
            Color: NormalizeTagColor(request.Color),
            Description: NormalizeName(request.Description) ?? string.Empty,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tags (
                id, name, color, description, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @color, @description, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@color", item.Color);
        AddParameter(command, "@description", item.Description);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<TagItem?> UpdateTagAsync(
        string id,
        UpdateTagRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetTagAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tags
            SET
                name = @name,
                color = @color,
                description = @description,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@color", NormalizeTagColor(request.Color ?? current.Color));
        AddParameter(command, "@description", NormalizeName(request.Description) ?? string.Empty);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetTagAsync(connection, id, cancellationToken);
    }

    public async Task<bool> DeleteTagAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tags WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private PlatformSettingsSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> roots)
    {
        var brokerUrl = NormalizeMetadataBrokerUrl(GetValue(settings, "metadata.brokerUrl")) ?? ManagedMetadataBrokerUrl;
        var tmdbKey = secretProtector.TryUnprotect("metadata:tmdb", GetValue(settings, "metadata.tmdbApiKey"));
        var omdbKey = secretProtector.TryUnprotect("metadata:omdb", GetValue(settings, "metadata.omdbApiKey"));
        var mdbKey = secretProtector.TryUnprotect("intake:mdblist", GetValue(settings, "intake.mdblistApiKey"));
        var unreadableKeys = new List<string>();
        if (tmdbKey.Unreadable) unreadableKeys.Add("TMDb API key");
        if (omdbKey.Unreadable) unreadableKeys.Add("OMDb API key");
        if (mdbKey.Unreadable) unreadableKeys.Add("MDbList API key");
        var keyProblem = unreadableKeys.Count == 0
            ? null
            : $"The saved {string.Join(" and the saved ", unreadableKeys)} was stored on another computer and can't be read here. Enter it again.";

        return new PlatformSettingsSnapshot(
            MovieRootPath: GetValue(roots, "movies"),
            SeriesRootPath: GetValue(roots, "series"),
            DownloadsPath: GetValue(roots, "downloads"),
            IncompleteDownloadsPath: GetValue(roots, "downloads.incomplete"),
            AutoStartJobs: string.Equals(GetValue(settings, "jobs.autoStart"), "true", StringComparison.OrdinalIgnoreCase),
            EnableNotifications: string.Equals(GetValue(settings, "notifications.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            // #936 reverses this to "off unless explicitly turned on". A
            // brand-new install reads a missing key as off; an install that
            // existed before #936 shipped keeps its old "on" behaviour
            // because V0073AutomationDefaultsOff wrote an explicit "true" for
            // any such install that had not already stored a value.
            RenameOnImport: string.Equals(GetValue(settings, "media.renameOnImport"), "true", StringComparison.OrdinalIgnoreCase),
            UseHardlinks: !string.Equals(GetValue(settings, "media.useHardlinks"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupEmptyFolders: string.Equals(GetValue(settings, "media.cleanupEmptyFolders"), "true", StringComparison.OrdinalIgnoreCase),
            MovieFolderFormat: GetValue(settings, "media.movieFolderFormat") ?? "{Movie Title} ({Release Year})",
            SeriesFolderFormat: GetValue(settings, "media.seriesFolderFormat") ?? "{Series Title} ({Series Year})",
            MovieFileFormat: GetValue(settings, "media.movieFileFormat") ?? "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
            EpisodeFileFormat: GetValue(settings, "media.episodeFileFormat") ?? "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}] [{Custom Formats}]",
            HostBindAddress: GetValue(settings, "host.bindAddress") ?? "127.0.0.1",
            HostPort: int.TryParse(GetValue(settings, "host.port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort) ? hostPort : 5099,
            UrlBase: GetValue(settings, "host.urlBase") ?? string.Empty,
            RequireAuthentication: true,
            UiTheme: NormalizeUiTheme(GetValue(settings, "ui.theme")),
            DefaultMovieView: NormalizeUiView(GetValue(settings, "ui.defaultMovieView")),
            DefaultShowView: NormalizeUiView(GetValue(settings, "ui.defaultShowView")),
            MetadataCertificationCountry: NormalizeName(GetValue(settings, "metadata.certificationCountry")) ?? "US",
            MetadataLanguage: NormalizeName(GetValue(settings, "metadata.language")) ?? "en",
            MetadataProviderMode: NormalizeMetadataProviderMode(GetValue(settings, "metadata.providerMode") ?? "broker"),
            MetadataBrokerUrl: brokerUrl,
            MetadataBrokerConfigured: !string.IsNullOrWhiteSpace(brokerUrl),
            MetadataTmdbApiKeyConfigured: tmdbKey.Value is not null,
            MetadataTmdbApiKeyUnreadable: tmdbKey.Unreadable,
            MetadataOmdbApiKeyConfigured: omdbKey.Value is not null,
            MetadataOmdbApiKeyUnreadable: omdbKey.Unreadable,
            ReleaseNeverGrabPatterns: NormalizeNeverGrabPatterns(GetValue(settings, "search.neverGrabPatterns")),
            SearchScoringMode: SearchScoringModes.Normalize(GetValue(settings, "search.scoringMode")),
            ImportRecoveryRetentionDays: int.TryParse(GetValue(settings, "media.importRecoveryRetentionDays"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays) && retentionDays > 0 ? retentionDays : 30,
            LibraryFileCheckHours: int.TryParse(GetValue(settings, "library.fileCheckHours"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileCheckHours) && fileCheckHours > 0 ? Math.Clamp(fileCheckHours, 1, 168) : 6,
            CloudflareHelperUrl: string.IsNullOrWhiteSpace(GetValue(settings, "search.cloudflareHelperUrl")) ? null : GetValue(settings, "search.cloudflareHelperUrl"),
            CloudflareHelperTimeoutSeconds: int.TryParse(GetValue(settings, "search.cloudflareHelperTimeoutSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var helperTimeoutSeconds) && helperTimeoutSeconds > 0 ? Math.Clamp(helperTimeoutSeconds, 1, 180) : 60,
            CloudflareHelperKind: CloudflareHelperKinds.Normalize(GetValue(settings, "search.cloudflareHelperKind")),
            UpdatedUtc: DateTimeOffset.UtcNow,
            MdbListApiKeyConfigured: mdbKey.Value is not null,
            MdbListApiKeyUnreadable: mdbKey.Unreadable,
            MetadataKeyProblem: keyProblem,
            DownloadHealthStrikeThreshold: ReadDownloadHealthStrikeThreshold(settings),
            CleanupBlockReleaseAfterThreshold: string.Equals(GetValue(settings, "cleanup.blockReleaseAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase),
            CleanupQueueReplacementAfterThreshold: string.Equals(GetValue(settings, "cleanup.queueReplacementAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase),
            CheckConnectionsRegularly: !string.Equals(GetValue(settings, "connections.checkRegularly"), "false", StringComparison.OrdinalIgnoreCase),
            KeepComputerAwake: string.Equals(GetValue(settings, "machine.keepAwake"), "true", StringComparison.OrdinalIgnoreCase),
            // #904's "default on" change (commit 2e9f2125) never shipped in a
            // release: v1.0.0-rc.22 already read a missing key here as off
            // (this same "true" comparison), so #936 does not need to change
            // this line or freeze anything for it — a missing value has
            // always read as off in every release anyone has actually run.
            CleanupRemoveClientEntryAfterThreshold: string.Equals(GetValue(settings, "cleanup.removeClientEntryAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase),
            SharingMode: SharingPolicy.NormalizeMode(GetValue(settings, "sharing.mode")),
            SharingForHours: ReadOptionalInt(GetValue(settings, "sharing.forHours"), SharingPolicy.Default.ForHours),
            SharingUntilRatio: ReadOptionalDouble(GetValue(settings, "sharing.untilRatio")),
            SharingStuckAction: SharingPolicy.NormalizeStuckAction(GetValue(settings, "sharing.stuckAction")),
            SharingStuckAfterDays: ReadOptionalInt(GetValue(settings, "sharing.stuckAfterDays"), SharingPolicy.Default.StuckAfterDays) ?? SharingPolicy.Default.StuckAfterDays,
            SharingCompletionAction: SharingPolicy.NormalizeCompletionAction(GetValue(settings, "sharing.completionAction")),
            // Same reasoning as CleanupRemoveClientEntryAfterThreshold above:
            // #904's "default on" never shipped, so this line and a missing
            // value's meaning are unchanged by #936.
            CleanupPurgePayloadAfterThreshold: string.Equals(GetValue(settings, "cleanup.purgePayloadAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase),
            WorkflowVerified: string.Equals(GetValue(settings, "setup.workflowVerified"), "true", StringComparison.OrdinalIgnoreCase),
            UiColorMode: NormalizeUiColorMode(GetValue(settings, "ui.colorMode")),
            UiLanguage: NormalizeUiLanguage(GetValue(settings, "ui.language")),
            CalendarFirstDayOfWeek: NormalizeCalendarFirstDay(GetValue(settings, "ui.calendarFirstDay")),
            CalendarWeekHeaderFormat: NormalizeCalendarWeekHeader(GetValue(settings, "ui.calendarWeekHeader")),
            RuntimeFormat: NormalizeRuntimeFormat(GetValue(settings, "ui.runtimeFormat")),
            ShortDateFormat: NormalizeShortDateFormat(GetValue(settings, "ui.shortDateFormat")),
            LongDateFormat: NormalizeLongDateFormat(GetValue(settings, "ui.longDateFormat")),
            TimeFormat: NormalizeTimeFormat(GetValue(settings, "ui.timeFormat")),
            ShowRelativeDates: !string.Equals(GetValue(settings, "ui.relativeDates"), "false", StringComparison.OrdinalIgnoreCase),
            DownloadHealthNoProgressEnabled: string.Equals(GetValue(settings, "download-health.noProgress.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadHealthNoProgressMinutes: ReadPositiveInt(GetValue(settings, "download-health.noProgress.minutes"), 30, 1, 1440),
            DownloadHealthMinSpeedEnabled: string.Equals(GetValue(settings, "download-health.minSpeed.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadHealthMinSpeedKbps: ReadPositiveInt(GetValue(settings, "download-health.minSpeed.kbps"), 50, 1, 1_000_000),
            DownloadHealthMinSpeedMinutes: ReadPositiveInt(GetValue(settings, "download-health.minSpeed.minutes"), 30, 1, 1440),
            DownloadHealthMinPeersEnabled: string.Equals(GetValue(settings, "download-health.minPeers.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadHealthMinPeers: ReadPositiveInt(GetValue(settings, "download-health.minPeers.count"), 1, 0, 1000),
            DownloadHealthMinPeersMinutes: ReadPositiveInt(GetValue(settings, "download-health.minPeers.minutes"), 30, 1, 1440),
            DownloadHealthDeadTorrentEnabled: string.Equals(GetValue(settings, "download-health.deadTorrent.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadHealthDeadTorrentGraceMinutes: ReadPositiveInt(GetValue(settings, "download-health.deadTorrent.graceMinutes"), 60, 0, 1440),
            LibraryFreeSpaceWarnPercent: ReadPositiveInt(GetValue(settings, "library.freeSpace.warnPercent"), 10, 1, 50),
            LibraryFreeSpacePausePercent: ReadPositiveInt(GetValue(settings, "library.freeSpace.pausePercent"), 5, 1, 50),
            SubtitleFetchAutomaticEnabled: string.Equals(GetValue(settings, "subtitles.fetchAutomatically"), "true", StringComparison.OrdinalIgnoreCase),
            ReplaceIllegalCharacters: !string.Equals(GetValue(settings, "media.replaceIllegalCharacters"), "false", StringComparison.OrdinalIgnoreCase),
            ColonReplacementMode: ColonReplacementModes.Normalize(GetValue(settings, "media.colonReplacementMode")),
            DailyEpisodeFileFormat: GetValue(settings, "media.dailyEpisodeFileFormat") ?? "{Series Title} - {Air-Date} - {Episode Title} [{Quality}] [{Custom Formats}]",
            AnimeEpisodeFileFormat: GetValue(settings, "media.animeEpisodeFileFormat") ?? "{Series Title} - {absolute:000} - {Episode Title} [{Quality}] [{Custom Formats}]",
            SetPermissionsOnImport: string.Equals(GetValue(settings, "media.setPermissionsOnImport"), "true", StringComparison.OrdinalIgnoreCase),
            FolderChmod: NormalizeChmod(GetValue(settings, "media.folderChmod")),
            ChownGroup: NormalizeName(GetValue(settings, "media.chownGroup")),
            ProperRepackAcquisitionMode: NormalizeProperRepackAcquisitionMode(GetValue(settings, "search.properRepackAcquisitionMode")),
            DownloadHealthDryRunEnabled: string.Equals(GetValue(settings, "download-health.dryRunEnabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadHealthDefaultStrikeWindowMinutes: ReadPositiveInt(GetValue(settings, "download-health.defaultStrikeWindowMinutes"), 5, 1, 1440),
            DownloadHealthStrikeInactivityHours: ReadPositiveInt(GetValue(settings, "download-health.strikeInactivityHours"), 72, 1, 720),
            DownloadHealthConnectivityCheckEnabled: string.Equals(GetValue(settings, "download-health.connectivityCheck.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadHealthConnectivityCheckUrls: GetValue(settings, "download-health.connectivityCheck.urls") ?? string.Empty,
            DownloadThroughputSamplerIntervalMinutes: ReadPositiveInt(GetValue(settings, "download-health.throughputSampler.intervalMinutes"), 1, 1, 60),
            Timezone: GetValue(settings, "ui.timezone") ?? string.Empty,
            LeftoverDownloadCleanupEnabled: string.Equals(GetValue(settings, "downloads.leftoverCleanup.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            LeftoverDownloadCleanupAgeHours: ReadPositiveInt(GetValue(settings, "downloads.leftoverCleanup.ageHours"), 24, 1, 8760),
            LeftoverExcludePatterns: GetValue(settings, "downloads.leftoverCleanup.excludePatterns") ?? string.Empty,
            LeftoverMinAgeHours: ReadPositiveInt(GetValue(settings, "downloads.leftoverCleanup.minAgeHours"), 2, 1, 8760),
            ForceImportEnabled: string.Equals(GetValue(settings, "imports.forceImport.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            ForceImportMaxTries: ReadPositiveInt(GetValue(settings, "imports.forceImport.maxTries"), 3, 1, 25),
            ForceImportMinimumMegabytes: ReadPositiveInt(GetValue(settings, "imports.forceImport.minimumMegabytes"), 100, 1, 100000),
            SeasonFolderFormat: GetValue(settings, "media.seasonFolderFormat") ?? string.Empty,
            SpecialsFolderFormat: GetValue(settings, "media.specialsFolderFormat") ?? string.Empty,
            MultiEpisodeStyle: NormalizeMultiEpisodeStyle(GetValue(settings, "media.multiEpisodeStyle")),
            RenameFoldersOnImport: string.Equals(GetValue(settings, "media.renameFoldersOnImport"), "true", StringComparison.OrdinalIgnoreCase),
            HistoryRetentionDays: ReadPositiveInt(GetValue(settings, "history.retentionDays"), 365, 1, 3650),
            SharingPrivacyScope: DownloadHealthPrivacyScopes.IsKnown(GetValue(settings, "sharing.privacyScope") ?? string.Empty)
                ? GetValue(settings, "sharing.privacyScope")!
                : DownloadHealthPrivacyScopes.Both,
            MaxSearchesPerHourEnabled: string.Equals(GetValue(settings, "search.maxSearchesPerHour.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            MaxSearchesPerHour: ReadPositiveInt(GetValue(settings, "search.maxSearchesPerHour"), 200, 1, 100_000),
            DownloadIntegrityCheckEnabled: string.Equals(GetValue(settings, "download-integrity.checkEnabled"), "true", StringComparison.OrdinalIgnoreCase),
            DownloadIntegrityMinimumRuntimePercent: ReadPositiveInt(GetValue(settings, "download-integrity.minimumRuntimePercent"), 80, 1, 100),
            MinimumFreeSpaceMb: ReadPositiveInt(GetValue(settings, "import.minimumFreeSpaceMb"), 100, 0, int.MaxValue),
            SkipFreeSpaceCheck: string.Equals(GetValue(settings, "import.skipFreeSpaceCheck"), "true", StringComparison.OrdinalIgnoreCase),
            RemoveAndDeletePackedArchivesFromClient: string.Equals(GetValue(settings, "cleanup.removeAndDeletePackedArchivesFromClient"), "true", StringComparison.OrdinalIgnoreCase),
            UnmatchedImportPolicy: UnmatchedImportPolicies.IsKnown(GetValue(settings, "import.unmatchedPolicy") ?? string.Empty)
                ? GetValue(settings, "import.unmatchedPolicy")!
                : UnmatchedImportPolicies.Hold,
            UnmatchedImportGiveUpDays: ReadPositiveInt(GetValue(settings, "import.unmatchedGiveUpDays"), 7, 1, 365),
            DownloadHealthDangerousFileEnabled: string.Equals(GetValue(settings, "download-health.dangerousFile.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            TagTitlesOffEveryList: string.Equals(GetValue(settings, "lists.tagTitlesOffEveryList"), "true", StringComparison.OrdinalIgnoreCase),
            OffListTag: (GetValue(settings, "lists.offListTag") ?? string.Empty).Trim(),
            ShowAbsoluteEpisodeNumbers: string.Equals(GetValue(settings, "ui.showAbsoluteEpisodeNumbers"), "true", StringComparison.OrdinalIgnoreCase),
            PauseSearchWhenQueueLongEnabled: string.Equals(GetValue(settings, "search.pauseWhenQueueLong.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            PauseSearchWhenQueueLongThreshold: ReadPositiveInt(GetValue(settings, "search.pauseWhenQueueLong.threshold"), 20, 1, 10_000));
    }

    private static string NormalizeMultiEpisodeStyle(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "extend" => "extend",
            "duplicate" => "duplicate",
            "repeat" => "repeat",
            "scene" => "scene",
            "prefixed-range" => "prefixed-range",
            _ => "range"
        };

    private static string NormalizeProperRepackAcquisitionMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "do-not-upgrade-automatically" => "do-not-upgrade-automatically",
            "do-not-prefer" => "do-not-prefer",
            _ => "prefer-and-upgrade"
        };

    /// <summary>Accepts a 3- or 4-digit octal mode (e.g. "755", "0644"); anything else is dropped rather than stored malformed.</summary>
    private static string? NormalizeChmod(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        return trimmed.Length is 3 or 4 && trimmed.All(digit => digit is >= '0' and <= '7')
            ? trimmed
            : null;
    }

    private static int ReadPositiveInt(string? value, int fallback, int min, int max)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static int ReadDownloadHealthStrikeThreshold(IReadOnlyDictionary<string, string> settings)
        => int.TryParse(GetValue(settings, "cleanup.strikeThreshold"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            ? Math.Clamp(threshold, 1, 100)
            : 3;

    private static SetupProgressItem CreateSetupProgress(IReadOnlyDictionary<string, string> settings)
    {
        // #1034: kept in step with the write-side clamp in SaveSetupProgressAsync.
        var lastCompletedStep = int.TryParse(GetValue(settings, "setup.lastCompletedStep"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, 12)
            : 0;
        var isSkipped = string.Equals(GetValue(settings, "setup.isSkipped"), "true", StringComparison.OrdinalIgnoreCase);
        var isCompleted = string.Equals(GetValue(settings, "setup.isCompleted"), "true", StringComparison.OrdinalIgnoreCase);
        var updatedUtc = DateTimeOffset.TryParse(
            GetValue(settings, "setup.updatedUtc"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedUpdatedUtc)
            ? parsedUpdatedUtc
            : DateTimeOffset.MinValue;
        return new SetupProgressItem(lastCompletedStep, isSkipped, isCompleted, updatedUtc);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadSettingsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM system_settings;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadRootsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_key, root_path FROM root_paths;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static async Task UpsertSettingAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        string value,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO system_settings (setting_key, setting_value, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRootAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        string? value,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (string.IsNullOrWhiteSpace(value))
        {
            command.CommandText = "DELETE FROM root_paths WHERE root_key = @key;";
            AddParameter(command, "@key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        command.CommandText =
            """
            INSERT INTO root_paths (root_key, root_path, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(root_key) DO UPDATE SET
                root_path = excluded.root_path,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TagItem?> GetTagAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, color, description, created_utc, updated_utc
            FROM tags
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTag(reader) : null;
    }

    private static string NormalizeSetupText(string? value)
        => NormalizeName(value) ?? string.Empty;

    private static string NormalizeSetupChoice(string? value, string fallback, params string[] allowed)
    {
        var normalized = NormalizeSetupText(value);
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }

    private static string NormalizeNeverGrabPatterns(string? value)
    {
        var defaultPatterns = new[] { "cam", "camrip", "telesync", "telecine", "workprint", "screener", "sample", "trailer", "extras" };
        var raw = string.IsNullOrWhiteSpace(value)
            ? defaultPatterns
            : value.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return string.Join(
            "\n",
            raw
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeUiTheme(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system"
        };
    }

    private static string NormalizeUiColorMode(string? value)
    {
        return string.Equals(value?.Trim(), "impaired", StringComparison.OrdinalIgnoreCase)
            ? "impaired"
            : "standard";
    }

    private static string NormalizeUiLanguage(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "en-AU" : normalized[..Math.Min(normalized.Length, 35)];
    }

    private static string NormalizeCalendarFirstDay(string? value)
        => string.Equals(value?.Trim(), "sunday", StringComparison.OrdinalIgnoreCase) ? "sunday" : "monday";

    private static string NormalizeCalendarWeekHeader(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, "ddd m/d", StringComparison.OrdinalIgnoreCase)) return "ddd m/d";
        if (string.Equals(normalized, "ddd d mmm", StringComparison.OrdinalIgnoreCase)) return "ddd d mmm";
        return "ddd d/M";
    }

    private static string NormalizeRuntimeFormat(string? value)
        => string.Equals(value?.Trim(), "minutes", StringComparison.OrdinalIgnoreCase) ? "minutes" : "hoursMinutes";

    private static string NormalizeShortDateFormat(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "mdy" => "mdy",
            "iso" => "iso",
            _ => "dmy"
        };

    private static string NormalizeLongDateFormat(string? value)
        => string.Equals(value?.Trim(), "mdy", StringComparison.OrdinalIgnoreCase) ? "mdy" : "full";

    private static string NormalizeTimeFormat(string? value)
        => string.Equals(value?.Trim(), "24", StringComparison.OrdinalIgnoreCase) ? "24" : "12";

    /// <summary>
    /// An IANA zone id, or empty. Not every host ships the same tz database
    /// entries (Windows vs Linux CI), so this only refuses what
    /// <see cref="TimeZoneInfo"/> itself refuses on this host, rather than
    /// keeping its own allow-list that would drift from either.
    /// </summary>
    private static string NormalizeTimezone(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            return trimmed;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeMetadataProviderMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "broker" or "cloud" or "managed" => "broker",
            "hybrid" or "broker-first" or "brokerfirst" => "hybrid",
            "direct" or "direct-only" or "directonly" or "self-hosted" => "direct",
            _ => "direct"
        };
    }

    private static string? NormalizeMetadataBrokerUrl(string? value)
    {
        var normalized = NormalizeName(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.TrimEnd('/');
    }

    private static string NormalizeTagColor(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "emerald" => "emerald",
            "teal" => "teal",
            "blue" => "blue",
            "violet" => "violet",
            "amber" => "amber",
            "rose" => "rose",
            _ => "slate"
        };
    }

    private static TagItem ReadTag(System.Data.Common.DbDataReader reader)
    {
        return new TagItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Color: reader.GetString(2),
            Description: reader.GetString(3),
            CreatedUtc: ParseTimestamp(reader.GetString(4)),
            UpdatedUtc: ParseTimestamp(reader.GetString(5)));
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// A sharing target that is absent and one that has been deliberately
    /// cleared mean different things. A missing key is an install that has
    /// never been configured, so it takes the shipped default; a stored empty
    /// string is a user who removed that half of the rule, and must stay unset
    /// rather than springing back to the default on the next read.
    /// </summary>
    private static int? ReadOptionalInt(string? value, int? fallback)
    {
        if (value is null) return fallback;
        if (value.Trim().Length == 0) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>As above; the ratio target ships unset, so there is no fallback.</summary>
    private static double? ReadOptionalDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

}
