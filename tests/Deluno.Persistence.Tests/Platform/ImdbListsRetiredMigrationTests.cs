using Deluno.Intake;
using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1292: a saved IMDb import list is switched off with one plain note and kept (never deleted); every other list is untouched.
/// </summary>
public sealed class ImdbListsRetiredMigrationTests
{
    [Fact]
    public async Task A_saved_imdb_list_is_switched_off_with_the_plain_note_and_kept()
    {
        await using var connection = await OpenAsync();
        await InsertAsync(connection, "imdb-1", "Weekend films", "imdb");
        await InsertAsync(connection, "imdb-2", "Shouty case", "IMDb");
        await InsertAsync(connection, "trakt-1", "Trakt list", "trakt");

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0139IntakeImdbListsRetired().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var rows = await ReadAsync(connection);
        Assert.Equal(3, rows.Count);
        foreach (var id in new[] { "imdb-1", "imdb-2" })
        {
            Assert.Equal((0L, "error", IntakeSourceNotes.ImdbNoLongerSupported), rows[id]);
        }

        Assert.Equal((1L, "never", (string?)null), rows["trakt-1"]);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE intake_sources (id TEXT PRIMARY KEY, name TEXT NOT NULL, provider TEXT NOT NULL, is_enabled INTEGER NOT NULL DEFAULT 1,
                last_sync_status TEXT NOT NULL DEFAULT 'never', last_sync_summary TEXT NULL, updated_utc TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task InsertAsync(SqliteConnection connection, string id, string name, string provider)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO intake_sources (id, name, provider, updated_utc) VALUES ($id, $name, $provider, '2026-01-01T00:00:00Z');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$provider", provider);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, (long Enabled, string Status, string? Summary)>> ReadAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, is_enabled, last_sync_status, last_sync_summary FROM intake_sources;";
        var rows = new Dictionary<string, (long, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows[reader.GetString(0)] = (reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        return rows;
    }
}
