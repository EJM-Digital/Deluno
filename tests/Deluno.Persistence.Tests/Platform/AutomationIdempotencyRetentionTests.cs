using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class AutomationIdempotencyRetentionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T00:00:00Z");

    [Fact]
    public async Task The_regular_clean_up_keeps_a_key_6_days_old_and_removes_one_8_days_old_and_a_repeat_inside_the_window_gets_the_stored_answer()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now.AddDays(-8));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteAutomationIdempotencyStore(storage.Factory, clock);
        await store.SaveAsync("old-key", "catalogue.bulk-add", "hash-a", "{\"total\":1}", CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(2));
        await store.SaveAsync("recent-key", "catalogue.bulk-add", "hash-b", "{\"total\":2}", CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(Now, clock.GetUtcNow());

        var service = new DispatchCleanupService(
            new SqliteDownloadDispatchesRepository(storage.Factory, clock),
            clock,
            NullLogger<DispatchCleanupService>.Instance,
            automationIdempotencyStore: store);
        await service.RunCleanupPassAsync(CancellationToken.None);

        Assert.False((await store.GetAsync("old-key", "catalogue.bulk-add", "hash-a", CancellationToken.None)).Found);
        var repeat = await store.GetAsync("recent-key", "catalogue.bulk-add", "hash-b", CancellationToken.None);
        Assert.True(repeat.Found);
        Assert.True(repeat.HashMatches);
        Assert.Equal("{\"total\":2}", repeat.ResponseJson);
    }
}
