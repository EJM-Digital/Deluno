using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class LibraryRoutingPersistenceTests
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

    private static Task<LibraryItem> CreateMovieLibraryAsync(SqlitePlatformSettingsRepository repo)
        => repo.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Test Movies",
                MediaType: "movies",
                Purpose: "General",
                RootPath: @"C:\Media\Movies",
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "auto",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: false,
                SearchIntervalHours: null,
                RetryDelayHours: null,
                MaxItemsPerRun: null),
            CancellationToken.None);

    private static Task<IndexerItem> CreateIndexerAsync(SqlitePlatformSettingsRepository repo)
        => repo.CreateIndexerAsync(
            new CreateIndexerRequest(
                Name: "Prowlarr",
                Protocol: "torznab",
                Privacy: "private",
                BaseUrl: "https://prowlarr.example.test",
                ApiKey: null,
                Priority: 1,
                Categories: "2000",
                Tags: "",
                MediaScope: "both",
                IsEnabled: true),
            CancellationToken.None);

    private static Task<DownloadClientItem> CreateDownloadClientAsync(SqlitePlatformSettingsRepository repo)
        => repo.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                Name: "qBittorrent",
                Protocol: "qbittorrent",
                Host: "localhost",
                Port: 8080,
                Username: null,
                Password: null,
                EndpointUrl: null,
                MoviesCategory: "movies",
                TvCategory: "tv",
                CategoryTemplate: null,
                Priority: 1,
                IsEnabled: true),
            CancellationToken.None);

    // ── GetLibraryRoutingAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetLibraryRoutingAsync_returns_null_for_unknown_library_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var result = await repo.GetLibraryRoutingAsync("nonexistent-id", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLibraryRoutingAsync_returns_empty_routing_for_new_library()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);

        var routing = await repo.GetLibraryRoutingAsync(library.Id, CancellationToken.None);

        Assert.NotNull(routing);
        Assert.Equal(library.Id, routing.LibraryId);
        Assert.Equal(library.Name, routing.LibraryName);
        Assert.Empty(routing.Sources);
        Assert.Empty(routing.DownloadClients);
    }

    // ── SaveLibraryRoutingAsync — source links ────────────────────────────────

    [Fact]
    public async Task SaveLibraryRoutingAsync_links_indexer_to_library()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);
        var indexer = await CreateIndexerAsync(repo);

        var routing = await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: [new UpdateLibrarySourceLinkRequest(indexer.Id, Priority: 1, null, null)],
                DownloadClients: null),
            CancellationToken.None);

        Assert.NotNull(routing);
        var link = Assert.Single(routing.Sources);
        Assert.Equal(indexer.Id, link.IndexerId);
        Assert.Equal(1, link.Priority);
        Assert.Empty(routing.DownloadClients);
    }

    [Fact]
    public async Task SaveLibraryRoutingAsync_links_download_client_to_library()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);
        var client = await CreateDownloadClientAsync(repo);

        var routing = await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: null,
                DownloadClients: [new UpdateLibraryDownloadClientLinkRequest(client.Id, Priority: 1)]),
            CancellationToken.None);

        Assert.NotNull(routing);
        Assert.Empty(routing.Sources);
        var link = Assert.Single(routing.DownloadClients);
        Assert.Equal(client.Id, link.DownloadClientId);
        Assert.Equal(1, link.Priority);
    }

    [Fact]
    public async Task SaveLibraryRoutingAsync_links_both_indexer_and_download_client()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);
        var indexer = await CreateIndexerAsync(repo);
        var client = await CreateDownloadClientAsync(repo);

        var routing = await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: [new UpdateLibrarySourceLinkRequest(indexer.Id, Priority: 5, null, null)],
                DownloadClients: [new UpdateLibraryDownloadClientLinkRequest(client.Id, Priority: 2)]),
            CancellationToken.None);

        Assert.NotNull(routing);
        Assert.Single(routing.Sources);
        Assert.Single(routing.DownloadClients);
    }

    [Fact]
    public async Task SaveLibraryRoutingAsync_replaces_existing_links_on_each_save()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);
        var indexer1 = await CreateIndexerAsync(repo);
        var indexer2 = await repo.CreateIndexerAsync(
            new CreateIndexerRequest("Second", "torznab", "public",
                "https://second.example.test", null, 2, "2000", "", "both", true),
            CancellationToken.None);

        // First save: link indexer1
        await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: [new UpdateLibrarySourceLinkRequest(indexer1.Id, 1, null, null)],
                DownloadClients: null),
            CancellationToken.None);

        // Second save: replace with indexer2
        var routing = await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: [new UpdateLibrarySourceLinkRequest(indexer2.Id, 1, null, null)],
                DownloadClients: null),
            CancellationToken.None);

        Assert.NotNull(routing);
        var link = Assert.Single(routing.Sources);
        Assert.Equal(indexer2.Id, link.IndexerId);
    }

    [Fact]
    public async Task SaveLibraryRoutingAsync_clears_all_links_when_empty_lists_provided()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);
        var indexer = await CreateIndexerAsync(repo);
        var client = await CreateDownloadClientAsync(repo);

        // Link both first
        await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: [new UpdateLibrarySourceLinkRequest(indexer.Id, 1, null, null)],
                DownloadClients: [new UpdateLibraryDownloadClientLinkRequest(client.Id, 1)]),
            CancellationToken.None);

        // Clear with empty lists
        var routing = await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(Sources: [], DownloadClients: []),
            CancellationToken.None);

        Assert.NotNull(routing);
        Assert.Empty(routing.Sources);
        Assert.Empty(routing.DownloadClients);
    }

    [Fact]
    public async Task SaveLibraryRoutingAsync_returns_null_for_unknown_library_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var result = await repo.SaveLibraryRoutingAsync(
            "nonexistent-id",
            new UpdateLibraryRoutingRequest(Sources: [], DownloadClients: []),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveLibraryRoutingAsync_routing_is_persisted_and_readable_via_GetLibraryRoutingAsync()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var library = await CreateMovieLibraryAsync(repo);
        var indexer = await CreateIndexerAsync(repo);
        var client = await CreateDownloadClientAsync(repo);

        await repo.SaveLibraryRoutingAsync(
            library.Id,
            new UpdateLibraryRoutingRequest(
                Sources: [new UpdateLibrarySourceLinkRequest(indexer.Id, Priority: 3, null, null)],
                DownloadClients: [new UpdateLibraryDownloadClientLinkRequest(client.Id, Priority: 1)]),
            CancellationToken.None);

        // Read back via a separate Get call to confirm it was actually persisted
        var routing = await repo.GetLibraryRoutingAsync(library.Id, CancellationToken.None);

        Assert.NotNull(routing);
        var sourceLink = Assert.Single(routing.Sources);
        Assert.Equal(indexer.Id, sourceLink.IndexerId);
        Assert.Equal(3, sourceLink.Priority);
        var clientLink = Assert.Single(routing.DownloadClients);
        Assert.Equal(client.Id, clientLink.DownloadClientId);
        Assert.Equal(1, clientLink.Priority);
    }
}
