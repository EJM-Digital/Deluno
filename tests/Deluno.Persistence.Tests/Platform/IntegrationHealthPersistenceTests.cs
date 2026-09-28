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

    /// <summary>
    /// #896: a wrong login or a used-up quota — both classify as
    /// <see cref="IntegrationFailureKind.Authentication"/> — actually pauses
    /// the provider now, instead of only recording the failure and letting
    /// the next cycle ask it again anyway.
    /// </summary>
    [Fact]
    public async Task An_authentication_failure_pauses_the_provider_with_a_reason()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: "wrong", ApiKey: null, Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var failure = IntegrationFailureFactory.FromLegacy(
            "subtitle", "opensubtitles", "OpenSubtitles", "search", "authentication",
            "OpenSubtitles rejected the request (401).");
        await repository.RecordHealthAsync(
            "opensubtitles", "failed", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: null, CancellationToken.None, failure);

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.False(stored.IsEnabled);
        Assert.Equal(failure.Message, stored.DisabledReason);
        // Not askable is the point: a paused provider is never asked again
        // until a person turns it back on.
        Assert.False(stored.IsAskable(timeProvider.GetUtcNow()));
    }

    [Fact]
    public async Task A_non_authentication_failure_never_pauses_the_provider()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "podnapisi",
            "Podnapisi",
            new SaveSubtitleProviderRequest(
                ProviderKey: "podnapisi", Username: null, Secret: null, ApiKey: null, Priority: 1, IsEnabled: true),
            CancellationToken.None);

        // A local setup problem (#880) or an internet outage (#901): neither
        // is a bad release, and neither is a reason to stop asking a source
        // that may simply be down for a minute.
        var failure = IntegrationFailureFactory.FromLegacy(
            "subtitle", "podnapisi", "Podnapisi", "search", "unreachable", "The provider host could not be reached.");
        await repository.RecordHealthAsync(
            "podnapisi", "failed", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: null, CancellationToken.None, failure);

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.True(stored.IsEnabled);
        Assert.Null(stored.DisabledReason);
    }

    [Fact]
    public async Task A_success_clears_a_stale_disabled_reason()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: "fixed-now", ApiKey: null, Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var failure = IntegrationFailureFactory.FromLegacy(
            "subtitle", "opensubtitles", "OpenSubtitles", "search", "authentication", "Wrong credentials.");
        await repository.RecordHealthAsync(
            "opensubtitles", "failed", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: null, CancellationToken.None, failure);

        // A person noticed, fixed the password and turned it back on; the
        // next successful search should not still say "wrong credentials".
        await repository.RecordHealthAsync(
            "opensubtitles", "healthy", "Found a subtitle.", latencyMs: 120, success: true,
            rateLimitedUntilUtc: null, CancellationToken.None);

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Null(stored.DisabledReason);
    }

    /// <summary>
    /// #946: a used-up quota classifies as
    /// <see cref="IntegrationFailureKind.QuotaExceeded"/>, not
    /// <see cref="IntegrationFailureKind.Authentication"/> — both pause the
    /// provider with a reason, but only the quota one carries a reset time.
    /// </summary>
    [Fact]
    public async Task A_quota_failure_pauses_the_provider_with_a_reset_time()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: "fine", ApiKey: "key", Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var resetUtc = DateTimeOffset.Parse("2026-04-30T00:00:00Z");
        var failure = IntegrationFailureFactory.QuotaExceeded(
            "subtitle", "opensubtitles", "OpenSubtitles", "download", resetUtc,
            "OpenSubtitles's daily quota is used up.");
        await repository.RecordHealthAsync(
            "opensubtitles", "degraded", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: resetUtc, CancellationToken.None, failure);

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.False(stored.IsEnabled);
        Assert.Equal(failure.Message, stored.DisabledReason);
        Assert.Equal(resetUtc, stored.RateLimitedUntilUtc);
        Assert.False(stored.IsAskable(timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// The other half of #946: once the reset time passes, the provider is
    /// asked again on its own — nobody has to open Subtitle providers and
    /// flip the switch back on.
    /// </summary>
    [Fact]
    public async Task A_quota_paused_provider_reactivates_on_its_own_once_the_reset_time_passes()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: "fine", ApiKey: "key", Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var resetUtc = timeProvider.GetUtcNow().AddHours(1);
        var failure = IntegrationFailureFactory.QuotaExceeded(
            "subtitle", "opensubtitles", "OpenSubtitles", "download", resetUtc, "Quota used up.");
        await repository.RecordHealthAsync(
            "opensubtitles", "degraded", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: resetUtc, CancellationToken.None, failure);

        Assert.False(Assert.Single(await repository.ListAsync(CancellationToken.None)).IsEnabled);

        timeProvider.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)));

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.True(stored.IsEnabled);
        Assert.Null(stored.DisabledReason);
        Assert.Null(stored.RateLimitedUntilUtc);
        Assert.True(stored.IsAskable(timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// A wrong login (#896) never sets a reset time, so it never reactivates
    /// on its own — the opposite of #946's quota pause. It stays off until a
    /// person fixes the credential and saves the provider again.
    /// </summary>
    [Fact]
    public async Task An_authentication_pause_never_reactivates_on_its_own()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: "wrong", ApiKey: null, Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var failure = IntegrationFailureFactory.FromLegacy(
            "subtitle", "opensubtitles", "OpenSubtitles", "search", "authentication",
            "OpenSubtitles rejected the request (401).");
        await repository.RecordHealthAsync(
            "opensubtitles", "failed", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: null, CancellationToken.None, failure);

        timeProvider.Advance(TimeSpan.FromDays(30));

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.False(stored.IsEnabled);
        Assert.Equal(failure.Message, stored.DisabledReason);
    }

    /// <summary>
    /// A person turning a provider off by hand is a fresh, deliberate choice.
    /// A reset time left over from an earlier quota pause must not later flip
    /// it back on behind them.
    /// </summary>
    [Fact]
    public async Task Manually_disabling_a_provider_clears_a_leftover_quota_reset_time()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = new SqliteSubtitleProviderRepository(
            storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: "fine", ApiKey: "key", Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var resetUtc = timeProvider.GetUtcNow().AddMinutes(5);
        var failure = IntegrationFailureFactory.QuotaExceeded(
            "subtitle", "opensubtitles", "OpenSubtitles", "download", resetUtc, "Quota used up.");
        await repository.RecordHealthAsync(
            "opensubtitles", "degraded", failure.Message, latencyMs: null, success: false,
            rateLimitedUntilUtc: resetUtc, CancellationToken.None, failure);

        // A person disables it themselves before the quota would have reset.
        await repository.SaveAsync(
            "opensubtitles",
            "OpenSubtitles",
            new SaveSubtitleProviderRequest(
                ProviderKey: "opensubtitles", Username: "someone", Secret: null, ApiKey: null, Priority: 1, IsEnabled: false),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromHours(1));

        var stored = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.False(stored.IsEnabled);
        Assert.Null(stored.RateLimitedUntilUtc);
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }
}
