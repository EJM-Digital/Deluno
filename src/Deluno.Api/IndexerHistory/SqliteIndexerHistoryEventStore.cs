using System.Data.Common;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Api.IndexerHistory;

/// <summary>
/// The RSS-sync and auth-failure half of the combined Indexer History (#754).
/// Grabs and searches are read live from the movie/series search history
/// tables by <see cref="IndexerHistoryService"/> instead of being duplicated
/// here.
/// </summary>
public sealed class SqliteIndexerHistoryEventStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IIndexerHistoryEventStore
{
    public async Task RecordAsync(RecordIndexerHistoryEventRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO indexer_history_events (id, indexer_id, indexer_name, event_type, outcome, message, details_json, created_utc) "
            + "VALUES (@id, @indexerId, @indexerName, @eventType, @outcome, @message, @detailsJson, @createdUtc);";
        AddParameter(command, "@id", Guid.NewGuid().ToString("n"));
        AddParameter(command, "@indexerId", request.IndexerId);
        AddParameter(command, "@indexerName", request.IndexerName);
        AddParameter(command, "@eventType", request.EventType);
        AddParameter(command, "@outcome", request.Outcome);
        AddParameter(command, "@message", request.Message);
        AddParameter(command, "@detailsJson", request.DetailsJson);
        AddParameter(command, "@createdUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IndexerHistoryItem>> ListAsync(DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, indexer_id, indexer_name, event_type, outcome, message, details_json, created_utc "
            + "FROM indexer_history_events "
            + (sinceUtc is null ? "" : "WHERE created_utc > @sinceUtc ")
            + "ORDER BY created_utc DESC "
            + "LIMIT 1000;";
        if (sinceUtc is not null)
        {
            AddParameter(command, "@sinceUtc", sinceUtc.Value.ToString("O"));
        }

        var items = new List<IndexerHistoryItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<DateTimeOffset?> GetClearedBeforeUtcAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cleared_before_utc FROM indexer_history_clear_state WHERE id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? ParseTimestamp(text) : null;
    }

    public async Task SetClearedBeforeUtcAsync(DateTimeOffset clearedBeforeUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO indexer_history_clear_state (id, cleared_before_utc) VALUES (1, @clearedBeforeUtc) "
            + "ON CONFLICT(id) DO UPDATE SET cleared_before_utc = excluded.cleared_before_utc;";
        AddParameter(command, "@clearedBeforeUtc", clearedBeforeUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IndexerHistoryItem Read(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            null,
            null,
            ParseTimestamp(reader.GetString(7)));
}
