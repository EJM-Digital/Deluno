using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Connections.Data;

/// <summary>
/// One server and media type's missed refreshes while it could not be reached
/// (#920). A row exists only while that library is failing: created on its
/// first failure, updated on every retry that still fails, and deleted the
/// moment one succeeds - which is also what a health check reads to show it
/// once on System → Health, and what a successful retry reads to catch up in
/// one go.
/// </summary>
public interface IMediaServerRefreshBacklogRepository
{
    Task<MediaServerRefreshBacklogItem?> FindAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken);

    /// <summary>Every server and media type currently failing, for the Health screen (#920).</summary>
    Task<IReadOnlyList<MediaServerRefreshBacklogItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records another failed attempt: keeps the first failure's timestamp,
    /// replaces the pending changes with everything still unsent, bumps the
    /// attempt count and schedules the next try with backoff.
    /// </summary>
    Task<MediaServerRefreshBacklogItem> RecordFailureAsync(
        string mediaServerId,
        string mediaType,
        IReadOnlyList<MediaServerPathChange> pendingChanges,
        CancellationToken cancellationToken);

    /// <summary>The server answered for this media type: nothing left to retry.</summary>
    Task ClearAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken);
}

public sealed class SqliteMediaServerRefreshBacklogRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IMediaServerRefreshBacklogRepository
{
    private const string Columns = "media_server_id, media_type, first_failed_utc, next_attempt_utc, attempt_count, changes_json, updated_utc";

    public async Task<MediaServerRefreshBacklogItem?> FindAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM media_server_refresh_backlog WHERE media_server_id = @id AND media_type = @mediaType LIMIT 1;";
        AddParameter(command, "@id", mediaServerId);
        AddParameter(command, "@mediaType", mediaType);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<MediaServerRefreshBacklogItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM media_server_refresh_backlog ORDER BY first_failed_utc ASC;";
        var items = new List<MediaServerRefreshBacklogItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<MediaServerRefreshBacklogItem> RecordFailureAsync(
        string mediaServerId,
        string mediaType,
        IReadOnlyList<MediaServerPathChange> pendingChanges,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await FindAsync(mediaServerId, mediaType, cancellationToken);
        var attemptCount = (existing?.AttemptCount ?? 0) + 1;
        var firstFailedUtc = existing?.FirstFailedUtc ?? now;
        var nextAttemptUtc = now + MediaServerRefreshBackoff.Next(attemptCount);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO media_server_refresh_backlog (media_server_id, media_type, first_failed_utc, next_attempt_utc, attempt_count, changes_json, updated_utc)
            VALUES (@id, @mediaType, @firstFailed, @nextAttempt, @attempts, @changes, @now)
            ON CONFLICT(media_server_id, media_type) DO UPDATE SET
                next_attempt_utc = excluded.next_attempt_utc,
                attempt_count = excluded.attempt_count,
                changes_json = excluded.changes_json,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@id", mediaServerId);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@firstFailed", firstFailedUtc.ToString("O"));
        AddParameter(command, "@nextAttempt", nextAttemptUtc.ToString("O"));
        AddParameter(command, "@attempts", attemptCount);
        AddParameter(command, "@changes", JsonSerializer.Serialize(pendingChanges));
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new MediaServerRefreshBacklogItem(mediaServerId, mediaType, firstFailedUtc, nextAttemptUtc, attemptCount, pendingChanges, now);
    }

    public async Task ClearAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_server_refresh_backlog WHERE media_server_id = @id AND media_type = @mediaType;";
        AddParameter(command, "@id", mediaServerId);
        AddParameter(command, "@mediaType", mediaType);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MediaServerRefreshBacklogItem Read(System.Data.Common.DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            JsonSerializer.Deserialize<IReadOnlyList<MediaServerPathChange>>(reader.GetString(5)) ?? [],
            DateTimeOffset.Parse(reader.GetString(6)));
}
