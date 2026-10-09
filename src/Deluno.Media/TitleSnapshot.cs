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

    /// <summary>
    /// #1188: a film's or an episode's file came back out of the recycle bin.
    /// The title never left Deluno, so its folder is read the way an added
    /// title's is, which takes the file again (quality, size and all), and the
    /// title is watched again if it was before the file was taken.
    /// </summary>
    public async Task<TitleRestoreOutcome> RelinkFileAsync(
        RecycleBinItem restored,
        RecycleBinFileOwner owner,
        CancellationToken cancellationToken)
    {
        if (libraryImport is null)
        {
            return new TitleRestoreOutcome(false, string.Empty);
        }

        var found = await libraryImport.ImportTitleFolderAsync(restored.LibraryId, owner.TitleId, owner.Title, owner.Year, cancellationToken);
        var map = MediaTableMap.For(KindOf(owner.MediaType));
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(map.DatabaseName, cancellationToken);
        if (owner.WasMonitored)
        {
            using var watch = connection.CreateCommand();
            watch.CommandText = owner.EpisodeId is { Length: > 0 }
                ? "UPDATE episode_entries SET monitored = 1 WHERE id = @id;"
                : $"UPDATE \"{map.EntryTable}\" SET monitored = 1 WHERE id = @id;";
            AddParameter(watch, "@id", owner.EpisodeId is { Length: > 0 } ? owner.EpisodeId : owner.TitleId);
            await watch.ExecuteNonQueryAsync(cancellationToken);
        }

        if (owner.EpisodeId is not { Length: > 0 } && map.WantedTable == "movie_wanted_state")
        {
            using var wanted = connection.CreateCommand();
            wanted.CommandText = $"UPDATE \"{map.WantedTable}\" SET not_wanted_here = 0, not_wanted_since_utc = NULL WHERE {map.WantedMediaIdColumn} = @id AND library_id = @libraryId AND not_wanted_here = 1;";
            AddParameter(wanted, "@id", owner.TitleId);
            AddParameter(wanted, "@libraryId", restored.LibraryId);
            await wanted.ExecuteNonQueryAsync(cancellationToken);
        }

        // What the person is told names the thing that came back: the film, or
        // the episode, not only the show it belongs to.
        var name = owner.Title;
        var linked = found.Matched;
        if (owner.EpisodeId is { Length: > 0 })
        {
            using var episode = connection.CreateCommand();
            episode.CommandText = "SELECT season_number, episode_number, has_file FROM episode_entries WHERE id = @id;";
            AddParameter(episode, "@id", owner.EpisodeId);
            using var reader = await episode.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                name = $"{owner.Title} S{reader.GetInt32(0):00}E{reader.GetInt32(1):00}";
                linked = found.Matched && reader.GetInt64(2) == 1;
            }
            else
            {
                linked = false;
            }
        }

        return linked
            ? new TitleRestoreOutcome(true, $"{name} has its file again.", owner.TitleId)
            : new TitleRestoreOutcome(false, $"The file is back, but Deluno could not match it to {name}. A library check will find it.", owner.TitleId);
    }

    /// <summary>
    /// #1189: a file came back from the bin with no recorded owner (it was
    /// binned by a replacement, the wrong-episode repair, a library check and
    /// so on, before every writer recorded one). The owner is found from where
    /// the file lived, the way an import reads a library: the title folder's
    /// name and year pick the film or show, and a show's file name picks the
    /// episode.
    /// </summary>
    public async Task<TitleRestoreOutcome> RelinkUnownedFileAsync(
        RecycleBinItem restored,
        CancellationToken cancellationToken)
    {
        if (libraryImport is null)
        {
            return new TitleRestoreOutcome(false, string.Empty);
        }

        var fileName = Path.GetFileName(restored.OriginalPath);
        if (SidecarExtensions.Contains(Path.GetExtension(fileName)))
        {
            // A subtitle or picture is not what a title is linked by.
            return new TitleRestoreOutcome(false, string.Empty);
        }

        var owner = await FindFileOwnerAsync(restored.LibraryId, restored.MediaType, restored.OriginalPath, cancellationToken);
        if (owner is null)
        {
            return new TitleRestoreOutcome(
                false,
                $"The file is back, but Deluno could not tell which {(KindOf(restored.MediaType) == MediaKind.Series ? "show" : "film")} it belongs to, so nothing has been linked to it. Use Import Existing on the library, or add the title again, and Deluno will find {fileName}.");
        }

        return await RelinkFileAsync(restored, owner, cancellationToken);
    }

    /// <summary>
    /// The film or episode a library file belongs to, found the way an import
    /// reads a library: the title folder's name and year pick the film or show,
    /// and a show's file name picks the episode (#1189). Null when it is not
    /// exactly one title, or the file is a subtitle or picture.
    /// </summary>
    public async Task<RecycleBinFileOwner?> FindFileOwnerAsync(
        string libraryId,
        string mediaType,
        string path,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        if (libraryImport is null || SidecarExtensions.Contains(Path.GetExtension(fileName)))
        {
            return null;
        }

        var guess = await libraryImport.ReadTitleOfPathAsync(libraryId, path, cancellationToken);
        if (guess is null)
        {
            return null;
        }

        var kind = KindOf(mediaType);
        var map = MediaTableMap.For(kind);
        var wanted = NormalizeTitle(guess.Title);
        var candidates = new List<(string Id, string Title, int? Year, bool Monitored)>();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(map.DatabaseName, cancellationToken);
        using (var list = connection.CreateCommand())
        {
            list.CommandText = $"SELECT id, title, \"{map.YearColumn}\", monitored FROM \"{map.EntryTable}\";";
            using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (NormalizeTitle(reader.GetString(1)) == wanted)
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetInt64(3) == 1));
                }
            }
        }

        var matches = candidates.Where(candidate => guess.Year is null || candidate.Year is null || candidate.Year == guess.Year).ToList();
        if (matches.Count != 1)
        {
            return null;
        }

        var (titleId, title, year, monitored) = matches[0];
        string? episodeId = null;
        if (kind == MediaKind.Series)
        {
            var code = System.Text.RegularExpressions.Regex.Match(fileName, @"[Ss](\d{1,3})[Ee](\d{1,4})");
            if (code.Success)
            {
                using var find = connection.CreateCommand();
                find.CommandText = "SELECT id, monitored FROM episode_entries WHERE series_id = @seriesId AND season_number = @season AND episode_number = @episode;";
                AddParameter(find, "@seriesId", titleId);
                AddParameter(find, "@season", int.Parse(code.Groups[1].Value));
                AddParameter(find, "@episode", int.Parse(code.Groups[2].Value));
                using var reader = await find.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    episodeId = reader.GetString(0);
                    monitored = reader.GetInt64(1) == 1;
                }
            }
        }

        return new RecycleBinFileOwner(mediaType, kind == MediaKind.Series ? "series" : "movie", titleId, title, year, episodeId, monitored);
    }

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".nfo", ".jpg", ".jpeg", ".png"
    };

    private static string NormalizeTitle(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
