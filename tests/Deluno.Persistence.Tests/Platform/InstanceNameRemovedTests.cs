using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1075: there is no Instance name setting. The name Deluno shows and sends
/// is always the machine's hostname.
/// </summary>
public sealed class InstanceNameRemovedTests
{
    [Fact]
    public void The_name_is_the_machines_hostname()
    {
        Assert.Equal(Environment.MachineName, DelunoHostName.Current);
    }

    [Fact]
    public async Task The_settings_contract_no_longer_carries_an_instance_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.False(settings.TryGetProperty("appInstanceName", out _));
    }

    [Fact]
    public async Task An_api_client_that_still_sends_the_old_field_is_ignored_not_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var patch = await app.Client.PatchAsJsonAsync("/api/settings", new { appInstanceName = "Renamed" });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(settings.TryGetProperty("appInstanceName", out _));
    }

    [Fact]
    public async Task The_system_status_reports_the_hostname()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/status");

        Assert.Equal(Environment.MachineName, status.GetProperty("instanceName").GetString());
    }

    [Fact]
    public async Task The_upgrade_removes_the_stored_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE system_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);" +
                "INSERT INTO system_settings VALUES ('app.instanceName', 'My Deluno', '2026-01-01T00:00:00Z');" +
                "INSERT INTO system_settings VALUES ('ui.theme', 'dark', '2026-01-01T00:00:00Z');";
            await create.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0131RemoveInstanceNameSetting().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT setting_key FROM system_settings;";
        var keys = new List<string>();
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }

        Assert.Equal(["ui.theme"], keys);
    }
}
