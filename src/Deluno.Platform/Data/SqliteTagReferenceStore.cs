using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// Every place in the platform database that stores a tag by name (#1039):
/// indexers, download clients, notification connections, acquisition
/// (release) rules, seeding (sharing) rules, library source rules and
/// destination rules. Rename and delete rewrite all of them together with the
/// tag definition in one transaction so no tag name is left dangling. Title
/// tags live in the movie and series databases and are handled by
/// <c>IMediaTagStore</c> straight afterwards.
/// </summary>
public interface ITagReferenceStore
{
    /// <summary>Every tag name (lower-cased) to the connections and rules using it.</summary>
    Task<IReadOnlyDictionary<string, List<TagUserItem>>> ListAllUsersAsync(CancellationToken cancellationToken);

    /// <summary>Renames the tag definition and every stored use of it in one transaction.</summary>
    Task RenameAsync(string tagId, string previousName, string nextName, CancellationToken cancellationToken);

    /// <summary>Removes the tag from every stored use and deletes its definition in one transaction.</summary>
    Task<bool> DeleteAsync(string tagId, string name, CancellationToken cancellationToken);
}

public sealed class SqliteTagReferenceStore(IDelunoDatabaseConnectionFactory databaseConnectionFactory) : ITagReferenceStore
{
    public async Task<IReadOnlyDictionary<string, List<TagUserItem>>> ListAllUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var result = new Dictionary<string, List<TagUserItem>>(StringComparer.Ordinal);
        foreach (var row in await ReadRowsAsync(connection, null, cancellationToken))
        {
            foreach (var tag in row.Tags)
            {
                var key = tag.ToLowerInvariant();
                if (!result.TryGetValue(key, out var list))
                {
                    result[key] = list = [];
                }

                if (!list.Any(item => item.Kind == row.Kind && item.Id == row.Id))
                {
                    list.Add(new TagUserItem(row.Kind, row.Id, row.Label, row.Link));
                }
            }
        }

