using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Intake.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Intake.Data;

/// <summary>
/// The intake slice of the Platform SQLite database. Split out of
/// SqlitePlatformSettingsRepository by ADR-001 Step 1, bodies unchanged. The
/// tables stay under the Platform migrations (V0010, V0014, V0016).
/// </summary>
public sealed class SqliteIntakeRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IIntakeRepository
{
    public async Task<IReadOnlyList<IntakeSourceItem>> ListIntakeSourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<IntakeSourceItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.name, s.provider, s.feed_url, s.media_type,
                s.library_id, l.name, s.quality_profile_id, q.name,
                s.required_genres, s.minimum_rating, s.minimum_year, s.maximum_age_days,
                s.allowed_certifications, s.audience, s.sync_interval_hours,
                s.last_sync_utc, s.last_sync_status, s.last_sync_summary,
                s.search_on_add, s.is_enabled, s.created_utc, s.updated_utc
            FROM intake_sources s
            LEFT JOIN libraries l ON l.id = s.library_id
            LEFT JOIN quality_profiles q ON q.id = s.quality_profile_id
            ORDER BY s.name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadIntakeSource(reader));
        }

        return items;
    }

    public async Task<IntakeSourceItem?> GetIntakeSourceAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        return await GetIntakeSourceAsync(connection, id, cancellationToken);
    }

    public async Task<IReadOnlyList<IntakeListExclusionItem>> ListActiveIntakeListExclusionsAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        var items = new List<IntakeListExclusionItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_id, entry_key, title, year, imdb_id, expires_utc, created_utc, updated_utc
            FROM intake_source_exclusions
            WHERE source_id = @sourceId
              AND (expires_utc IS NULL OR expires_utc > @now)
            ORDER BY created_utc DESC;
            """;
        AddParameter(command, "@sourceId", sourceId);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadIntakeListExclusion(reader));
        }

        return items;
    }

    public async Task<IntakeListExclusionItem?> CreateIntakeListExclusionAsync(
        string sourceId,
        CreateIntakeListExclusionRequest request,
        CancellationToken cancellationToken)
    {
        var title = NormalizeName(request.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var imdbId = NormalizeName(request.ImdbId);
        var year = NormalizeNullableYear(request.Year);
        var entryKey = BuildIntakeEntryKey(title, year, imdbId);
        DateTimeOffset? expiresUtc = request.DurationDays is > 0
            ? now.AddDays(Math.Clamp(request.DurationDays.Value, 1, 3650))
            : null;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO intake_source_exclusions (
                id, source_id, entry_key, title, year, imdb_id, expires_utc, created_utc, updated_utc
            ) VALUES (
                @id, @sourceId, @entryKey, @title, @year, @imdbId, @expiresUtc, @createdUtc, @updatedUtc
            ) ON CONFLICT(source_id, entry_key) DO UPDATE SET
                title = excluded.title,
                year = excluded.year,
                imdb_id = excluded.imdb_id,
                expires_utc = excluded.expires_utc,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@sourceId", sourceId);
        AddParameter(command, "@entryKey", entryKey);
        AddParameter(command, "@title", title);
        AddParameter(command, "@year", year);
        AddParameter(command, "@imdbId", imdbId);
        AddParameter(command, "@expiresUtc", expiresUtc?.ToString("O"));
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, source_id, entry_key, title, year, imdb_id, expires_utc, created_utc, updated_utc
            FROM intake_source_exclusions
            WHERE source_id = @sourceId AND entry_key = @entryKey;
            """;
        AddParameter(select, "@sourceId", sourceId);
        AddParameter(select, "@entryKey", entryKey);
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntakeListExclusion(reader) : null;
    }

    public async Task<bool> DeleteIntakeListExclusionAsync(
        string sourceId,
        string exclusionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM intake_source_exclusions WHERE id = @id AND source_id = @sourceId;";
        AddParameter(command, "@id", exclusionId);
        AddParameter(command, "@sourceId", sourceId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<IntakeTitleOriginItem>> ListIntakeTitleOriginsAsync(
        string mediaType,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return [];
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_id, source_name, provider, media_type, entity_id, entry_key, title, year, imdb_id, first_seen_utc, last_seen_utc
            FROM intake_title_origins
            WHERE media_type = @mediaType AND entity_id = @entityId
            ORDER BY last_seen_utc DESC, source_name COLLATE NOCASE;
            """;
        AddParameter(command, "@mediaType", NormalizeMediaType(mediaType));
        AddParameter(command, "@entityId", entityId);
        var items = new List<IntakeTitleOriginItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadIntakeTitleOrigin(reader));
        }

        return items;
    }

    public async Task<IntakeTitleOriginItem?> RecordIntakeTitleOriginAsync(
        CreateIntakeTitleOriginRequest request,
        CancellationToken cancellationToken)
    {
        var sourceId = NormalizeName(request.SourceId);
        var sourceName = NormalizeName(request.SourceName);
        var entityId = NormalizeName(request.EntityId);
        var entryKey = NormalizeName(request.EntryKey);
        var title = NormalizeName(request.Title);
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(sourceName) ||
            string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(entryKey) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var mediaType = NormalizeMediaType(request.MediaType);
        var provider = NormalizeName(request.Provider) ?? "unknown";
        var year = NormalizeNullableYear(request.Year);
        var imdbId = NormalizeName(request.ImdbId);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO intake_title_origins (
                id, source_id, source_name, provider, media_type, entity_id, entry_key, title, year, imdb_id, first_seen_utc, last_seen_utc
            ) VALUES (
                @id, @sourceId, @sourceName, @provider, @mediaType, @entityId, @entryKey, @title, @year, @imdbId, @now, @now
            ) ON CONFLICT(source_id, media_type, entity_id, entry_key) DO UPDATE SET
                source_name = excluded.source_name,
                provider = excluded.provider,
                title = excluded.title,
                year = excluded.year,
                imdb_id = excluded.imdb_id,
                last_seen_utc = excluded.last_seen_utc;
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@sourceId", sourceId);
        AddParameter(command, "@sourceName", sourceName);
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@entityId", entityId);
        AddParameter(command, "@entryKey", entryKey);
        AddParameter(command, "@title", title);
        AddParameter(command, "@year", year);
        AddParameter(command, "@imdbId", imdbId);
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, source_id, source_name, provider, media_type, entity_id, entry_key, title, year, imdb_id, first_seen_utc, last_seen_utc
            FROM intake_title_origins
            WHERE source_id = @sourceId AND media_type = @mediaType AND entity_id = @entityId AND entry_key = @entryKey;
            """;
        AddParameter(select, "@sourceId", sourceId);
        AddParameter(select, "@mediaType", mediaType);
        AddParameter(select, "@entityId", entityId);
        AddParameter(select, "@entryKey", entryKey);
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntakeTitleOrigin(reader) : null;
    }

    public async Task<IntakeSourceItem> CreateIntakeSourceAsync(
        CreateIntakeSourceRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var mediaType = NormalizeMediaType(request.MediaType);
        var item = new IntakeSourceItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New list source",
            Provider: NormalizeName(request.Provider) ?? "manual",
            FeedUrl: NormalizeName(request.FeedUrl) ?? string.Empty,
            MediaType: mediaType,
            LibraryId: NormalizeName(request.LibraryId),
            LibraryName: null,
            QualityProfileId: NormalizeName(request.QualityProfileId),
            QualityProfileName: null,
            RequiredGenres: NormalizeCsv(request.RequiredGenres),
            MinimumRating: NormalizeNullableRating(request.MinimumRating),
            MinimumYear: NormalizeNullableYear(request.MinimumYear),
            MaximumAgeDays: NormalizeNullablePositiveValue(request.MaximumAgeDays),
            AllowedCertifications: NormalizeCsv(request.AllowedCertifications),
            Audience: NormalizeAudience(request.Audience),
            SyncIntervalHours: NormalizeSyncIntervalHours(request.SyncIntervalHours),
            LastSyncUtc: null,
            LastSyncStatus: "never",
            LastSyncSummary: null,
            SearchOnAdd: request.SearchOnAdd,
            IsEnabled: request.IsEnabled,
            CreatedUtc: now,
            UpdatedUtc: now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO intake_sources (
                id, name, provider, feed_url, media_type, library_id, quality_profile_id,
                required_genres, minimum_rating, minimum_year, maximum_age_days,
                allowed_certifications, audience, sync_interval_hours, last_sync_utc, last_sync_status, last_sync_summary,
                search_on_add, is_enabled, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @provider, @feedUrl, @mediaType, @libraryId, @qualityProfileId,
                @requiredGenres, @minimumRating, @minimumYear, @maximumAgeDays,
                @allowedCertifications, @audience, @syncIntervalHours, @lastSyncUtc, @lastSyncStatus, @lastSyncSummary,
                @searchOnAdd, @isEnabled, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@provider", item.Provider);
        AddParameter(command, "@feedUrl", item.FeedUrl);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@libraryId", item.LibraryId);
        AddParameter(command, "@qualityProfileId", item.QualityProfileId);
        AddParameter(command, "@requiredGenres", item.RequiredGenres);
        AddParameter(command, "@minimumRating", item.MinimumRating);
        AddParameter(command, "@minimumYear", item.MinimumYear);
        AddParameter(command, "@maximumAgeDays", item.MaximumAgeDays);
        AddParameter(command, "@allowedCertifications", item.AllowedCertifications);
        AddParameter(command, "@audience", item.Audience);
        AddParameter(command, "@syncIntervalHours", item.SyncIntervalHours);
        AddParameter(command, "@lastSyncUtc", item.LastSyncUtc?.ToString("O"));
        AddParameter(command, "@lastSyncStatus", item.LastSyncStatus);
        AddParameter(command, "@lastSyncSummary", item.LastSyncSummary);
        AddParameter(command, "@searchOnAdd", item.SearchOnAdd ? 1 : 0);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetIntakeSourceAsync(connection, item.Id, cancellationToken))!;
    }

    public async Task<IntakeSourceItem?> UpdateIntakeSourceAsync(
        string id,
        UpdateIntakeSourceRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetIntakeSourceAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE intake_sources
            SET
                name = @name,
                provider = @provider,
                feed_url = @feedUrl,
                media_type = @mediaType,
                library_id = @libraryId,
                quality_profile_id = @qualityProfileId,
                required_genres = @requiredGenres,
                minimum_rating = @minimumRating,
                minimum_year = @minimumYear,
                maximum_age_days = @maximumAgeDays,
                allowed_certifications = @allowedCertifications,
                audience = @audience,
                sync_interval_hours = @syncIntervalHours,
                search_on_add = @searchOnAdd,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@provider", NormalizeName(request.Provider) ?? current.Provider);
        AddParameter(command, "@feedUrl", NormalizeName(request.FeedUrl) ?? current.FeedUrl);
        AddParameter(command, "@mediaType", NormalizeMediaType(request.MediaType));
        AddParameter(command, "@libraryId", NormalizeName(request.LibraryId));
        AddParameter(command, "@qualityProfileId", NormalizeName(request.QualityProfileId));
        AddParameter(command, "@requiredGenres", NormalizeCsv(request.RequiredGenres));
        AddParameter(command, "@minimumRating", NormalizeNullableRating(request.MinimumRating));
        AddParameter(command, "@minimumYear", NormalizeNullableYear(request.MinimumYear));
        AddParameter(command, "@maximumAgeDays", NormalizeNullablePositiveValue(request.MaximumAgeDays));
        AddParameter(command, "@allowedCertifications", NormalizeCsv(request.AllowedCertifications));
        AddParameter(command, "@audience", NormalizeAudience(request.Audience));
        AddParameter(command, "@syncIntervalHours", NormalizeSyncIntervalHours(request.SyncIntervalHours));
        AddParameter(command, "@searchOnAdd", request.SearchOnAdd ? 1 : 0);
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetIntakeSourceAsync(connection, id, cancellationToken);
    }

    public async Task<IntakeSourceItem?> RecordIntakeSourceSyncResultAsync(
        string id,
        DateTimeOffset syncedUtc,
        string status,
        string? summary,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE intake_sources
            SET
                last_sync_utc = @lastSyncUtc,
                last_sync_status = @lastSyncStatus,
                last_sync_summary = @lastSyncSummary,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@lastSyncUtc", syncedUtc.ToString("O"));
        AddParameter(command, "@lastSyncStatus", NormalizeIntakeSyncStatus(status));
        AddParameter(command, "@lastSyncSummary", NormalizeName(summary));
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetIntakeSourceAsync(connection, id, cancellationToken);
    }

    public async Task<bool> DeleteIntakeSourceAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM intake_sources WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<IntakeSourceItem?> GetIntakeSourceAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.name, s.provider, s.feed_url, s.media_type,
                s.library_id, l.name, s.quality_profile_id, q.name,
                s.required_genres, s.minimum_rating, s.minimum_year, s.maximum_age_days,
                s.allowed_certifications, s.audience, s.sync_interval_hours,
                s.last_sync_utc, s.last_sync_status, s.last_sync_summary,
                s.search_on_add, s.is_enabled, s.created_utc, s.updated_utc
            FROM intake_sources s
            LEFT JOIN libraries l ON l.id = s.library_id
            LEFT JOIN quality_profiles q ON q.id = s.quality_profile_id
            WHERE s.id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntakeSource(reader) : null;
    }

    private static string BuildIntakeEntryKey(string title, int? year, string? imdbId)
    {
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            return $"imdb:{imdbId.Trim().ToLowerInvariant()}";
        }

        return $"title:{title.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }

    private static string NormalizeIntakeSyncStatus(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "success" => "success",
            "partial" => "partial",
            "error" => "error",
            _ => "never"
        };
    }

    private static IntakeSourceItem ReadIntakeSource(System.Data.Common.DbDataReader reader)
    {
        return new IntakeSourceItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Provider: reader.GetString(2),
            FeedUrl: reader.GetString(3),
            MediaType: reader.GetString(4),
            LibraryId: reader.IsDBNull(5) ? null : reader.GetString(5),
            LibraryName: reader.IsDBNull(6) ? null : reader.GetString(6),
            QualityProfileId: reader.IsDBNull(7) ? null : reader.GetString(7),
            QualityProfileName: reader.IsDBNull(8) ? null : reader.GetString(8),
            RequiredGenres: reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            MinimumRating: reader.IsDBNull(10) ? null : reader.GetDouble(10),
            MinimumYear: reader.IsDBNull(11) ? null : reader.GetInt32(11),
            MaximumAgeDays: reader.IsDBNull(12) ? null : reader.GetInt32(12),
            AllowedCertifications: reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            Audience: reader.IsDBNull(14) ? "any" : NormalizeAudience(reader.GetString(14)),
            SyncIntervalHours: reader.IsDBNull(15) ? 24 : NormalizeSyncIntervalHours(reader.GetInt32(15)),
            LastSyncUtc: reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)),
            LastSyncStatus: reader.IsDBNull(17) ? "never" : NormalizeIntakeSyncStatus(reader.GetString(17)),
            LastSyncSummary: reader.IsDBNull(18) ? null : reader.GetString(18),
            SearchOnAdd: reader.GetInt64(19) == 1,
            IsEnabled: reader.GetInt64(20) == 1,
            CreatedUtc: ParseTimestamp(reader.GetString(21)),
            UpdatedUtc: ParseTimestamp(reader.GetString(22)));
    }

    private static IntakeListExclusionItem ReadIntakeListExclusion(System.Data.Common.DbDataReader reader)
    {
        return new IntakeListExclusionItem(
            Id: reader.GetString(0),
            SourceId: reader.GetString(1),
            EntryKey: reader.GetString(2),
            Title: reader.GetString(3),
            Year: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            ImdbId: reader.IsDBNull(5) ? null : reader.GetString(5),
            ExpiresUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
            CreatedUtc: ParseTimestamp(reader.GetString(7)),
            UpdatedUtc: ParseTimestamp(reader.GetString(8)));
    }

    private static IntakeTitleOriginItem ReadIntakeTitleOrigin(System.Data.Common.DbDataReader reader)
    {
        return new IntakeTitleOriginItem(
            Id: reader.GetString(0),
            SourceId: reader.GetString(1),
            SourceName: reader.GetString(2),
            Provider: reader.GetString(3),
            MediaType: reader.GetString(4),
            EntityId: reader.GetString(5),
            EntryKey: reader.GetString(6),
            Title: reader.GetString(7),
            Year: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ImdbId: reader.IsDBNull(9) ? null : reader.GetString(9),
            FirstSeenUtc: ParseTimestamp(reader.GetString(10)),
            LastSeenUtc: ParseTimestamp(reader.GetString(11)));
    }

}
