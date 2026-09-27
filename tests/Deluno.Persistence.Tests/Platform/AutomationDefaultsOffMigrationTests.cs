using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #936 reverses the "on unless explicitly turned off" reading of
/// <c>media.renameOnImport</c> back to "off unless explicitly turned on".
/// <see cref="V0073AutomationDefaultsOff"/> is the migration that keeps an
/// install that already existed reading exactly as it did before, by writing
/// an explicit "true" for that one key if the install had not already stored
/// a value for it.
///
/// <para><c>cleanup.removeClientEntryAfterThreshold</c> and
/// <c>cleanup.purgePayloadAfterThreshold</c> are deliberately NOT frozen:
/// <c>v1.0.0-rc.22</c> (the last release) already read a missing value for
/// both as OFF — <c>string.Equals(value, "true", ...)</c>, not the "unless
/// false" shape <c>media.renameOnImport</c> uses. #904's "default on" change
/// for those two never shipped in a release, so no existing install's
/// effective behaviour ever depended on a missing key reading as on for
/// either of them. Freezing them to "true" here would switch removal and
/// deletion on for every existing install that never explicitly saved
/// either key — exactly the silent flip this migration exists to prevent.
/// </para>
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
    public async Task A_database_that_already_had_some_other_setting_is_an_existing_install_and_keeps_reading_rename_on_import_on()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["media.renameOnImport"]);
        // The setting that made this an "existing install" in the first place
        // is untouched.
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    /// <summary>
    /// The core of the coordinator's correction: rc.22 already read a missing
    /// cleanup key as off, so an existing install must not get either cleanup
    /// key written at all — writing "true" would switch removal/deletion on
    /// for an install that never asked for it.
    /// </summary>
    [Fact]
    public async Task An_existing_install_never_gets_either_cleanup_key_written_and_so_keeps_reading_them_off()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.False(settings.ContainsKey("cleanup.removeClientEntryAfterThreshold"));
        Assert.False(settings.ContainsKey("cleanup.purgePayloadAfterThreshold"));
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_rename_on_import_off_keeps_that_choice()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "media.renameOnImport", "false");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("false", settings["media.renameOnImport"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_rename_on_import_on_is_unaffected()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "media.renameOnImport", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["media.renameOnImport"]);
    }

    /// <summary>
    /// An existing install that had already explicitly turned a cleanup key
    /// on for itself (through the settings screen, before #936) keeps reading
    /// that stored value — the migration just never manufactures one.
    /// </summary>
    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_a_cleanup_key_on_keeps_that_value()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "cleanup.removeClientEntryAfterThreshold", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["cleanup.removeClientEntryAfterThreshold"]);
        Assert.False(settings.ContainsKey("cleanup.purgePayloadAfterThreshold"));
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
        await new V0073AutomationDefaultsOff().UpAsync(connection, transaction, CancellationToken.None);
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
