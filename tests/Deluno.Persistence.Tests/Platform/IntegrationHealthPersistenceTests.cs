using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class IntegrationHealthPersistenceTests
{
    [Fact]
    public async Task Enabled_integrations_start_untested_and_store_real_test_metadata()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        var indexer = await repository.CreateIndexerAsync(
            new CreateIndexerRequest(
                Name: "Indexer",
                Protocol: "torznab",
                Privacy: "private",
                BaseUrl: "https://indexer.example.test",
                ApiKey: null,
                Priority: 1,
                Categories: "2000",
                Tags: "movies",
                MediaScope: "both",
                IsEnabled: true),
            CancellationToken.None);
        var disabledClient = await repository.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                Name: "Disabled client",
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
                IsEnabled: false),
            CancellationToken.None);

        Assert.Equal("untested", indexer.HealthStatus);
        Assert.Equal("disabled", disabledClient.HealthStatus);

        var result = await repository.UpdateIndexerHealthAsync(
            indexer.Id,
            "unreachable",
            "Connection timed out.",
            "connectivity",
            812,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("unreachable", result.HealthStatus);
        Assert.Equal("connectivity", result.FailureCategory);
        Assert.Equal(812, result.LatencyMs);

        var stored = Assert.Single(await repository.ListIndexersAsync(CancellationToken.None));
        Assert.Equal("unreachable", stored.HealthStatus);
        Assert.Equal("Connection timed out.", stored.LastHealthMessage);
        Assert.Equal("connectivity", stored.LastHealthFailureCategory);
        Assert.Equal(812, stored.LastHealthLatencyMs);
        Assert.NotNull(stored.LastHealthTestUtc);
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }
}
