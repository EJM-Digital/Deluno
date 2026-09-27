using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #939's Allowed Hosts already reads a missing
/// <c>security.allowedHosts.enabled</c> key as off, so
/// <see cref="V0096SecurityAllowedHostsDefaultsOff"/> changes nothing about
/// what an install answers -- it only makes an existing install's row set
/// say explicitly "this was decided off" instead of leaving it unwritten, the
/// same belt-and-braces shape <see cref="V0073AutomationDefaultsOff"/> uses.
/// A brand-new install (no settings at all yet) gets no row either way,
/// which is exactly what a never-configured install should read.
/// </summary>
public sealed class SecurityAllowedHostsDefaultsOffMigrationTests
{
    [Fact]
    public async Task A_database_with_no_settings_at_all_is_a_new_install_and_gets_no_explicit_write()
    {
        await using var connection = await OpenWithSettingsTableAsync();

        await MigrateAsync(connection);

        Assert.Empty(await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task An_existing_install_gets_allowed_hosts_explicitly_frozen_off()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("false", settings["security.allowedHosts.enabled"]);
        Assert.Equal("My Deluno", settings["app.instanceName"]);
    }

    [Fact]
    public async Task An_existing_install_that_already_turned_allowed_hosts_on_keeps_that_choice()
    {
        await using var connection = await OpenWithSettingsTableAsync();
        await InsertSettingAsync(connection, "app.instanceName", "My Deluno");
        await InsertSettingAsync(connection, "security.allowedHosts.enabled", "true");

        await MigrateAsync(connection);

        var settings = await ReadSettingsAsync(connection);
        Assert.Equal("true", settings["security.allowedHosts.enabled"]);
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
        await new V0096SecurityAllowedHostsDefaultsOff().UpAsync(connection, transaction, CancellationToken.None);
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
