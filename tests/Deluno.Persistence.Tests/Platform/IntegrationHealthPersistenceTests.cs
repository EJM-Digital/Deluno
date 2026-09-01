using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Platform;

public sealed class IntegrationHealthPersistenceTests
{
    [Fact]
    public async Task Enabled_integrations_start_untested_and_store_real_test_metadata()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));

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

        var failure = IntegrationFailureFactory.FromLegacy(
            "indexer",
            indexer.Id,
            indexer.Name,
            "health-test",
            "timeout",
            "Connection timed out.",
            attempts: 2);
        var result = await repository.UpdateIndexerHealthAsync(
            indexer.Id,
            "unreachable",
            "Connection timed out.",
            "connectivity",
            812,
            CancellationToken.None,
            failure);

        Assert.NotNull(result);
        Assert.Equal("unreachable", result.HealthStatus);
        Assert.Equal("connectivity", result.FailureCategory);
        Assert.Equal(812, result.LatencyMs);
        Assert.Equal(IntegrationFailureKind.Timeout, result.Failure?.Kind);
        Assert.Equal(2, result.Failure?.Attempts);

        var stored = Assert.Single(await repository.ListIndexersAsync(CancellationToken.None));
        Assert.Equal("unreachable", stored.HealthStatus);
        Assert.Equal("Connection timed out.", stored.LastHealthMessage);
        Assert.Equal("connectivity", stored.LastHealthFailureCategory);
        Assert.Equal(812, stored.LastHealthLatencyMs);
        Assert.Equal(IntegrationFailureKind.Timeout, stored.LastHealthFailure?.Kind);
        Assert.Equal("indexer", stored.LastHealthFailure?.ServiceType);
        Assert.Equal("health-test", stored.LastHealthFailure?.Operation);
        Assert.NotNull(stored.LastHealthTestUtc);
    }

    [Fact]
    public async Task Subtitle_provider_health_retains_the_typed_failure()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "podnapisi",
            "Podnapisi",
            new SaveSubtitleProviderRequest(
                ProviderKey: "podnapisi",
                Username: null,
                Secret: null,
                ApiKey: null,
                Priority: 1,
                IsEnabled: true),
            CancellationToken.None);

        var failure = IntegrationFailureFactory.FromLegacy(
            "subtitle",
            "podnapisi",
            "Podnapisi",
            "test",
            "unreachable",
            "The provider host could not be reached.");
        await repository.RecordHealthAsync(
            "podnapisi",
            "failed",
            failure.Message,
            latencyMs: null,
            success: false,
            rateLimitedUntilUtc: null,
            CancellationToken.None,
            failure);

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Equal("failed", stored.HealthStatus);
        Assert.Equal(IntegrationFailureKind.Unavailable, stored.LastHealthFailure!.Kind);
        Assert.Equal("podnapisi", stored.LastHealthFailure.ServiceId);
        Assert.Equal("test", stored.LastHealthFailure.Operation);
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }
}
