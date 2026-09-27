using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #764 gives folder renaming its own switch (<c>media.renameFoldersOnImport</c>),
/// separate from the file-rename switch. Before this, Deluno's destination
/// folder was always computed from the naming template -- there was no way
/// to keep a release's own folder name at all. #936 says a brand-new
/// install reads the new switch's missing key as off, but
/// <see cref="V0084RenameFoldersOnImportDefaultsExisting"/> must keep an
/// install that already existed reading exactly as it did before, by
/// writing an explicit "true" if the install had not already stored a value
/// for it -- the same shape as <see cref="V0073AutomationDefaultsOff"/>.
/// </summary>
public sealed class RenameFoldersOnImportDefaultsExistingMigrationTests
{
    [Fact]
    public async Task A_database_with_no_settings_at_all_is_a_new_install_and_gets_no_explicit_write()
    {
        await using var connection = await OpenWithSettingsTableAsync();

        await MigrateAsync(connection);

        Assert.Empty(await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task A_database_that_already_had_some_other_setting_is_an_existing_install_and_keeps_reading_rename_folders_on_import_on()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["media.renameFoldersOnImport"]);
        // The setting that made this an "existing install" in the first place
        // is untouched.
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_rename_folders_on_import_off_keeps_that_choice()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "media.renameFoldersOnImport", "false");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("false", settings["media.renameFoldersOnImport"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_explicitly_turned_rename_folders_on_import_on_is_unaffected()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "media.renameFoldersOnImport", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["media.renameFoldersOnImport"]);
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
        await new V0084RenameFoldersOnImportDefaultsExisting().UpAsync(connection, transaction, CancellationToken.None);
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