        return result;
    }

    public async Task RenameAsync(string tagId, string previousName, string nextName, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var previous = previousName.Trim();
        var next = nextName.Trim();

        foreach (var row in await ReadRowsAsync(connection, transaction, cancellationToken))
        {
            if (!row.Tags.Contains(previous, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var updated = row.Tags
                .Select(tag => string.Equals(tag, previous, StringComparison.OrdinalIgnoreCase) ? next : tag)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            await WriteAsync(connection, transaction, row, string.Join(",", updated), cancellationToken);
        }

        await Exec(connection, transaction, "UPDATE tags SET name = @name WHERE id = @id;", cancellationToken, ("@name", next), ("@id", tagId));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string tagId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var target = name.Trim();

        foreach (var row in await ReadRowsAsync(connection, transaction, cancellationToken))
        {
            if (!row.Tags.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var remaining = row.Tags.Where(tag => !string.Equals(tag, target, StringComparison.OrdinalIgnoreCase)).ToArray();
            switch (row.Table)
            {
                case "release_profiles":
                    // A tag-keyed rule with no tag would become the global one,
                    // so the rule goes with its tag.
                    await Exec(connection, transaction, "DELETE FROM release_profiles WHERE id = @id;", cancellationToken, ("@id", row.Id));
                    break;
                case "destination_rules":
                    await Exec(connection, transaction, "DELETE FROM destination_rules WHERE id = @id;", cancellationToken, ("@id", row.Id));
                    break;
                case "sharing_rules" when remaining.Length == 0 && !row.HasOtherSharingMatch:
                    // With no tag left it would match every download; switch it off instead.
                    await WriteAsync(connection, transaction, row, string.Empty, cancellationToken);
                    await Exec(connection, transaction, "UPDATE sharing_rules SET is_enabled = 0 WHERE id = @id;", cancellationToken, ("@id", row.Id));
                    break;
                default:
                    await WriteAsync(connection, transaction, row, string.Join(",", remaining), cancellationToken);
                    break;
            }
        }

        var removed = await Exec(connection, transaction, "DELETE FROM tags WHERE id = @id;", cancellationToken, ("@id", tagId)) > 0;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private sealed record TagRow(
        string Table,
        string Column,
        string Kind,
        string Id,
        string Label,
        string Link,
        IReadOnlyList<string> Tags,
        bool HasOtherSharingMatch = false);

    private static async Task<List<TagRow>> ReadRowsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        var rows = new List<TagRow>();

        async Task Read(string sql, Func<DbDataReader, TagRow> map)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }
        }

        await Read("SELECT id, name, tags FROM indexer_sources WHERE trim(tags) <> '';",
            r => new TagRow("indexer_sources", "tags", "indexer", r.GetString(0), r.GetString(1), "/indexers/indexers", Split(r.GetString(2))));
        await Read("SELECT id, name, tags FROM download_clients WHERE trim(tags) <> '';",
            r => new TagRow("download_clients", "tags", "downloadClient", r.GetString(0), r.GetString(1), "/indexers/download-clients", Split(r.GetString(2))));
        await Read("SELECT id, name, tags FROM notification_webhooks WHERE trim(tags) <> '';",
            r => new TagRow("notification_webhooks", "tags", "notification", r.GetString(0), r.GetString(1), "/settings/notifications", Split(r.GetString(2))));
        await Read("SELECT id, tags_csv, category_pattern, tracker_pattern FROM sharing_rules WHERE trim(coalesce(tags_csv, '')) <> '';",
            r => new TagRow("sharing_rules", "tags_csv", "sharingRule", r.GetString(0), SharingLabel(r), "/indexers/download-clients", Split(r.GetString(1)),
                HasText(r, 2) || HasText(r, 3)));
        foreach (var column in new[] { "required_tags", "excluded_tags" })
        {
            await Read($"SELECT l.id, coalesce(lib.name, l.library_id), l.{column}, l.library_id FROM library_source_links l LEFT JOIN libraries lib ON lib.id = l.library_id WHERE trim(l.{column}) <> '';",
                r => new TagRow("library_source_links", column, "librarySource", r.GetString(0), r.GetString(1), $"/settings/libraries?libraryId={Uri.EscapeDataString(r.GetString(3))}", Split(r.GetString(2))));
        }

        await Read("SELECT id, tag_name, name FROM release_profiles WHERE trim(tag_name) <> '';",
            r => new TagRow("release_profiles", "tag_name", "acquisitionRule", r.GetString(0), r.GetString(2), "/settings/release-rules", [r.GetString(1).Trim()]));
        await Read("SELECT id, name, match_value FROM destination_rules WHERE lower(trim(match_kind)) = 'tag' AND trim(match_value) <> '';",
            r => new TagRow("destination_rules", "match_value", "destinationRule", r.GetString(0), r.GetString(1), "/settings/destination-rules", Split(r.GetString(2))));
        return rows;
    }

    private static bool HasText(DbDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && !string.IsNullOrWhiteSpace(reader.GetString(ordinal));

    private static string SharingLabel(DbDataReader reader)
    {
        var parts = new[] { reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3) }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var text = string.Join(" / ", parts);
        return text.Length == 0 ? "Seeding rule" : $"Seeding rule for {text}";
    }

    private static Task WriteAsync(DbConnection connection, DbTransaction transaction, TagRow row, string value, CancellationToken cancellationToken)
    {
        // Table and column names come from the closed list in ReadRowsAsync, never from input.
        object? stored = row.Table == "sharing_rules" && value.Length == 0 ? null : value;
        return Exec(connection, transaction, $"UPDATE {row.Table} SET {row.Column} = @value WHERE id = @id;", cancellationToken, ("@value", stored), ("@id", row.Id));
    }

    private static async Task<int> Exec(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string[] Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
