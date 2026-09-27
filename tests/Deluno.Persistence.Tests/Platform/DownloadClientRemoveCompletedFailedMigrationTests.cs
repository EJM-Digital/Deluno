using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #806 replaces the single platform-wide <c>media.removeCompletedDownloads</c>
/// flag with per-client Remove Completed / Remove Failed toggles.
/// <see cref="V0092DownloadClientRemoveCompletedFailed"/> must carry an
/// existing install's old platform-wide value onto every one of its clients
/// so upgrading does not silently stop (or start) removing completed
/// downloads — Remove Failed has no old equivalent, so it always starts off.
/// </summary>
public sealed class DownloadClientRemoveCompletedFailedMigrationTests
{
    [Fact]
    public async Task A_new_install_with_no_old_setting_gets_both_toggles_off()
    {
        await using var connection = await OpenWithTablesAsync();
        await InsertClientAsync(connection, "client-1");

        await MigrateAsync(connection);

        var client = await ReadClientAsync(connection, "client-1");
        Assert.Equal(0, client.RemoveCompleted);
        Assert.Equal(0, client.RemoveFailed);
    }

    [Fact]
    public async Task An_install_that_had_the_old_flag_off_keeps_remove_completed_off()
    {
        await using var connection = await OpenWithTablesAsync();
        await InsertClientAsync(connection, "client-1");
        await InsertSettingAsync(connection, "media.removeCompletedDownloads", "false");

        await MigrateAsync(connection);

        var client = await ReadClientAsync(connection, "client-1");
        Assert.Equal(0, client.RemoveCompleted);
        Assert.Equal(0, client.RemoveFailed);
    }

    [Fact]
    public async Task An_install_that_had_the_old_flag_on_keeps_removing_completed_downloads_on_every_client()
    {
        await using var connection = await OpenWithTablesAsync();
        await InsertClientAsync(connection, "client-1");
        await InsertClientAsync(connection, "client-2");
        await InsertSettingAsync(connection, "media.removeCompletedDownloads", "true");

        await MigrateAsync(connection);

        Assert.Equal(1, (await ReadClientAsync(connection, "client-1")).RemoveCompleted);
        Assert.Equal(1, (await ReadClientAsync(connection, "client-2")).RemoveCompleted);
        // The old flag being on never implied anything about failed downloads.
        Assert.Equal(0, (await ReadClientAsync(connection, "client-1")).RemoveFailed);
    }

    private static async Task<SqliteConnection> OpenWithTablesAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE system_settings (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE download_clients (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task InsertSettingAsync(SqliteConnection connection, string key, string value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ($key, $value, '2026-01-01T00:00:00Z');";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertClientAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO download_clients (id, name) VALUES ($id, $id);";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await new V0092DownloadClientRemoveCompletedFailed().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<(int RemoveCompleted, int RemoveFailed)> ReadClientAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT remove_completed, remove_failed FROM download_clients WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
