using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #936 gives automatic subtitle fetching its own global switch
/// (<c>subtitles.fetchAutomatically</c>), read as off when the key is
/// missing. <see cref="V0076SubtitleFetchDefaultsExisting"/> is the migration
/// that keeps an install which already existed reading exactly as it did
/// before there was a switch to turn off -- by writing an explicit "true" for
/// that key if the install had not already stored a value for it. Same
/// technique as <see cref="AutomationDefaultsOffMigrationTests"/> uses for
/// renaming on import.
/// </summary>
public sealed class SubtitleFetchDefaultsExistingMigrationTests
{
    [Fact]
    public async Task A_database_with_no_settings_at_all_is_a_new_install_and_gets_no_explicit_write()
    {
        await using var connection = await OpenWithSettingsTableAsync();

        await MigrateAsync(connection);

        Assert.Empty(await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task An_existing_install_with_no_stored_value_keeps_reading_automatic_fetching_on()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["subtitles.fetchAutomatically"]);
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_it_off_keeps_that_choice()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "subtitles.fetchAutomatically", "false");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("false", settings["subtitles.fetchAutomatically"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_it_on_is_unaffected()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "subtitles.fetchAutomatically", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["subtitles.fetchAutomatically"]);
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
        await new V0076SubtitleFetchDefaultsExisting().UpAsync(connection, transaction, CancellationToken.None);
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
