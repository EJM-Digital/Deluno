using System.Data.Common;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// Catalogue-local persistence for user-owned tags. The platform database owns
/// the definitions; this store owns only the relationship between a definition
/// and a movie or show, which keeps filtering and assignment inside the same
/// SQLite database as the title.
/// </summary>
public sealed class SqliteMediaTagStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IMediaTagStore
{
    public async Task<IReadOnlyList<MediaTagAssignment>> ListAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT tag_id, tag_name
            FROM {map.TagTable}
            WHERE {map.TagMediaIdColumn} = @mediaId
            ORDER BY tag_name COLLATE NOCASE ASC, tag_id ASC;
            """;
        AddParameter(command, "@mediaId", mediaId);

        var items = new List<MediaTagAssignment>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MediaTagAssignment(reader.GetString(0), reader.GetString(1)));
        }

        return items;
    }

    public async Task ReplaceAsync(
        MediaKind kind,
        string mediaId,
        IReadOnlyList<MediaTagAssignment> assignments,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {map.TagTable} WHERE {map.TagMediaIdColumn} = @mediaId;";
            AddParameter(delete, "@mediaId", mediaId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var assignment in assignments
                     .Where(item => !string.IsNullOrWhiteSpace(item.TagId) && !string.IsNullOrWhiteSpace(item.Name))
                     .GroupBy(item => item.TagId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {map.TagTable} ({map.TagMediaIdColumn}, tag_id, tag_name, created_utc)
                VALUES (@mediaId, @tagId, @tagName, @createdUtc);
                """;
            AddParameter(insert, "@mediaId", mediaId);
            AddParameter(insert, "@tagId", assignment.TagId.Trim());
            AddParameter(insert, "@tagName", assignment.Name.Trim());
            AddParameter(insert, "@createdUtc", timeProvider.GetUtcNow().ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaTagUsage>> ListUsageAsync(
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT lower(trim(tag_name)) AS normalized_name,
                   MIN(trim(tag_name)) AS display_name,
                   COUNT(DISTINCT {map.TagMediaIdColumn}) AS title_count
            FROM {map.TagTable}
            WHERE trim(tag_name) <> ''
            GROUP BY normalized_name
            ORDER BY normalized_name ASC;
            """;

        var items = new List<MediaTagUsage>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MediaTagUsage(reader.GetString(1), reader.GetInt32(2)));
        }

        return items;
    }

    public async Task RenameAsync(
        MediaKind kind,
        string tagId,
        string previousName,
        string nextName,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // A pre-join build used a deterministic legacy id. If the managed tag
        // now exists with the same name, discard that duplicate before moving
        // the legacy relationship to the real platform id.
        using (var removeDuplicate = connection.CreateCommand())
        {
            removeDuplicate.Transaction = transaction;
            removeDuplicate.CommandText = $"""
                DELETE FROM {map.TagTable} AS legacy
                WHERE lower(trim(legacy.tag_name)) = lower(trim(@previousName))
                  AND legacy.tag_id <> @tagId
                  AND EXISTS (
                      SELECT 1
                      FROM {map.TagTable} AS managed
                      WHERE managed.{map.TagMediaIdColumn} = legacy.{map.TagMediaIdColumn}
                        AND managed.tag_id = @tagId);
                """;
            AddParameter(removeDuplicate, "@previousName", previousName);
            AddParameter(removeDuplicate, "@tagId", tagId);
            await removeDuplicate.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE {map.TagTable}
                SET tag_id = @tagId,
                    tag_name = @nextName
                WHERE tag_id = @tagId
                   OR lower(trim(tag_name)) = lower(trim(@previousName));
                """;
            AddParameter(update, "@tagId", tagId);
            AddParameter(update, "@nextName", nextName.Trim());
            AddParameter(update, "@previousName", previousName);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(
        MediaKind kind,
        string tagId,
        string name,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM {map.TagTable}
            WHERE tag_id = @tagId
               OR lower(trim(tag_name)) = lower(trim(@name));
            """;
        AddParameter(command, "@tagId", tagId);
        AddParameter(command, "@name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public static class MediaTagIds
{
    /// <summary>
    /// Stable id for a free-form label written before a managed platform tag
    /// exists. It lets migration and the editor preserve the label without
    /// pretending it is a platform row; the next managed save upgrades it.
    /// </summary>
    public static string ForLegacyName(string name)
        => "legacy:" + name.Trim().ToLowerInvariant();
}
