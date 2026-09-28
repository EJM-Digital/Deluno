using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #984: <see cref="V0115LibrarySearchIntervalMinutes"/> gives the library
/// search interval minute granularity. Every existing library must keep
/// exactly the cadence it already had -- an hours value of 6 has to read as
/// 360 minutes after the migration runs, not some other number.
/// </summary>
public sealed class LibrarySearchIntervalMinutesMigrationTests
{
    [Fact]
    public async Task An_existing_librarys_hours_value_is_converted_to_the_same_number_of_minutes()
    {
        await using var connection = await OpenWithLibrariesTableAsync();
        await InsertLibraryAsync(connection, "library-1", searchIntervalHours: 6);

        await MigrateAsync(connection);

        Assert.Equal(360, await ReadMinutesAsync(connection, "library-1"));
    }

    /// <summary>Two libraries, two different hour values -- proves the conversion is per-row.</summary>
    [Fact]
    public async Task Each_librarys_own_hours_value_is_converted_independently()
    {
        await using var connection = await OpenWithLibrariesTableAsync();
        await InsertLibraryAsync(connection, "library-movies", searchIntervalHours: 1);
        await InsertLibraryAsync(connection, "library-tv", searchIntervalHours: 12);

        await MigrateAsync(connection);

        Assert.Equal(60, await ReadMinutesAsync(connection, "library-movies"));
        Assert.Equal(720, await ReadMinutesAsync(connection, "library-tv"));
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
                search_interval_hours INTEGER NOT NULL DEFAULT 6
            );
            """;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task InsertLibraryAsync(SqliteConnection connection, string id, int searchIntervalHours)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO libraries (id, search_interval_hours) VALUES ($id, $hours);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hours", searchIntervalHours);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await new V0115LibrarySearchIntervalMinutes().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<int> ReadMinutesAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT search_interval_minutes FROM libraries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetInt32(0);
    }
}
