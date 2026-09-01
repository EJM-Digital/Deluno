using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// The shared exclusion store lives in the Platform database because both
/// intake sources and catalogue automation need to consult it.
/// </summary>
public sealed class SqliteUnifiedExclusionRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IUnifiedExclusionRepository
{
    private const string DatabaseName = DelunoDatabaseNames.Platform;

    public async Task<IReadOnlyList<MediaExclusionItem>> ListActiveAsync(
        string? mediaType,
        string? sourceKind,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        var predicates = new List<string>
        {
            "(expires_utc IS NULL OR expires_utc > @now)"
        };
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            predicates.Add("media_type = @mediaType");
            AddParameter(command, "@mediaType", NormalizeMediaType(mediaType));
        }

        if (!string.IsNullOrWhiteSpace(sourceKind))
        {
            predicates.Add("source_kind = @sourceKind");
            AddParameter(command, "@sourceKind", NormalizeRequired(sourceKind, nameof(sourceKind)).ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            predicates.Add("source_id = @sourceId");
            AddParameter(command, "@sourceId", NormalizeRequired(sourceId, nameof(sourceId)));
        }

        command.CommandText = $"""
            SELECT id, media_type, source_kind, source_id, source_name, provider,
                   entry_key, title, year, imdb_id, reason, expires_utc,
                   created_utc, updated_utc
            FROM media_exclusions
            WHERE {string.Join(" AND ", predicates)}
            ORDER BY created_utc DESC, id ASC;
            """;

        var items = new List<MediaExclusionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<MediaExclusionItem?> UpsertAsync(
        UpsertMediaExclusionRequest request,
        CancellationToken cancellationToken)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        var sourceKind = NormalizeRequired(request.SourceKind, nameof(request.SourceKind)).ToLowerInvariant();
        var sourceId = NormalizeRequired(request.SourceId, nameof(request.SourceId));
        var sourceName = NormalizeRequired(request.SourceName, nameof(request.SourceName));
        var provider = NormalizeRequired(request.Provider, nameof(request.Provider)).ToLowerInvariant();
        var entryKey = NormalizeRequired(request.EntryKey, nameof(request.EntryKey));
        var title = NormalizeRequired(request.Title, nameof(request.Title));
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Excluded by user" : request.Reason.Trim();
        var now = timeProvider.GetUtcNow();
        DateTimeOffset? expiresUtc = request.DurationDays is > 0
            ? now.AddDays(Math.Clamp(request.DurationDays.Value, 1, 3650))
            : null;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO media_exclusions (
                id, media_type, source_kind, source_id, source_name, provider,
                entry_key, title, year, imdb_id, reason, expires_utc,
                created_utc, updated_utc
            ) VALUES (
                @id, @mediaType, @sourceKind, @sourceId, @sourceName, @provider,
                @entryKey, @title, @year, @imdbId, @reason, @expiresUtc,
                @createdUtc, @updatedUtc
            ) ON CONFLICT(source_kind, source_id, entry_key) DO UPDATE SET
                media_type = excluded.media_type,
                source_name = excluded.source_name,
                provider = excluded.provider,
                title = excluded.title,
                year = excluded.year,
                imdb_id = excluded.imdb_id,
                reason = excluded.reason,
                expires_utc = excluded.expires_utc,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@sourceKind", sourceKind);
        AddParameter(command, "@sourceId", sourceId);
        AddParameter(command, "@sourceName", sourceName);
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@entryKey", entryKey);
        AddParameter(command, "@title", title);
        AddParameter(command, "@year", request.Year);
        AddParameter(command, "@imdbId", string.IsNullOrWhiteSpace(request.ImdbId) ? null : request.ImdbId.Trim());
        AddParameter(command, "@reason", reason);
        AddParameter(command, "@expiresUtc", expiresUtc?.ToString("O"));
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, media_type, source_kind, source_id, source_name, provider,
                   entry_key, title, year, imdb_id, reason, expires_utc,
                   created_utc, updated_utc
            FROM media_exclusions
            WHERE source_kind = @sourceKind AND source_id = @sourceId AND entry_key = @entryKey;
            """;
        AddParameter(select, "@sourceKind", sourceKind);
        AddParameter(select, "@sourceId", sourceId);
        AddParameter(select, "@entryKey", entryKey);
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_exclusions WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteByScopeAsync(
        string sourceKind,
        string sourceId,
        string entryKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DatabaseName, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM media_exclusions WHERE source_kind = @sourceKind AND source_id = @sourceId AND entry_key = @entryKey;";
        AddParameter(command, "@sourceKind", NormalizeRequired(sourceKind, nameof(sourceKind)).ToLowerInvariant());
        AddParameter(command, "@sourceId", NormalizeRequired(sourceId, nameof(sourceId)));
        AddParameter(command, "@entryKey", NormalizeRequired(entryKey, nameof(entryKey)));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static MediaExclusionItem Read(System.Data.Common.DbDataReader reader)
        => new(
            Id: reader.GetString(0),
            MediaType: reader.GetString(1),
            SourceKind: reader.GetString(2),
            SourceId: reader.GetString(3),
            SourceName: reader.GetString(4),
            Provider: reader.GetString(5),
            EntryKey: reader.GetString(6),
            Title: reader.GetString(7),
            Year: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ImdbId: reader.IsDBNull(9) ? null : reader.GetString(9),
            Reason: reader.GetString(10),
            ExpiresUtc: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
            CreatedUtc: ParseTimestamp(reader.GetString(12)),
            UpdatedUtc: ParseTimestamp(reader.GetString(13)));

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException("A value is required.", parameterName)
            : normalized;
    }

    private static string NormalizeMediaType(string? value)
        => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";
}
