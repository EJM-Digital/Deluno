using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class AutomationIdempotencyStoreTests
{
    [Fact]
    public async Task Completed_response_is_replayed_and_key_reuse_with_a_new_body_is_rejected()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteAutomationIdempotencyStore(storage.Factory, clock);
        var missing = await store.GetAsync("batch-1", "catalogue.bulk-add", "hash-a", CancellationToken.None);
        Assert.False(missing.Found);

        var saved = await store.SaveAsync(
            "batch-1",
            "catalogue.bulk-add",
            "hash-a",
            "{\"total\":1}",
            CancellationToken.None);
        Assert.True(saved.Found);
        Assert.True(saved.HashMatches);
        Assert.Equal("{\"total\":1}", saved.ResponseJson);

        var replay = await store.GetAsync("batch-1", "catalogue.bulk-add", "hash-a", CancellationToken.None);
        Assert.True(replay.Found);
        Assert.True(replay.HashMatches);
        Assert.Equal(saved.ResponseJson, replay.ResponseJson);

        var losingWriter = await store.SaveAsync(
            "batch-1",
            "catalogue.bulk-add",
            "hash-a",
            "{\"total\":99}",
            CancellationToken.None);
        Assert.Equal(saved.ResponseJson, losingWriter.ResponseJson);

        var changedBody = await store.GetAsync("batch-1", "catalogue.bulk-add", "hash-b", CancellationToken.None);
        Assert.True(changedBody.Found);
        Assert.False(changedBody.HashMatches);

        var changedOperation = await store.GetAsync("batch-1", "series.episodes.bulk-sync", "hash-a", CancellationToken.None);
        Assert.True(changedOperation.Found);
        Assert.False(changedOperation.HashMatches);
    }
}
