using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class UnifiedExclusionPersistenceTests
{
    [Fact]
    public async Task Upsert_list_and_delete_keep_import_lists_and_collections_in_one_active_store()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteUnifiedExclusionRepository(storage.Factory, timeProvider);

        var importList = await repository.UpsertAsync(
            new(
                MediaType: "movie",
                SourceKind: "IMPORT-LIST",
                SourceId: "list-1",
                SourceName: "My Import List",
                Provider: "TMDb",
                EntryKey: "tmdb:123",
                Title: "Example Movie",
                Year: 2026,
                ImdbId: "tt1234567",
                DurationDays: null,
                Reason: "Already reviewed"),
            CancellationToken.None);

        var collection = await repository.UpsertAsync(
            new(
                MediaType: "tv",
                SourceKind: "collection",
                SourceId: "collection-1",
                SourceName: "Example Collection",
                Provider: "tmdb",
                EntryKey: "tmdb:456",
                Title: "Example Series",
                Year: 2025,
                ImdbId: null,
                DurationDays: 7,
                Reason: "Not wanted"),
            CancellationToken.None);

        Assert.NotNull(importList);
        Assert.NotNull(collection);
        Assert.Equal("movies", importList.MediaType);
        Assert.Equal("import-list", importList.SourceKind);
        Assert.Equal("tmdb", importList.Provider);
        Assert.Equal("tv", collection.MediaType);
        Assert.NotNull(collection.ExpiresUtc);

        var all = await repository.ListActiveAsync(null, null, null, CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, item => item.SourceKind == "import-list" && item.SourceId == "list-1");
        Assert.Contains(all, item => item.SourceKind == "collection" && item.SourceId == "collection-1");

        Assert.True(await repository.DeleteByScopeAsync(
            "collection", "collection-1", "tmdb:456", CancellationToken.None));
        Assert.False(await repository.DeleteAsync(collection.Id, CancellationToken.None));
        Assert.Single(await repository.ListActiveAsync(null, null, null, CancellationToken.None));
    }
}
