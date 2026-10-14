using System.Data.Common;
using System.Globalization;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// One hand-written upgrade: the database as the migration before N left it, the rows a real install would hold,
/// then migration N, then what those rows mean. Columns a test does not name get a neutral value, so a test shows
/// only the columns the migration reads or writes.
/// </summary>
internal sealed class UpgradeCase : IAsyncDisposable
{
    private readonly TestStorage storage;
    private readonly MigratedDatabase database;
    private readonly DbConnection connection;

    private UpgradeCase(TestStorage storage, MigratedDatabase database, DbConnection connection, int version)
    {
        this.storage = storage;
        this.database = database;
        this.connection = connection;
        Version = version;
    }

    public int Version { get; }

    public static async Task<UpgradeCase> BeforeAsync(string databaseName, int version)
    {
        var storage = TestStorage.Create();
        var database = MigrationUpgradeHarness.Database(databaseName);
        await MigrationUpgradeHarness.ApplyThroughAsync(storage, database, MigrationUpgradeHarness.PreviousVersion(database, version));
        var connection = await MigrationUpgradeHarness.OpenAsync(storage, databaseName);
        return new UpgradeCase(storage, database, connection, version);
    }

    /// <summary>Puts one row in a table. Required columns that are not named get a neutral value.</summary>
    public async Task InsertAsync(string table, params (string Column, object? Value)[] values)
    {
        var given = values.ToDictionary(value => value.Column, value => value.Value, StringComparer.Ordinal);
        var columns = await MigrationUpgradeHarness.RowsAsync(connection, $"PRAGMA table_xinfo(\"{table}\");");
        var names = new List<string>();
        var parameters = new List<object?>();
        foreach (var column in columns)
        {
            var name = (string)column["name"]!;
            if (given.Remove(name, out var value))
            {
                names.Add(name);
                parameters.Add(value);
            }
            else if (Convert.ToInt64(column["notnull"], CultureInfo.InvariantCulture) != 0 && column["dflt_value"] is null)
            {
                names.Add(name);
                parameters.Add(MigrationUpgradeHarness.NeutralValue(table, name, (string?)column["type"] ?? string.Empty));
            }
        }

        Assert.True(given.Count == 0, $"{table} has no column {string.Join(", ", given.Keys)} before migration {Version}.");
        await MigrationUpgradeHarness.ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => $"@p{i}"))});";
            for (var i = 0; i < names.Count; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{i}";
                parameter.Value = parameters[i] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await MigrationUpgradeHarness.ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    /// <summary>Runs SQL the test needs between inserting and migrating, such as clearing a trigger-written row.</summary>
    public Task ExecuteAsync(string sql) => MigrationUpgradeHarness.ExecuteAsync(connection, sql);

    /// <summary>Applies the migration under test.</summary>
    public Task MigrateAsync() => MigrationUpgradeHarness.ApplyOnlyAsync(storage, database, Version);

    public Task<List<Dictionary<string, object?>>> RowsAsync(string sql) => MigrationUpgradeHarness.RowsAsync(connection, sql);

    public Task<object?> ScalarAsync(string sql) => MigrationUpgradeHarness.ScalarAsync(connection, sql);

    public async Task<string?> TextAsync(string sql) => Convert.ToString(await ScalarAsync(sql), CultureInfo.InvariantCulture);

    public async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(sql), CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        storage.Dispose();
    }
}
