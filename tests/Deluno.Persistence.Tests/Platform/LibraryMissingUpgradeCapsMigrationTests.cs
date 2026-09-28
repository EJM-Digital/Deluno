using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #830: <see cref="V0097LibraryMissingUpgradeCaps"/> splits a library's
/// single <c>max_items_per_run</c> cap into a missing-item cap and an
/// upgrade-item cap that can be tuned independently. Every existing library
/// must keep its current value applied to both new caps, so nothing changes
/// for it until it is re-tuned.
/// </summary>
public sealed class LibraryMissingUpgradeCapsMigrationTests
{
    [Fact]
    public async Task An_existing_librarys_cap_lands_in_both_new_columns()
    {
        await using var connection = await OpenWithLibrariesTableAsync();
        await InsertLibraryAsync(connection, "library-1", maxItemsPerRun: 40);

        await MigrateAsync(connection);

        var (missing, upgrade) = await ReadCapsAsync(connection, "library-1");
        Assert.Equal(40, missing);
        Assert.Equal(40, upgrade);
    }

    /// <summary>Two libraries, two different caps -- proves the copy is per-row, not a single constant.</summary>
    [Fact]
    public async Task Each_librarys_own_cap_is_copied_independently()
    {
        await using var connection = await OpenWithLibrariesTableAsync();
        await InsertLibraryAsync(connection, "library-movies", maxItemsPerRun: 10);
        await InsertLibraryAsync(connection, "library-tv", maxItemsPerRun: 99);

        await MigrateAsync(connection);

        var movies = await ReadCapsAsync(connection, "library-movies");
        var tv = await ReadCapsAsync(connection, "library-tv");
        Assert.Equal((10, 10), movies);
        Assert.Equal((99, 99), tv);
    }

    private static async Task<SqliteConnection> OpenWithLibrariesTableAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE libraries (
                id TEXT PRIMARY KEY,
                max_items_per_run INTEGER NOT NULL DEFAULT 25
            );
            """;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task InsertLibraryAsync(SqliteConnection connection, string id, int maxItemsPerRun)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO libraries (id, max_items_per_run) VALUES ($id, $cap);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$cap", maxItemsPerRun);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await new V0097LibraryMissingUpgradeCaps().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<(int Missing, int Upgrade)> ReadCapsAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT max_missing_items_per_run, max_upgrade_items_per_run FROM libraries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
