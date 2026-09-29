using System.Data.Common;
using Deluno.Contracts;
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

    /// <summary>Deletes the definition of a tag that nothing uses; the caller checks first (#1039).</summary>
    Task<bool> DeleteAsync(string tagId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Adds, removes or replaces tags on several indexers, download clients or
    /// notification connections in one transaction, with the same modes as the
    /// title bulk editor (#1044). Returns each connection changed, by name.
    /// </summary>
    Task<IReadOnlyList<string>> BulkApplyAsync(
        BulkTagTarget target,
        IReadOnlyList<string> ids,
        BulkTagMode mode,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken);
}

/// <summary>The kinds of connection that can be tagged in bulk (#1044).</summary>
public enum BulkTagTarget
{
    Indexer,
    DownloadClient,
    Notification,
}

public sealed class SqliteTagReferenceStore(IDelunoDatabaseConnectionFactory databaseConnectionFactory) : ITagReferenceStore
{
    public async Task<IReadOnlyDictionary<string, List<TagUserItem>>> ListAllUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var result = new Dictionary<string, List<TagUserItem>>(StringComparer.Ordinal);
        var allRows = await ReadRowsAsync(connection, null, cancellationToken);
        allRows.AddRange(await ReadCollectionRowsAsync(cancellationToken));
        foreach (var row in allRows)
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

        // #1046: a collection's tags live in the movie database, so they follow the rename there.
        try
        {
            await using var movies = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
            foreach (var row in await ReadCollectionRowsAsync(movies, cancellationToken))
            {
                if (!row.Tags.Contains(previous, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var updated = row.Tags
                    .Select(tag => string.Equals(tag, previous, StringComparison.OrdinalIgnoreCase) ? next : tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                await Exec(movies, null, "UPDATE movie_collections SET tags = @value WHERE id = @id;", cancellationToken, ("@value", string.Join(",", updated)), ("@id", row.Id));
            }
        }
        catch (DbException)
        {
            // No movie database yet means no collection can carry the tag.
        }
    }

    private async Task<List<TagRow>> ReadCollectionRowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var movies = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
            return await ReadCollectionRowsAsync(movies, cancellationToken);
        }
        catch (DbException)
        {
            return [];
        }
    }

    private static async Task<List<TagRow>> ReadCollectionRowsAsync(DbConnection movies, CancellationToken cancellationToken)
    {
        var rows = new List<TagRow>();
        using var command = movies.CreateCommand();
        command.CommandText = "SELECT id, name, tags FROM movie_collections WHERE trim(tags) <> '';";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TagRow("movie_collections", "tags", "collection", reader.GetString(0), reader.GetString(1), "/collections", Split(reader.GetString(2))));
        }

        return rows;
    }

    public async Task<bool> DeleteAsync(string tagId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var removed = await Exec(connection, transaction, "DELETE FROM tags WHERE id = @id;", cancellationToken, ("@id", tagId)) > 0;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<IReadOnlyList<string>> BulkApplyAsync(
        BulkTagTarget target,
        IReadOnlyList<string> ids,
        BulkTagMode mode,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        // Table names come from this closed map, never from input.
        var table = target switch
        {
            BulkTagTarget.Indexer => "indexer_sources",
            BulkTagTarget.DownloadClient => "download_clients",
            BulkTagTarget.Notification => "notification_webhooks",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var requested = tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Select(tag => new MediaTagAssignment(tag, tag))
            .ToArray();
        var changed = new List<string>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            string name;
            string current;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT name, tags FROM {table} WHERE id = @id;";
                AddParameter(command, "@id", id);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    continue;
                }

                name = reader.GetString(0);
                current = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }

            var existing = Split(current).Select(tag => new MediaTagAssignment(tag, tag)).ToArray();
            var final = MediaTagBulkOperations.Apply(mode, existing, requested)
                .Select(item => item.Name.Trim())
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var next = string.Join(",", final);
            if (string.Equals(next, string.Join(",", existing.Select(item => item.Name)), StringComparison.Ordinal))
            {
                continue;
            }

            await Exec(connection, transaction, $"UPDATE {table} SET tags = @value WHERE id = @id;", cancellationToken, ("@value", next), ("@id", id));
            changed.Add(name);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private sealed record TagRow(
        string Table,
        string Column,
        string Kind,
        string Id,
        string Label,
        string Link,
        IReadOnlyList<string> Tags,
        bool HasOtherSharingMatch = false,
        string IdColumn = "id");

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
        // #1046: a Seerr connection gives tags to the titles it adds, and the optional
        // "no longer on any list" setting names one; both must follow a rename and block a delete.
        foreach (var column in new[] { "tags", "tags_4k" })
        {
            await Read($"SELECT id, name, {column} FROM seerr_servers WHERE trim({column}) <> '';",
                r => new TagRow("seerr_servers", column, "seerrConnection", r.GetString(0), r.GetString(1), "/indexers/media-servers", Split(r.GetString(2))));
        }

        await Read("SELECT setting_key, setting_value FROM system_settings WHERE setting_key = 'lists.offListTag' AND trim(setting_value) <> '';",
            r => new TagRow("system_settings", "setting_value", "listSetting", r.GetString(0), "Titles no longer on any list", "/settings/lists", [r.GetString(1).Trim()], IdColumn: "setting_key"));
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

        // #994: a rule can carry several tags. tag_name keeps the first of them for older readers,
        // and WriteAsync keeps it in step when a rename rewrites tag_names.
        await Read("SELECT id, CASE WHEN trim(tag_names) <> '' THEN tag_names ELSE tag_name END, name FROM release_profiles WHERE trim(tag_names) <> '' OR trim(tag_name) <> '';",
            r => new TagRow("release_profiles", "tag_names", "acquisitionRule", r.GetString(0), r.GetString(2), "/settings/release-rules", Split(r.GetString(1))));
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

    private static async Task WriteAsync(DbConnection connection, DbTransaction transaction, TagRow row, string value, CancellationToken cancellationToken)
    {
        if (row.Table == "release_profiles")
        {
            var first = Split(value).FirstOrDefault() ?? string.Empty;
            await Exec(connection, transaction, "UPDATE release_profiles SET tag_name = @first WHERE id = @id;", cancellationToken, ("@first", first), ("@id", row.Id));
        }

        // Table and column names come from the closed list in ReadRowsAsync, never from input.
        object? stored = row.Table == "sharing_rules" && value.Length == 0 ? null : value;
        await Exec(connection, transaction, $"UPDATE {row.Table} SET {row.Column} = @value WHERE {row.IdColumn} = @id;", cancellationToken, ("@value", stored), ("@id", row.Id));
    }

    private static async Task<int> Exec(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
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
