using System.Data.Common;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Platform;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// Keeps a title's catalogue record before a removal deletes it, so the
/// recycle bin can put the same title back with its files (#1161).
/// </summary>
public interface ITitleSnapshotService
{
    /// <summary>
    /// Every row the title owns (its entry, wanted state per library, search
    /// history, seasons, episodes, subtitles, tags) as opaque JSON, or null
    /// when the title is not in its catalogue.
    /// </summary>
    Task<string?> CaptureAsync(string mediaType, string titleId, CancellationToken cancellationToken);
}

/// <summary>
/// Captures and restores a title by following the database's own foreign
/// keys, rather than a list of table names. A table added later that cascades
/// from a title is captured without anyone remembering to add it here, and a
/// restore only writes columns the table still has.
/// </summary>
public sealed class TitleSnapshotService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IExistingLibraryImportService? libraryImport = null)
    : ITitleSnapshotService, ITitleRestorer
{
    public bool Handles(string mediaType)
        => string.Equals(mediaType, "movies", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> CaptureAsync(string mediaType, string titleId, CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(KindOf(mediaType));
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(map.DatabaseName, cancellationToken);

        var plan = await PlanAsync(connection, map.EntryTable, cancellationToken);
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("root", map.EntryTable);
            writer.WriteStartArray("tables");
            var rootFound = false;
            foreach (var table in plan)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table.Name}\" WHERE {table.Where};";
                AddParameter(command, "@id", titleId);
                var rows = 0;
                writer.WriteStartObject();
                writer.WriteString("name", table.Name);
                writer.WriteStartArray("rows");
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        writer.WriteStartObject();
                        for (var index = 0; index < reader.FieldCount; index++)
                        {
                            writer.WritePropertyName(reader.GetName(index));
                            WriteValue(writer, reader.IsDBNull(index) ? null : reader.GetValue(index));
                        }

                        writer.WriteEndObject();
                        rows++;
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                if (string.Equals(table.Name, map.EntryTable, StringComparison.OrdinalIgnoreCase) && rows > 0)
                {
                    rootFound = true;
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            if (!rootFound)
            {
                return null;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public async Task<TitleRestoreOutcome> RestoreAsync(RecycleBinTitleSnapshot snapshot, CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(KindOf(snapshot.MediaType));
        using var document = JsonDocument.Parse(snapshot.DataJson);
        var libraryIds = new List<string>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(map.DatabaseName, cancellationToken);

        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = $"SELECT 1 FROM \"{map.EntryTable}\" WHERE id = @id LIMIT 1;";
            AddParameter(exists, "@id", snapshot.TitleId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return new TitleRestoreOutcome(true, $"{snapshot.Title} is already in Deluno.", snapshot.TitleId);
            }
        }

        // The same title was added again before it was restored, and the
        // catalogue keeps one entry per title. Keep that one and match the
        // files that just came back to it.
        string? existingId = null;
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText =
                $"SELECT id FROM \"{map.EntryTable}\" WHERE lower(title) = lower(@title) AND (({map.YearColumn} IS NULL AND @year IS NULL) OR {map.YearColumn} = @year) LIMIT 1;";
            AddParameter(lookup, "@title", snapshot.Title.Trim());
            AddParameter(lookup, "@year", snapshot.Year);
            existingId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }

        foreach (var table in document.RootElement.GetProperty("tables").EnumerateArray())
        {
            if (!string.Equals(table.GetProperty("name").GetString(), map.WantedTable, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var row in table.GetProperty("rows").EnumerateArray())
            {
                if (row.TryGetProperty("library_id", out var library) && library.GetString() is { Length: > 0 } libraryId &&
                    !libraryIds.Contains(libraryId, StringComparer.OrdinalIgnoreCase))
                {
                    libraryIds.Add(libraryId);
                }
            }
        }

        if (existingId is not null)
        {
            return await KeepExistingAsync(existingId);
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            // Rows reference each other in both directions (an episode's season,
            // a season's show); the checks run when the whole title is in.
            using (var defer = connection.CreateCommand())
            {
                defer.Transaction = transaction;
                defer.CommandText = "PRAGMA defer_foreign_keys = ON;";
                await defer.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var table in document.RootElement.GetProperty("tables").EnumerateArray())
            {
                var name = table.GetProperty("name").GetString()!;
                var columns = await ColumnsOfAsync(connection, transaction, name, cancellationToken);
                if (columns.Count == 0)
                {
                    continue;
                }

                foreach (var row in table.GetProperty("rows").EnumerateArray())
                {
                    var present = row.EnumerateObject().Where(property => columns.Contains(property.Name)).ToArray();
                    if (present.Length == 0)
                    {
                        continue;
                    }

                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        $"INSERT OR IGNORE INTO \"{name}\" ({string.Join(", ", present.Select(property => $"\"{property.Name}\""))}) " +
                        $"VALUES ({string.Join(", ", present.Select((_, index) => $"@p{index}"))});";
                    for (var index = 0; index < present.Length; index++)
                    {
                        AddParameter(insert, $"@p{index}", ReadValue(present[index].Value));
                    }

                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT 1 FROM \"{map.EntryTable}\" WHERE id = @id LIMIT 1;";
            AddParameter(check, "@id", snapshot.TitleId);
            if (await check.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return new TitleRestoreOutcome(
                    true,
                    $"{snapshot.Title} is back in Deluno with its files, monitoring and history.",
                    snapshot.TitleId);
            }
        }

        return new TitleRestoreOutcome(
            false,
            $"{snapshot.Title}'s files are back, but Deluno could not put the title back. Add {snapshot.Title} again and it will find these files.");

        async Task<TitleRestoreOutcome> KeepExistingAsync(string existing)
        {
            if (libraryImport is not null)
            {
                foreach (var libraryId in libraryIds)
                {
                    await libraryImport.ImportTitleFolderAsync(libraryId, existing, snapshot.Title, snapshot.Year, cancellationToken);
                }
            }

            return new TitleRestoreOutcome(
                true,
                $"{snapshot.Title} was already added to Deluno again, so Deluno kept that one and matched the restored files to it.",
                existing);
        }
    }

    private static MediaKind KindOf(string mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? MediaKind.Series : MediaKind.Movie;

    private sealed record TablePlan(string Name, string Where);

    /// <summary>
    /// The tables to copy, parents before children, each with the SQL that
    /// selects only this title's rows. A table is included when it cascades
    /// from one already included.
    /// </summary>
    private static async Task<IReadOnlyList<TablePlan>> PlanAsync(DbConnection connection, string rootTable, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var foreignKeys = new Dictionary<string, List<(string Parent, string From, string To)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var parentOrdinal = reader.GetOrdinal("table");
            var fromOrdinal = reader.GetOrdinal("from");
            var toOrdinal = reader.GetOrdinal("to");
            var deleteOrdinal = reader.GetOrdinal("on_delete");
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(deleteOrdinal), "CASCADE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!foreignKeys.TryGetValue(table, out var list))
                {
                    foreignKeys[table] = list = [];
                }

                list.Add((reader.GetString(parentOrdinal), reader.GetString(fromOrdinal), reader.IsDBNull(toOrdinal) ? "id" : reader.GetString(toOrdinal)));
            }
        }

        var plan = new List<TablePlan> { new(rootTable, "id = @id") };
        var included = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [rootTable] = "id = @id" };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var table in tables)
            {
                if (included.ContainsKey(table) || !foreignKeys.TryGetValue(table, out var keys))
                {
                    continue;
                }

                var key = keys.FirstOrDefault(candidate => included.ContainsKey(candidate.Parent));
                if (key.Parent is null)
                {
                    continue;
                }

                var where = $"\"{key.From}\" IN (SELECT \"{key.To}\" FROM \"{key.Parent}\" WHERE {included[key.Parent]})";
                included[table] = where;
                plan.Add(new TablePlan(table, where));
                changed = true;
            }
        }

        return plan;
    }

    private static async Task<HashSet<string>> ColumnsOfAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nameOrdinal = reader.GetOrdinal("name");
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(nameOrdinal));
        }

        return columns;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case bool flag:
                writer.WriteNumberValue(flag ? 1 : 0);
                break;
            case byte[] bytes:
                writer.WriteStringValue(Convert.ToBase64String(bytes));
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    private static object? ReadValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number : value.GetDouble(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            _ => value.GetString()
        };
}
