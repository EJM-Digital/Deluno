using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private const string DownloadHealthRecordsSettingKey = "download-health.records.v1";
    private static readonly TimeSpan DownloadHealthStrikeWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DownloadHealthRetention = TimeSpan.FromDays(90);
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

    public async Task<IReadOnlyList<DownloadHealthRecord>> RecordDownloadHealthObservationsAsync(
        IReadOnlyList<DownloadHealthObservation> observations,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings).Where(record => now - record.LastObservedUtc <= DownloadHealthRetention).ToList();
        var touched = new List<DownloadHealthRecord>();

        foreach (var observation in observations)
        {
            if (string.IsNullOrWhiteSpace(observation.ClientId) || string.IsNullOrWhiteSpace(observation.QueueItemId) ||
                string.IsNullOrWhiteSpace(observation.ReleaseName) || string.IsNullOrWhiteSpace(observation.Kind))
            {
                continue;
            }

            var releaseKey = NormalizeDownloadReleaseKey(observation.ReleaseName);
            var index = records.FindIndex(record =>
                string.Equals(record.ClientId, observation.ClientId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.QueueItemId, observation.QueueItemId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Kind, observation.Kind, StringComparison.OrdinalIgnoreCase));
            var record = index >= 0 ? records[index] : null;
            var strikes = record is null ? 1 : record.LastObservedUtc <= now - DownloadHealthStrikeWindow ? record.StrikeCount + 1 : record.StrikeCount;
            var updated = new DownloadHealthRecord(
                observation.ClientId.Trim(), observation.QueueItemId.Trim(), observation.ReleaseName.Trim(), releaseKey,
                observation.Kind.Trim(), observation.Severity.Trim(), SanitizeDownloadHealthEvidence(observation.Evidence),
                record?.FirstObservedUtc ?? now, now, strikes, record?.IgnoredUntilUtc);

            if (index >= 0) records[index] = updated; else records.Add(updated);
            touched.Add(updated);
        }

        await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return touched;
    }

    public async Task<IReadOnlyList<DownloadHealthRecord>> ListDownloadHealthRecordsAsync(int take, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        return ReadDownloadHealthRecords(await ReadSettingsAsync(connection, cancellationToken))
            .Where(record => now - record.LastObservedUtc <= DownloadHealthRetention)
            .OrderByDescending(record => record.LastObservedUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToArray();
    }

    public async Task<bool> IsDownloadReleaseBlockedAsync(string clientId, string releaseName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(releaseName)) return false;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings);
        var releaseKey = NormalizeDownloadReleaseKey(releaseName);
        var now = timeProvider.GetUtcNow();
        var threshold = ReadDownloadHealthStrikeThreshold(settings);
        if (string.Equals(GetValue(settings, "cleanup.blockReleaseAfterThreshold"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return records.Any(record =>
            string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.ReleaseKey, releaseKey, StringComparison.Ordinal) &&
            record.BlocksCandidate(now, threshold));
    }

    public async Task<DownloadHealthRecord?> IgnoreDownloadHealthFindingAsync(
        string clientId,
        string queueItemId,
        string kind,
        int durationDays,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings).ToList();
        var index = records.FindIndex(record =>
            string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.QueueItemId, queueItemId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var updated = records[index] with { IgnoredUntilUtc = now.AddDays(Math.Clamp(durationDays, 1, 30)) };
        records[index] = updated;
        await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
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
        await UpsertSettingAsync(connection, transaction, "cleanup.strikeThreshold", Math.Clamp(request.DownloadHealthStrikeThreshold ?? 3, 1, 20).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.blockReleaseAfterThreshold", request.CleanupBlockReleaseAfterThreshold is false ? "false" : "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.queueReplacementAfterThreshold", request.CleanupQueueReplacementAfterThreshold is false ? "false" : "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "cleanup.removeClientEntryAfterThreshold", request.CleanupRemoveClientEntryAfterThreshold == true ? "true" : "false", updatedUtc, cancellationToken);
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
            UpdatedUtc: DateTimeOffset.UtcNow,
            MdbListApiKeyConfigured: !string.IsNullOrWhiteSpace(GetValue(settings, "intake.mdblistApiKey")),
            DownloadHealthStrikeThreshold: ReadDownloadHealthStrikeThreshold(settings),
            CleanupBlockReleaseAfterThreshold: !string.Equals(GetValue(settings, "cleanup.blockReleaseAfterThreshold"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupQueueReplacementAfterThreshold: !string.Equals(GetValue(settings, "cleanup.queueReplacementAfterThreshold"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupRemoveClientEntryAfterThreshold: string.Equals(GetValue(settings, "cleanup.removeClientEntryAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase),
            CleanupPurgePayloadAfterThreshold: string.Equals(GetValue(settings, "cleanup.purgePayloadAfterThreshold"), "true", StringComparison.OrdinalIgnoreCase));
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

    private static IReadOnlyList<DownloadHealthRecord> ReadDownloadHealthRecords(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(DownloadHealthRecordsSettingKey, out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<DownloadHealthRecord>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string NormalizeDownloadReleaseKey(string releaseName)
    {
        var builder = new StringBuilder(releaseName.Length);
        foreach (var character in releaseName.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    private static string SanitizeDownloadHealthEvidence(string evidence)
        => evidence.TrimStart().StartsWith("Import source:", StringComparison.OrdinalIgnoreCase)
            ? "Import source: [redacted path]"
            : evidence.Trim();

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

    public async Task<IReadOnlyList<ProcessorConnectionItem>> ListProcessorConnectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc FROM processor_connections ORDER BY name COLLATE NOCASE;";
        var items = new List<ProcessorConnectionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadProcessorConnection(reader));
        }

        return items;
    }

    public async Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<ProcessorConnectionItem?> FindProcessorConnectionByNameAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc FROM processor_connections WHERE name = @name COLLATE NOCASE LIMIT 1;";
        AddParameter(command, "@name", name.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorConnection(reader) : null;
    }

    public async Task<ProcessorConnectionItem> CreateProcessorConnectionAsync(
        CreateProcessorConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new ProcessorConnectionItem(
            Guid.CreateVersion7().ToString("N"),
            NormalizeName(request.Name) ?? "Processor connection",
            NormalizeProcessorConnectionProvider(request.Provider),
            NormalizeProcessorConnectionUrl(request.SubmissionUrl) ?? string.Empty,
            NormalizeProcessorAuthHeaderName(request.AuthHeaderName),
            string.IsNullOrWhiteSpace(request.Secret) ? null : request.Secret.Trim(),
            request.IsEnabled,
            "unknown",
            null,
            null,
            now,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO processor_connections (
                id, name, provider, submission_url, auth_header_name, secret_value, is_enabled,
                health_status, last_health_message, last_health_test_utc, created_utc, updated_utc
            ) VALUES (
                @id, @name, @provider, @submissionUrl, @authHeaderName, @secretValue, @isEnabled,
                @healthStatus, NULL, NULL, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@provider", item.Provider);
        AddParameter(command, "@submissionUrl", item.SubmissionUrl);
        AddParameter(command, "@authHeaderName", item.AuthHeaderName);
        AddParameter(command, "@secretValue", item.Secret is null ? null : secretProtector.Protect($"processor-connection:{item.Id}", item.Secret));
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<ProcessorConnectionItem?> UpdateProcessorConnectionAsync(
        string id,
        UpdateProcessorConnectionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        var existing = await GetProcessorConnectionAsync(connection, id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var name = NormalizeName(request.Name) ?? existing.Name;
        var provider = NormalizeProcessorConnectionProvider(request.Provider ?? existing.Provider);
        var submissionUrl = NormalizeProcessorConnectionUrl(request.SubmissionUrl) ?? existing.SubmissionUrl;
        var authHeaderName = NormalizeProcessorAuthHeaderName(request.AuthHeaderName ?? existing.AuthHeaderName);
        var secret = string.IsNullOrWhiteSpace(request.Secret) ? existing.Secret : request.Secret.Trim();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE processor_connections
            SET name = @name,
                provider = @provider,
                submission_url = @submissionUrl,
                auth_header_name = @authHeaderName,
                secret_value = @secretValue,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@name", name);
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@submissionUrl", submissionUrl);
        AddParameter(command, "@authHeaderName", authHeaderName);
        AddParameter(command, "@secretValue", secret is null ? null : secretProtector.Protect($"processor-connection:{id.Trim()}", secret));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<bool> DeleteProcessorConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processor_connections WHERE id = @id;";
        AddParameter(command, "@id", id.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ProcessorConnectionItem?> RecordProcessorConnectionHealthAsync(
        string id,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE processor_connections
            SET health_status = @status,
                last_health_message = @message,
                last_health_test_utc = @testedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        var now = timeProvider.GetUtcNow();
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@status", NormalizeProcessorConnectionHealth(status));
        AddParameter(command, "@message", NormalizeName(message));
        AddParameter(command, "@testedUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<ProcessorHandoffItem> EnsureProcessorHandoffAsync(
        CreateProcessorHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sourcePath = request.SourcePath.Trim();
        var sourceKey = BuildProcessorSourceKey(sourcePath);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO processor_handoffs (
                    id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key,
                    processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc
                ) VALUES (
                    @id, @libraryId, @mediaType, @clientId, @queueItemId, @releaseName, @sourcePath, @sourceKey,
                    @processorName, 'waiting', NULL, NULL, NULL, @createdUtc, @updatedUtc
                )
                ON CONFLICT(library_id, source_key) DO NOTHING;
                """;
            AddParameter(insert, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(insert, "@libraryId", request.LibraryId.Trim());
            AddParameter(insert, "@mediaType", request.MediaType.Trim().ToLowerInvariant());
            AddParameter(insert, "@clientId", request.ClientId.Trim());
            AddParameter(insert, "@queueItemId", request.QueueItemId.Trim());
            AddParameter(insert, "@releaseName", request.ReleaseName.Trim());
            AddParameter(insert, "@sourcePath", sourcePath);
            AddParameter(insert, "@sourceKey", sourceKey);
            AddParameter(insert, "@processorName", NormalizeName(request.ProcessorName));
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await FindProcessorHandoffAsync(request.LibraryId, null, sourcePath, cancellationToken))
            ?? throw new InvalidOperationException("Processor hand-off could not be created or loaded.");
    }

    public async Task<ProcessorHandoffItem?> FindProcessorHandoffAsync(
        string libraryId,
        string? handoffId,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || (string.IsNullOrWhiteSpace(handoffId) && string.IsNullOrWhiteSpace(sourcePath)))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = !string.IsNullOrWhiteSpace(handoffId)
            ? "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE id = @id AND library_id = @libraryId LIMIT 1;"
            : "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE library_id = @libraryId AND source_key = @sourceKey LIMIT 1;";
        AddParameter(command, "@libraryId", libraryId.Trim());
        if (!string.IsNullOrWhiteSpace(handoffId)) AddParameter(command, "@id", handoffId.Trim());
        else AddParameter(command, "@sourceKey", BuildProcessorSourceKey(sourcePath!));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<ProcessorHandoffItem?> GetProcessorHandoffAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<ProcessorHandoffItem?> UpdateProcessorHandoffAsync(
        string id,
        string status,
        string? outputPath,
        string? importJobId,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE processor_handoffs
                SET status = @status,
                    output_path = COALESCE(@outputPath, output_path),
                    import_job_id = COALESCE(@importJobId, import_job_id),
                    failure_message = @failureMessage,
                    updated_utc = @updatedUtc
                WHERE id = @id;
                """;
            AddParameter(update, "@id", id.Trim());
            AddParameter(update, "@status", NormalizeProcessorHandoffStatus(status));
            AddParameter(update, "@outputPath", NormalizePath(outputPath));
            AddParameter(update, "@importJobId", NormalizeName(importJobId));
            AddParameter(update, "@failureMessage", NormalizeName(failureMessage));
            AddParameter(update, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE id = @id LIMIT 1;";
        AddParameter(select, "@id", id.Trim());
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsAsync(
        string? libraryId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(libraryId)
            ? "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs ORDER BY updated_utc DESC LIMIT @take;"
            : "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE library_id = @libraryId ORDER BY updated_utc DESC LIMIT @take;";
        AddParameter(command, "@take", Math.Clamp(take, 1, 200));
        if (!string.IsNullOrWhiteSpace(libraryId)) AddParameter(command, "@libraryId", libraryId.Trim());
        var items = new List<ProcessorHandoffItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorHandoff(reader));
        return items;
    }

    public async Task<MigrationAuditReport> RecordMigrationAuditReportAsync(
        MigrationAuditReport report,
        CancellationToken cancellationToken)
    {
        var persisted = report with
        {
            Id = Guid.CreateVersion7().ToString("N"),
            AppliedUtc = timeProvider.GetUtcNow()
        };
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO migration_audit_reports (
                id, source_kind, source_name, applied_utc, preflight_report_json, result_report_json, applied_items_json
            ) VALUES (
                @id, @sourceKind, @sourceName, @appliedUtc, @preflightReportJson, @resultReportJson, @appliedItemsJson
            );
            """;
        AddParameter(command, "@id", persisted.Id);
        AddParameter(command, "@sourceKind", persisted.SourceKind);
        AddParameter(command, "@sourceName", persisted.SourceName);
        AddParameter(command, "@appliedUtc", persisted.AppliedUtc.ToString("O"));
        AddParameter(command, "@preflightReportJson", JsonSerializer.Serialize(persisted.PreflightReport));
        AddParameter(command, "@resultReportJson", JsonSerializer.Serialize(persisted.ResultReport));
        AddParameter(command, "@appliedItemsJson", JsonSerializer.Serialize(persisted.Applied));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return persisted;
    }

    public async Task<IReadOnlyList<MigrationAuditReport>> ListMigrationAuditReportsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_kind, source_name, applied_utc, preflight_report_json, result_report_json, applied_items_json
            FROM migration_audit_reports
            ORDER BY applied_utc DESC
            LIMIT @take;
            """;
        AddParameter(command, "@take", Math.Clamp(take, 1, 100));
        var reports = new List<MigrationAuditReport>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reports.Add(ReadMigrationAuditReport(reader));
        }

        return reports;
    }

    public async Task<MigrationAuditReport?> GetMigrationAuditReportAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_kind, source_name, applied_utc, preflight_report_json, result_report_json, applied_items_json
            FROM migration_audit_reports
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMigrationAuditReport(reader) : null;
    }

    private static MigrationAuditReport ReadMigrationAuditReport(System.Data.Common.DbDataReader reader)
    {
        var preflight = JsonSerializer.Deserialize<MigrationReport>(reader.GetString(4))
            ?? throw new InvalidOperationException("Stored migration preflight report could not be read.");
        var result = JsonSerializer.Deserialize<MigrationReport>(reader.GetString(5))
            ?? throw new InvalidOperationException("Stored migration result report could not be read.");
        var applied = JsonSerializer.Deserialize<IReadOnlyList<MigrationAppliedItem>>(reader.GetString(6)) ?? [];
        return new MigrationAuditReport(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            preflight,
            result,
            applied);
    }

    private static ProcessorHandoffItem ReadProcessorHandoff(System.Data.Common.DbDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), ParseTimestamp(reader.GetString(12)), ParseTimestamp(reader.GetString(13)));

    private ProcessorConnectionItem ReadProcessorConnection(System.Data.Common.DbDataReader reader)
    {
        var id = reader.GetString(0);
        return new ProcessorConnectionItem(
            id,
            reader.GetString(1),
            NormalizeProcessorConnectionProvider(reader.GetString(2)),
            reader.GetString(3),
            NormalizeProcessorAuthHeaderName(reader.GetString(4)),
            reader.IsDBNull(5) ? null : secretProtector.Unprotect($"processor-connection:{id}", reader.GetString(5)),
            reader.GetInt64(6) == 1,
            NormalizeProcessorConnectionHealth(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)));
    }

    private async Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc FROM processor_connections WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorConnection(reader) : null;
    }

    private static string BuildProcessorSourceKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant())));

    private static string NormalizeProcessorHandoffStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "submitted" or "accepted" or "started" or "waiting" or "completed" or "failed" or "timed-out" => status.Trim().ToLowerInvariant(),
            _ => "waiting"
        };

    private static string NormalizeProcessorConnectionProvider(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "fileflows" or "fileflows-webhook" => "fileflows-webhook",
            _ => "generic-webhook"
        };

    private static string NormalizeProcessorAuthHeaderName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Authorization" : value.Trim();

    private static string? NormalizeProcessorConnectionUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.ToString().TrimEnd('/');
    }

    private static string NormalizeProcessorConnectionHealth(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "healthy" or "degraded" or "unreachable" => status.Trim().ToLowerInvariant(),
            _ => "unknown"
        };

}

