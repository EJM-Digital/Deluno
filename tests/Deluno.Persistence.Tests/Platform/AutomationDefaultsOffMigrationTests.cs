using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #936 reverses #904's "on unless explicitly turned off" reading of
/// <c>media.renameOnImport</c>, <c>cleanup.removeClientEntryAfterThreshold</c>
/// and <c>cleanup.purgePayloadAfterThreshold</c> back to "off unless
/// explicitly turned on". <see cref="V0071AutomationDefaultsOff"/> is the
/// migration that keeps an install that already existed reading exactly as
/// it did before, by writing an explicit "true" for any of the three keys
/// that install had not already stored a value for.
/// </summary>
public sealed class AutomationDefaultsOffMigrationTests
{
    [Fact]
    public async Task A_database_with_no_settings_at_all_is_a_new_install_and_gets_no_explicit_write()
    {
        await using var connection = await OpenWithSettingsTableAsync();

        await MigrateAsync(connection);

        Assert.Empty(await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task A_database_that_already_had_some_other_setting_is_an_existing_install_and_keeps_reading_on()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["media.renameOnImport"]);
        Assert.Equal("true", settings["cleanup.removeClientEntryAfterThreshold"]);
        Assert.Equal("true", settings["cleanup.purgePayloadAfterThreshold"]);
        // The setting that made this an "existing install" in the first place
        // is untouched.
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_one_off_keeps_that_choice()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "cleanup.removeClientEntryAfterThreshold", "false");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("false", settings["cleanup.removeClientEntryAfterThreshold"]);
        // The other two keys were never explicitly set on this install, so
        // they are still frozen to "true".
        Assert.Equal("true", settings["media.renameOnImport"]);
        Assert.Equal("true", settings["cleanup.purgePayloadAfterThreshold"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_all_three_on_is_unaffected()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "media.renameOnImport", "true");
        await InsertSettingAsync(connection, "cleanup.removeClientEntryAfterThreshold", "true");
        await InsertSettingAsync(connection, "cleanup.purgePayloadAfterThreshold", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["media.renameOnImport"]);
        Assert.Equal("true", settings["cleanup.removeClientEntryAfterThreshold"]);
        Assert.Equal("true", settings["cleanup.purgePayloadAfterThreshold"]);
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
        await new V0071AutomationDefaultsOff().UpAsync(connection, transaction, CancellationToken.None);
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
