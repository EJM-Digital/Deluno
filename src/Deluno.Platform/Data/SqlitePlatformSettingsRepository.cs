using System.Globalization;
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
        var lastCompletedStep = Math.Clamp(request.LastCompletedStep, 0, 4);

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
            QualityPreset: NormalizeSetupChoice(request.QualityPreset, "", "", "balanced1080p", "premium4k"),
            FormatGoal: NormalizeSetupChoice(request.FormatGoal, "", "", "simpleClean", "balanced", "homeTheater", "storageSaver", "anime"),
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

        await UpsertSettingAsync(connection, transaction, "app.instanceName", NormalizeName(request.AppInstanceName) ?? "Deluno", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "jobs.autoStart", request.AutoStartJobs ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "notifications.enabled", request.EnableNotifications ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.renameOnImport", request.RenameOnImport ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.useHardlinks", request.UseHardlinks ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.cleanupEmptyFolders", request.CleanupEmptyFolders ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.removeCompletedDownloads", request.RemoveCompletedDownloads ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.unmonitorWhenCutoffMet", request.UnmonitorWhenCutoffMet ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.movieFolderFormat", NormalizeName(request.MovieFolderFormat) ?? "{Movie Title} ({Release Year})", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.seriesFolderFormat", NormalizeName(request.SeriesFolderFormat) ?? "{Series Title} ({Series Year})", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.episodeFileFormat", NormalizeName(request.EpisodeFileFormat) ?? "{Series Title} - S{season:00}E{episode:00} - {Episode Title}", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.bindAddress", NormalizeName(request.HostBindAddress) ?? "127.0.0.1", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.port", NormalizePositiveValue(request.HostPort, 5099).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.urlBase", NormalizeName(request.UrlBase) ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "security.requireAuthentication", "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.theme", NormalizeUiTheme(request.UiTheme), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.density", NormalizeUiDensity(request.UiDensity), updatedUtc, cancellationToken);
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
        await UpsertSettingAsync(connection, transaction, "metadata.nfoEnabled", request.MetadataNfoEnabled ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.artworkEnabled", request.MetadataArtworkEnabled ? "true" : "false", updatedUtc, cancellationToken);
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
        await UpsertSettingAsync(connection, transaction, "library.fileCheckHours", Math.Clamp(request.LibraryFileCheckHours ?? 6, 1, 168).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.strikeThreshold", Math.Clamp(request.DownloadHealthStrikeThreshold ?? 3, 1, 20).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.blockReleaseAfterThreshold", request.CleanupBlockReleaseAfterThreshold is false ? "false" : "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.queueReplacementAfterThreshold", request.CleanupQueueReplacementAfterThreshold is false ? "false" : "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.removeClientEntryAfterThreshold", request.CleanupRemoveClientEntryAfterThreshold == true ? "true" : "false", updatedUtc, cancellationToken);
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
        return secretProtector.Unprotect(purpose, stored);
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

    private static PlatformSettingsSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> roots)
    {
        var brokerUrl = NormalizeMetadataBrokerUrl(GetValue(settings, "metadata.brokerUrl")) ?? ManagedMetadataBrokerUrl;

        return new PlatformSettingsSnapshot(
            AppInstanceName: GetValue(settings, "app.instanceName") ?? "Deluno",
            MovieRootPath: GetValue(roots, "movies"),
            SeriesRootPath: GetValue(roots, "series"),
            DownloadsPath: GetValue(roots, "downloads"),
            IncompleteDownloadsPath: GetValue(roots, "downloads.incomplete"),
            AutoStartJobs: string.Equals(GetValue(settings, "jobs.autoStart"), "true", StringComparison.OrdinalIgnoreCase),
            EnableNotifications: string.Equals(GetValue(settings, "notifications.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            RenameOnImport: !string.Equals(GetValue(settings, "media.renameOnImport"), "false", StringComparison.OrdinalIgnoreCase),
            UseHardlinks: !string.Equals(GetValue(settings, "media.useHardlinks"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupEmptyFolders: string.Equals(GetValue(settings, "media.cleanupEmptyFolders"), "true", StringComparison.OrdinalIgnoreCase),
            RemoveCompletedDownloads: string.Equals(GetValue(settings, "media.removeCompletedDownloads"), "true", StringComparison.OrdinalIgnoreCase),
            UnmonitorWhenCutoffMet: string.Equals(GetValue(settings, "media.unmonitorWhenCutoffMet"), "true", StringComparison.OrdinalIgnoreCase),
            MovieFolderFormat: GetValue(settings, "media.movieFolderFormat") ?? "{Movie Title} ({Release Year})",
            SeriesFolderFormat: GetValue(settings, "media.seriesFolderFormat") ?? "{Series Title} ({Series Year})",
            EpisodeFileFormat: GetValue(settings, "media.episodeFileFormat") ?? "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            HostBindAddress: GetValue(settings, "host.bindAddress") ?? "127.0.0.1",
            HostPort: int.TryParse(GetValue(settings, "host.port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort) ? hostPort : 5099,
            UrlBase: GetValue(settings, "host.urlBase") ?? string.Empty,
            RequireAuthentication: true,
            UiTheme: NormalizeUiTheme(GetValue(settings, "ui.theme")),
            UiDensity: NormalizeUiDensity(GetValue(settings, "ui.density")),
            DefaultMovieView: NormalizeUiView(GetValue(settings, "ui.defaultMovieView")),
            DefaultShowView: NormalizeUiView(GetValue(settings, "ui.defaultShowView")),
            MetadataNfoEnabled: string.Equals(GetValue(settings, "metadata.nfoEnabled"), "true", StringComparison.OrdinalIgnoreCase),
            MetadataArtworkEnabled: !string.Equals(GetValue(settings, "metadata.artworkEnabled"), "false", StringComparison.OrdinalIgnoreCase),
            MetadataCertificationCountry: NormalizeName(GetValue(settings, "metadata.certificationCountry")) ?? "US",
            MetadataLanguage: NormalizeName(GetValue(settings, "metadata.language")) ?? "en",
            MetadataProviderMode: NormalizeMetadataProviderMode(GetValue(settings, "metadata.providerMode") ?? "broker"),
            MetadataBrokerUrl: brokerUrl,
            MetadataBrokerConfigured: !string.IsNullOrWhiteSpace(brokerUrl),
            MetadataTmdbApiKeyConfigured: !string.IsNullOrWhiteSpace(GetValue(settings, "metadata.tmdbApiKey")),
            MetadataOmdbApiKeyConfigured: !string.IsNullOrWhiteSpace(GetValue(settings, "metadata.omdbApiKey")),
            ReleaseNeverGrabPatterns: NormalizeNeverGrabPatterns(GetValue(settings, "search.neverGrabPatterns")),
            SearchScoringMode: SearchScoringModes.Normalize(GetValue(settings, "search.scoringMode")),
            ImportRecoveryRetentionDays: int.TryParse(GetValue(settings, "media.importRecoveryRetentionDays"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays) && retentionDays > 0 ? retentionDays : 30,
            LibraryFileCheckHours: int.TryParse(GetValue(settings, "library.fileCheckHours"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileCheckHours) && fileCheckHours > 0 ? Math.Clamp(fileCheckHours, 1, 168) : 6,
            UpdatedUtc: DateTimeOffset.UtcNow,
            MdbListApiKeyConfigured: !string.IsNullOrWhiteSpace(GetValue(settings, "intake.mdblistApiKey")),
            DownloadHealthStrikeThreshold: ReadDownloadHealthStrikeThreshold(settings),
            CleanupBlockReleaseAfterThreshold: !string.Equals(GetValue(settings, "cleanup.blockReleaseAfterThreshold"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupQueueReplacementAfterThreshold: !string.Equals(GetValue(settings, "cleanup.queueReplacementAfterThreshold"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupRemoveClientEntryAfterThreshold: string.Equals(GetValue(settings, "cleanup.removeClientEntryAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase),
            SharingMode: SharingPolicy.NormalizeMode(GetValue(settings, "sharing.mode")),
            SharingForHours: ReadOptionalInt(GetValue(settings, "sharing.forHours"), SharingPolicy.Default.ForHours),
            SharingUntilRatio: ReadOptionalDouble(GetValue(settings, "sharing.untilRatio")),
            SharingStuckAction: SharingPolicy.NormalizeStuckAction(GetValue(settings, "sharing.stuckAction")),
            SharingStuckAfterDays: ReadOptionalInt(GetValue(settings, "sharing.stuckAfterDays"), SharingPolicy.Default.StuckAfterDays) ?? SharingPolicy.Default.StuckAfterDays,
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
            ShowRelativeDates: !string.Equals(GetValue(settings, "ui.relativeDates"), "false", StringComparison.OrdinalIgnoreCase));
    }

    private static int ReadDownloadHealthStrikeThreshold(IReadOnlyDictionary<string, string> settings)
        => int.TryParse(GetValue(settings, "cleanup.strikeThreshold"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            ? Math.Clamp(threshold, 1, 20)
            : 3;

    private static SetupProgressItem CreateSetupProgress(IReadOnlyDictionary<string, string> settings)
    {
        var lastCompletedStep = int.TryParse(GetValue(settings, "setup.lastCompletedStep"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, 4)
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

    private static string NormalizeUiDensity(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "compact" => "compact",
            "spacious" => "spacious",
            "expanded" => "expanded",
            _ => "comfortable"
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
