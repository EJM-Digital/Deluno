using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1194: a fresh install has no enabled strike rule and no acting cleanup switch; an install that already existed
/// keeps exactly what it does today (V0137 writes the old "on when missing" readings out explicitly).
/// </summary>
public sealed class StrikeRulesDefaultOffMigrationTests
{
    private static readonly string[] Keys =
    [
        "download-health.noProgress.enabled",
        "cleanup.blockReleaseAfterThreshold",
        "cleanup.queueReplacementAfterThreshold"
    ];

    [Fact]
    public async Task A_database_with_no_settings_is_a_new_install_and_gets_no_explicit_write()
    {
        await using var connection = await OpenAsync();

        await MigrateAsync(connection);

        Assert.Empty(await ReadAsync(connection));
    }

    [Fact]
    public async Task An_existing_install_is_frozen_at_what_it_did_before()
    {
        await using var connection = await OpenAsync();
        await InsertAsync(connection, "app.instanceName", "My Deluno");

        await MigrateAsync(connection);

        var settings = await ReadAsync(connection);
        foreach (var key in Keys) Assert.Equal("true", settings[key]);
    }

    [Fact]
    public async Task An_existing_install_that_turned_a_switch_off_keeps_it_off()
    {
        await using var connection = await OpenAsync();
        await InsertAsync(connection, "app.instanceName", "My Deluno");
        await InsertAsync(connection, "cleanup.blockReleaseAfterThreshold", "false");

        await MigrateAsync(connection);

        var settings = await ReadAsync(connection);
        Assert.Equal("false", settings["cleanup.blockReleaseAfterThreshold"]);
        Assert.Equal("true", settings["download-health.noProgress.enabled"]);
    }

    [Fact]
    public async Task A_fresh_install_has_nothing_on_and_no_enabled_rule()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-09T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));

        var settings = await settingsRepository.GetAsync(CancellationToken.None);
        var rules = await new SqliteDownloadHealthRepository(storage.Factory, time).GetDownloadHealthRulesAsync(settings, CancellationToken.None);

        Assert.False(settings.CleanupBlockReleaseAfterThreshold);
        Assert.False(settings.CleanupQueueReplacementAfterThreshold);
        Assert.False(settings.CleanupRemoveClientEntryAfterThreshold);
        Assert.False(settings.CleanupPurgePayloadAfterThreshold);
        Assert.False(settings.DownloadHealthNoProgressEnabled);
        Assert.DoesNotContain(rules, rule => rule.Enabled);
        Assert.All(rules, rule => Assert.Equal(DownloadHealthActionPresets.JustTellMe, rule.ActionPreset));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE system_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task InsertAsync(SqliteConnection connection, string key, string value)
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
        await new V0137StrikeRulesDefaultOffForNewInstalls().UpAsync(connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task<Dictionary<string, string>> ReadAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM system_settings;";
        var settings = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) settings[reader.GetString(0)] = reader.GetString(1);
        return settings;
    }
}
