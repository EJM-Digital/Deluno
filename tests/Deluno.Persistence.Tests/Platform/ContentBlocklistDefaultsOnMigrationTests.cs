using System.Text.Json;
using Deluno.Contracts;
using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #944: the content blocklist's warning was unconditional before #816 made
/// it configurable and, following #936, off by default -- which silently
/// dropped the warning for every install that already existed.
/// <see cref="V0113ContentBlocklistDefaultsOnForExistingInstalls"/> freezes
/// an existing install back to "on", the same "existing install, never
/// silently flip" shape <see cref="V0073AutomationDefaultsOff"/> and
/// <see cref="V0096SecurityAllowedHostsDefaultsOff"/> use.
/// </summary>
public sealed class ContentBlocklistDefaultsOnMigrationTests
{
    private const string SettingKey = "download-health.content-blocklist.v1";

    [Fact]
    public async Task A_database_with_no_settings_at_all_is_a_new_install_and_gets_no_explicit_write()
    {
        await using var connection = await OpenWithSettingsTableAsync();

        await MigrateAsync(connection);

        Assert.Empty(await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task An_existing_install_gets_the_content_blocklist_explicitly_frozen_on()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("My Deluno", settings["app.instanceName"]);
        var stored = JsonSerializer.Deserialize<ContentBlocklistSettings>(settings[SettingKey]);
        Assert.NotNull(stored);
        Assert.True(stored!.Enabled);
        Assert.True(stored.UseBuiltInPatterns);
        Assert.Equal(ContentBlocklistModes.Blacklist, stored.Mode);
    }

    [Fact]
    public async Task An_existing_install_that_already_saved_the_content_blocklist_keeps_that_choice()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        var saved = JsonSerializer.Serialize(ContentBlocklistSettings.Default with { Enabled = false, Mode = ContentBlocklistModes.Whitelist });
        await InsertSettingAsync(connection, SettingKey, saved);

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        var stored = JsonSerializer.Deserialize<ContentBlocklistSettings>(settings[SettingKey]);
        Assert.NotNull(stored);
        Assert.False(stored!.Enabled);
        Assert.Equal(ContentBlocklistModes.Whitelist, stored.Mode);
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
        await new V0113ContentBlocklistDefaultsOnForExistingInstalls().UpAsync(connection, transaction, CancellationToken.None);
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
