using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1030: <c>metadata.nfoEnabled</c> is dead -- nothing reads it and no
/// screen ever showed it (The owner decided, sheet row G230, #1025, that Deluno
/// writes no .nfo files). <see cref="V0115RemoveDeadNfoSetting"/> drops any
/// stored row for the key so it does not linger as leftover state on an
/// install that once saved the settings form.
/// </summary>
public sealed class RemoveDeadNfoSettingMigrationTests
{
    [Fact]
    public async Task An_existing_stored_value_is_dropped()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "metadata.nfoEnabled", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.False(settings.ContainsKey("metadata.nfoEnabled"));
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    [Fact]
    public async Task A_database_with_no_stored_value_is_unaffected()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    private static async Task<SqliteConnection> OpenWithSettingsTableAsync()
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

    private static async Task MigrateAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await new V0115RemoveDeadNfoSetting().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<Dictionary<string, string>> ReadSettingsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM system_settings;";
        var settings = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }
}
