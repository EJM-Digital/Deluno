using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class IndexerPersistenceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<SqlitePlatformSettingsRepository> CreateRepositoryAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
    }

    private static CreateIndexerRequest BaseCreateRequest(string name = "Test Indexer") =>
        new(Name: name,
            Protocol: "torznab",
            Privacy: "private",
            BaseUrl: "https://indexer.example.test",
            ApiKey: "secret-key",
            Priority: 5,
            Categories: "2000, 5000",
            Tags: "movies, tv",
            MediaScope: "movies",
            IsEnabled: true);

    // ── CreateIndexerAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIndexerAsync_persists_indexer_with_correct_field_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateIndexerAsync(BaseCreateRequest(), CancellationToken.None);

        Assert.Equal("Test Indexer", created.Name);
        Assert.Equal("torznab", created.Protocol);
        Assert.Equal("private", created.Privacy);
        Assert.Equal("https://indexer.example.test", created.BaseUrl);
        Assert.Equal(5, created.Priority);
        Assert.Equal("2000, 5000", created.Categories);  // NormalizeCsv joins with ", "
        Assert.Equal("movies, tv", created.Tags);
        Assert.Equal("movies", created.MediaScope);
        Assert.True(created.IsEnabled);
        Assert.Equal("untested", created.HealthStatus);
    }

    [Fact]
    public async Task CreateIndexerAsync_disabled_indexer_starts_with_disabled_health_status()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateIndexerAsync(
            BaseCreateRequest() with { IsEnabled = false },
            CancellationToken.None);

        Assert.Equal("disabled", created.HealthStatus);
        Assert.False(created.IsEnabled);
    }

    [Fact]
    public async Task CreateIndexerAsync_multiple_indexers_are_all_listed()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        await repo.CreateIndexerAsync(BaseCreateRequest("First"), CancellationToken.None);
        await repo.CreateIndexerAsync(BaseCreateRequest("Second"), CancellationToken.None);
        await repo.CreateIndexerAsync(BaseCreateRequest("Third"), CancellationToken.None);

        var list = await repo.ListIndexersAsync(CancellationToken.None);
        Assert.Equal(3, list.Count);
    }

    // ── UpdateIndexerAsync — null/patch semantics ─────────────────────────────

    [Fact]
    public async Task UpdateIndexerAsync_returns_null_for_unknown_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var result = await repo.UpdateIndexerAsync(
            "nonexistent-id",
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateIndexerAsync_null_fields_preserve_all_existing_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(BaseCreateRequest(), CancellationToken.None);

        // Send a completely null patch — nothing should change
        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal(created.Protocol, updated.Protocol);
        Assert.Equal(created.Privacy, updated.Privacy);
        Assert.Equal(created.BaseUrl, updated.BaseUrl);
        Assert.Equal(created.Priority, updated.Priority);
        Assert.Equal(created.Categories, updated.Categories);
        Assert.Equal(created.Tags, updated.Tags);
        Assert.Equal(created.MediaScope, updated.MediaScope);
        Assert.Equal(created.IsEnabled, updated.IsEnabled);
    }

    [Fact]
    public async Task UpdateIndexerAsync_media_scope_is_preserved_when_not_provided()
    {
        // Regression test for the newScope PATCH bug: previously used a broken
        // operator-precedence expression that would always fall back to the existing
        // value even when a new scope was supplied, and vice versa in edge cases.
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(
            BaseCreateRequest() with { MediaScope = "movies" },
            CancellationToken.None);

        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(Name: "Renamed", null, null, null, null, null, null, null, MediaScope: null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("movies", updated.MediaScope);  // must be unchanged
    }

    [Fact]
    public async Task UpdateIndexerAsync_media_scope_is_updated_when_provided()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(
            BaseCreateRequest() with { MediaScope = "movies" },
            CancellationToken.None);

        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, MediaScope: "tv", null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("tv", updated.MediaScope);
    }

    [Fact]
    public async Task UpdateIndexerAsync_categories_and_tags_update_independently()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(
            BaseCreateRequest() with { Categories = "2000", Tags = "movies" },
            CancellationToken.None);

        // Update only categories, leave tags null
        var updatedCats = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, Categories: "2000, 5000", null, null, null),
            CancellationToken.None);

        Assert.NotNull(updatedCats);
        Assert.Equal("2000, 5000", updatedCats.Categories);  // NormalizeCsv joins with ", "
        Assert.Equal("movies", updatedCats.Tags);  // preserved

        // Update only tags, leave categories null
        var updatedTags = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, Tags: "movies, tv", null, null),
            CancellationToken.None);

        Assert.NotNull(updatedTags);
        Assert.Equal("2000, 5000", updatedTags.Categories);  // preserved
        Assert.Equal("movies, tv", updatedTags.Tags);
    }

    [Fact]
    public async Task UpdateIndexerAsync_priority_below_one_keeps_existing_priority()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(
            BaseCreateRequest() with { Priority = 10 },
            CancellationToken.None);

        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, Priority: 0, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(10, updated.Priority);
    }

    // ── UpdateIndexerAsync — enable/disable health reset ──────────────────────

    [Fact]
    public async Task UpdateIndexerAsync_enabling_disabled_indexer_resets_health_to_untested()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(
            BaseCreateRequest() with { IsEnabled = false },
            CancellationToken.None);
        Assert.Equal("disabled", created.HealthStatus);

        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, IsEnabled: true),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated.IsEnabled);
        Assert.Equal("untested", updated.HealthStatus);
    }

    [Fact]
    public async Task UpdateIndexerAsync_disabling_enabled_indexer_keeps_existing_health_status()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(BaseCreateRequest(), CancellationToken.None);
        await repo.UpdateIndexerHealthAsync(created.Id, "healthy", "OK", null, 50, CancellationToken.None);

        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, IsEnabled: false),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.False(updated.IsEnabled);
        // Health status should not be reset when disabling
        Assert.Equal("healthy", updated.HealthStatus);
    }

    // ── DeleteIndexerAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteIndexerAsync_removes_indexer_from_list()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(BaseCreateRequest(), CancellationToken.None);

        var deleted = await repo.DeleteIndexerAsync(created.Id, CancellationToken.None);

        Assert.True(deleted);
        var list = await repo.ListIndexersAsync(CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task DeleteIndexerAsync_returns_false_for_unknown_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var deleted = await repo.DeleteIndexerAsync("nonexistent-id", CancellationToken.None);

        Assert.False(deleted);
    }
}
