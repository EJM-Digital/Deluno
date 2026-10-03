using System.Text.Json;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>#1120: the notification switches survive a restart.</summary>
public sealed class NotificationPreferencesPersistenceTests
{
    private static async Task<(TestStorage Storage, FixedTimeProvider Time)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-03T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        return (storage, time);
    }

    [Fact]
    public async Task A_fresh_database_gives_the_defaults()
    {
        var (storage, time) = await CreateAsync();
        using var _ = storage;

        var prefs = await new SqliteNotificationService(storage.Factory, time).GetPreferencesAsync();

        var defaults = new NotificationPreferences();
        Assert.Equal(JsonSerializer.Serialize(defaults), JsonSerializer.Serialize(prefs));
        Assert.True(prefs.ImportCompletedEnabled);
        Assert.False(prefs.UpgradeEnabled);
    }

    [Fact]
    public async Task A_changed_preference_survives_recreating_the_service()
    {
        var (storage, time) = await CreateAsync();
        using var _ = storage;

        var first = new SqliteNotificationService(storage.Factory, time);
        var prefs = await first.GetPreferencesAsync();
        prefs.ImportCompletedEnabled = false;
        prefs.CleanupClearedEnabled = true;
        await first.UpdatePreferencesAsync(prefs);

        var afterRestart = await new SqliteNotificationService(storage.Factory, time).GetPreferencesAsync();

        Assert.False(afterRestart.ImportCompletedEnabled);
        Assert.True(afterRestart.CleanupClearedEnabled);
        Assert.True(afterRestart.ImportFailedEnabled);
    }

    [Fact]
    public async Task The_json_the_endpoint_sends_and_receives_round_trips()
    {
        var (storage, time) = await CreateAsync();
        using var _ = storage;
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var service = new SqliteNotificationService(storage.Factory, time);
        var put = JsonSerializer.Deserialize<NotificationPreferences>(
            """{"importFailedEnabled":false,"providerHealthChangedEnabled":false,"grabEnabled":true}""", web)!;
        await service.UpdatePreferencesAsync(put);

        var body = JsonSerializer.Serialize(await service.GetPreferencesAsync(), web);
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("importFailedEnabled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("providerHealthChangedEnabled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("grabEnabled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("importCompletedEnabled").GetBoolean());
    }
}
