using System.Data.Common;
using System.Globalization;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Migrations;
using Deluno.Jobs.Migrations;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Migrations;
using Deluno.Series.Migrations;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>One of Deluno's migrated SQLite databases and the migrations that build it.</summary>
internal sealed record MigratedDatabase(string Name, IReadOnlyList<IDelunoDatabaseMigration> Migrations)
{
    public int Latest => Migrations.Max(migration => migration.Version);

    public override string ToString() => Name;
}

/// <summary>
/// #1274. Builds a database at one migration, fills every table with rows whose
/// values and links are known, applies the next migration, and says what changed.
///
/// <para>The rows are generated from the schema the previous migration left, so a
/// migration written next month is covered the day it is added, with no one
/// remembering to write a fixture. Where the meaning of a row depends on a
/// particular value (a status string, a setting key) the migration has its own
/// hand-written test as well, named in <see cref="DataChangingMigrations"/>.</para>
/// </summary>
internal static class MigrationUpgradeHarness
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T05:00:00Z", CultureInfo.InvariantCulture);

    public static IReadOnlyList<MigratedDatabase> Databases { get; } =
    [
        new(DelunoDatabaseNames.Platform, PlatformDatabaseMigrations.All),
        new(DelunoDatabaseNames.Movies, MoviesDatabaseMigrations.All),
        new(DelunoDatabaseNames.Series, SeriesDatabaseMigrations.All),
        new(DelunoDatabaseNames.Jobs, JobsDatabaseMigrations.All),
        new(DelunoDatabaseNames.Cache, CacheDatabaseMigrations.All)
    ];

    public static MigratedDatabase Database(string name) => Databases.Single(database => database.Name == name);

    /// <summary>Every migration of every database, as theory data: database name, version.</summary>
    public static IEnumerable<object[]> EveryMigration() =>
        Databases.SelectMany(database => database.Migrations
            .OrderBy(migration => migration.Version)
            .Select(migration => new object[] { database.Name, migration.Version }));

    /// <summary>Applies the migrations whose version is at most <paramref name="throughVersion"/>.</summary>
    public static async Task ApplyThroughAsync(TestStorage storage, MigratedDatabase database, int throughVersion)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now));
        var wanted = database.Migrations.Where(migration => migration.Version <= throughVersion).ToArray();
        if (wanted.Length == 0)
        {
            return;
        }

        await migrator.ApplyAsync(database.Name, wanted, CancellationToken.None);
    }

    /// <summary>Applies exactly one more migration on top of what the database already has.</summary>
    public static async Task ApplyOnlyAsync(TestStorage storage, MigratedDatabase database, int version)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now));
        var wanted = database.Migrations.Where(migration => migration.Version <= version).ToArray();
        await migrator.ApplyAsync(database.Name, wanted, CancellationToken.None);
    }

    public static int PreviousVersion(MigratedDatabase database, int version)
        => database.Migrations.Where(migration => migration.Version < version).Select(migration => migration.Version).DefaultIfEmpty(0).Max();

    public static async Task<DbConnection> OpenAsync(TestStorage storage, string databaseName)
        => await storage.Factory.OpenConnectionAsync(databaseName);

    public static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public static async Task<List<Dictionary<string, object?>>> RowsAsync(DbConnection connection, string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    // ---- the shape of a database ---------------------------------------------------------

    public sealed record ColumnInfo(string Name, string Type, bool NotNull, string? Default, bool PrimaryKey);

    public sealed record ForeignKeyInfo(string Column, string ParentTable, string ParentColumn);

    public sealed class Snapshot
    {
        public Dictionary<string, List<ColumnInfo>> Columns { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<ForeignKeyInfo>> ForeignKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Indexes { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Triggers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<Dictionary<string, object?>>> Rows { get; } = new(StringComparer.Ordinal);
    }

    public static async Task<Snapshot> TakeSnapshotAsync(DbConnection connection)
    {
        var snapshot = new Snapshot();
        var master = await RowsAsync(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';");
        foreach (var entry in master)
        {
            var type = (string)entry["type"]!;
            var name = (string)entry["name"]!;
            switch (type)
            {
                case "index":
                    snapshot.Indexes.Add(name);
                    break;
                case "trigger":
                    snapshot.Triggers.Add(name);
                    break;
                case "table" when name != "schema_migrations":
                    var columns = new List<ColumnInfo>();
                    foreach (var column in await RowsAsync(connection, $"PRAGMA table_xinfo(\"{name}\");"))
                    {
                        if (Convert.ToInt64(column["hidden"], CultureInfo.InvariantCulture) != 0)
                        {
                            continue;
                        }

                        columns.Add(new ColumnInfo(
                            (string)column["name"]!,
                            (string?)column["type"] ?? string.Empty,
                            Convert.ToInt64(column["notnull"], CultureInfo.InvariantCulture) != 0,
                            column["dflt_value"] as string,
                            Convert.ToInt64(column["pk"], CultureInfo.InvariantCulture) > 0));
                    }

                    snapshot.Columns[name] = columns;
                    snapshot.ForeignKeys[name] = (await RowsAsync(connection, $"PRAGMA foreign_key_list(\"{name}\");"))
                        .Where(fk => fk["to"] is not null)
                        .Select(fk => new ForeignKeyInfo((string)fk["from"]!, (string)fk["table"]!, (string)fk["to"]!))
                        .ToList();
                    break;
            }
        }

        foreach (var (table, columns) in snapshot.Columns)
        {
            var list = string.Join(", ", columns.Select(column => $"\"{column.Name}\""));
            snapshot.Rows[table] = await RowsAsync(connection, $"SELECT {list} FROM \"{table}\" ORDER BY rowid;");
        }

        return snapshot;
    }

    // ---- seeding ------------------------------------------------------------------------

    /// <summary>Values the generator cannot guess because a CHECK constraint or a trigger insists on them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PinnedByCheck = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, object> Overrides = new(StringComparer.Ordinal)
    {
        ["guide_update_check_state.id"] = 1L
    };

    /// <summary>
    /// Fills every table with one row. A child row's link column carries the value its parent row
    /// was given, so the links between rows are real and can be checked after the migration.
    /// </summary>
    public static async Task SeedEveryTableAsync(DbConnection connection)
    {
        var snapshot = await TakeSnapshotAsync(connection);
        var seeded = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var entry in await RowsAsync(connection, "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND sql LIKE '%CHECK%';"))
        {
            foreach (System.Text.RegularExpressions.Match check in System.Text.RegularExpressions.Regex.Matches((string)entry["sql"]!, @"CHECK\s*\(\s*(\w+)\s*=\s*(\d+)\s*\)"))
            {
                PinnedByCheck[$"{entry["name"]}.{check.Groups[1].Value}"] = long.Parse(check.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
        try
        {
            foreach (var table in snapshot.Columns.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                await SeedTableAsync(connection, snapshot, table, seeded, []);
            }
        }
        finally
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    private static async Task SeedTableAsync(
        DbConnection connection,
        Snapshot snapshot,
        string table,
        Dictionary<string, Dictionary<string, object>> seeded,
        HashSet<string> inProgress)
    {
        if (seeded.ContainsKey(table) || !inProgress.Add(table))
        {
            return;
        }

        foreach (var parent in snapshot.ForeignKeys[table].Select(fk => fk.ParentTable).Distinct())
        {
            if (snapshot.Columns.ContainsKey(parent))
            {
                await SeedTableAsync(connection, snapshot, parent, seeded, inProgress);
            }
        }

        // A trigger on an earlier table may already have written a row here.
        var existing = await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{table}\";");
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var column in snapshot.Columns[table])
        {
            var link = snapshot.ForeignKeys[table].FirstOrDefault(fk => fk.Column == column.Name);
            if (link is not null && seeded.TryGetValue(link.ParentTable, out var parentValues) && parentValues.TryGetValue(link.ParentColumn, out var parentValue))
            {
                values[column.Name] = parentValue;
                continue;
            }

            values[column.Name] = Overrides.TryGetValue($"{table}.{column.Name}", out var forced) ? forced
                : PinnedByCheck.TryGetValue($"{table}.{column.Name}", out var pinned) ? pinned
                : Generate(table, column);
        }

        if (Convert.ToInt64(existing, CultureInfo.InvariantCulture) == 0)
        {
            await using var command = connection.CreateCommand();
            var names = snapshot.Columns[table].Select(column => column.Name).ToArray();
            command.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => $"@p{i}"))});";
            for (var i = 0; i < names.Length; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{i}";
                parameter.Value = values[names[i]];
                command.Parameters.Add(parameter);
            }

            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"The seed row for '{table}' was refused: {exception.Message}", exception);
            }
        }
        else
        {
            // Take over what the trigger wrote, so children link to the real value.
            var row = (await RowsAsync(connection, $"SELECT * FROM \"{table}\" LIMIT 1;")).Single();
            foreach (var (key, value) in row)
            {
                if (value is not null)
                {
                    values[key] = value;
                }
            }
        }

        seeded[table] = values;
        inProgress.Remove(table);
    }

    private static object Generate(string table, ColumnInfo column) => NeutralValue(table, column.Name, column.Type);

    /// <summary>A value of the right type for a column nobody cares about, recognisable if it shows up somewhere unexpected.</summary>
    public static object NeutralValue(string table, string name, string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT"))
        {
            return 7L;
        }

        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB") || type.Contains("NUM") || type.Contains("DEC"))
        {
            return 2.5d;
        }

        if (type.Contains("BLOB"))
        {
            return new byte[] { 1, 2, 3 };
        }

        if (name.EndsWith("_json", StringComparison.Ordinal) || name.EndsWith("Json", StringComparison.Ordinal))
        {
            return "[]";
        }

        if (name.EndsWith("_utc", StringComparison.Ordinal) || name.EndsWith("_at", StringComparison.Ordinal))
        {
            return "2026-01-02T03:04:05.0000000+00:00";
        }

        return $"{table}.{name}";
    }

    // ---- comparing ---------------------------------------------------------------------

    /// <summary>
    /// What a migration did to rows that were already there: a table that is gone, a row
    /// lost, a value changed. Empty means every row kept its meaning.
    /// </summary>
    public static List<string> LostOrChanged(Snapshot before, Snapshot after, ISet<string> allowed)
    {
        var findings = new List<string>();
        foreach (var (table, rowsBefore) in before.Rows)
        {
            if (!after.Rows.TryGetValue(table, out var rowsAfter))
            {
                if (!allowed.Contains(table))
                {
                    findings.Add($"table {table} no longer exists ({rowsBefore.Count} row(s) went with it)");
                }

                continue;
            }

            if (rowsAfter.Count != rowsBefore.Count && !allowed.Contains(table))
            {
                findings.Add($"table {table} had {rowsBefore.Count} row(s) and now has {rowsAfter.Count}");
                continue;
            }

            for (var i = 0; i < Math.Min(rowsBefore.Count, rowsAfter.Count); i++)
            {
                foreach (var (column, valueBefore) in rowsBefore[i])
                {
                    if (!rowsAfter[i].TryGetValue(column, out var valueAfter))
                    {
                        if (!allowed.Contains(table) && !allowed.Contains($"{table}.{column}"))
                        {
                            findings.Add($"column {table}.{column} no longer exists");
                        }

                        continue;
                    }

                    if (!ValuesEqual(valueBefore, valueAfter) && !allowed.Contains(table) && !allowed.Contains($"{table}.{column}"))
                    {
                        findings.Add($"{table}.{column} was '{Show(valueBefore)}' and is now '{Show(valueAfter)}'");
                    }
                }
            }
        }

        return findings;
    }

    /// <summary>What the migration added: a table, a column, an index, a trigger.</summary>
    public static List<string> SchemaAdded(Snapshot before, Snapshot after)
    {
        var added = new List<string>();
        foreach (var (table, columns) in after.Columns)
        {
            if (!before.Columns.TryGetValue(table, out var previous))
            {
                added.Add($"table {table}");
                continue;
            }

            added.AddRange(columns
                .Where(column => previous.All(old => old.Name != column.Name))
                .Select(column => $"column {table}.{column.Name}"));
        }

        added.AddRange(after.Indexes.Except(before.Indexes).Select(index => $"index {index}"));
        added.AddRange(after.Triggers.Except(before.Triggers).Select(trigger => $"trigger {trigger}"));
        return added;
    }

    /// <summary>Every column added to a table that already held a row must read back as its declared default (or empty).</summary>
    public static async Task<List<string>> AddedColumnsHoldTheirDefaultAsync(DbConnection connection, Snapshot before, Snapshot after, ISet<string> allowed)
    {
        var findings = new List<string>();
        foreach (var (table, columns) in after.Columns)
        {
            if (!before.Columns.TryGetValue(table, out var previous) || before.Rows[table].Count == 0)
            {
                continue;
            }

            foreach (var column in columns.Where(column => previous.All(old => old.Name != column.Name)))
            {
                if (allowed.Contains(table) || allowed.Contains($"{table}.{column.Name}"))
                {
                    continue;
                }

                var expected = column.Default is null ? null : await ScalarAsync(connection, $"SELECT {column.Default};");
                var actual = after.Rows[table][0][column.Name];
                if (!ValuesEqual(expected, actual))
                {
                    findings.Add($"new column {table}.{column.Name} reads '{Show(actual)}' on an existing row; its default is '{Show(expected)}'");
                }
            }
        }

        return findings;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is byte[] a && right is byte[] b)
        {
            return a.AsSpan().SequenceEqual(b);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Convert.ToDouble(left, CultureInfo.InvariantCulture) == Convert.ToDouble(right, CultureInfo.InvariantCulture);
        }

        return Equals(left, right);
    }

    private static bool IsNumber(object value) => value is long or int or double or float or decimal or short or byte;

    private static string Show(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
