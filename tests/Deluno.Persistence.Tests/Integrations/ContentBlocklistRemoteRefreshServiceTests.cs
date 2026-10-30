using System.Net;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #816: a configured custom remote URL is re-fetched and its patterns kept
/// current, the same way Cleanuparr auto-reloads its own remote lists -
/// nothing Deluno did before this pass, since <c>RecordContentBlocklistRemoteRefreshAsync</c>
/// existed on the repository with no caller.
/// </summary>
public sealed class ContentBlocklistRemoteRefreshServiceTests
{
    private static async Task<SqliteDownloadHealthRepository> CreateHealthAsync(TestStorage storage, FixedTimeProvider time)
    {
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteDownloadHealthRepository(storage.Factory, time);
    }

    [Fact]
    public async Task No_remote_url_configured_fetches_nothing()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var health = await CreateHealthAsync(storage, time);
        await health.SaveContentBlocklistSettingsAsync(ContentBlocklistSettings.Default with { Enabled = true }, CancellationToken.None);
        var handler = new NeverCalledHandler();

        await CreateService(health, handler, time).RefreshAsync(CancellationToken.None);

        var after = await health.GetContentBlocklistSettingsAsync(CancellationToken.None);
        Assert.Null(after.LastRefreshedUtc);
    }

    [Fact]
    public async Task A_configured_remote_url_is_fetched_and_its_patterns_recorded()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var health = await CreateHealthAsync(storage, time);
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, RemoteUrl = "https://example.test/blocklist.txt" },
            CancellationToken.None);
        var handler = new FakeResponseHandler("# comment\n*.exe\n*.scr\n\n*.exe\n");

        await CreateService(health, handler, time).RefreshAsync(CancellationToken.None);

        var after = await health.GetContentBlocklistSettingsAsync(CancellationToken.None);
        Assert.Equal(["*.exe", "*.scr"], after.RemotePatterns);
        Assert.Equal(time.GetUtcNow(), after.LastRefreshedUtc);
        // The fetched patterns take effect immediately in EffectivePatterns(),
        // for a caller checking a file against the blocklist right after.
        Assert.Contains("*.scr", after.EffectivePatterns());
    }

    [Fact]
    public async Task A_failed_fetch_keeps_whatever_patterns_the_last_successful_one_left()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var health = await CreateHealthAsync(storage, time);
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, RemoteUrl = "https://example.test/blocklist.txt" },
            CancellationToken.None);
        await health.RecordContentBlocklistRemoteRefreshAsync(["*.exe"], CancellationToken.None);
        var handler = new FailingHandler();

        await CreateService(health, handler, time).RefreshAsync(CancellationToken.None);

        var after = await health.GetContentBlocklistSettingsAsync(CancellationToken.None);
        Assert.Equal(["*.exe"], after.RemotePatterns);
    }

    [Fact]
    public async Task An_address_that_is_not_http_or_https_fetches_nothing_and_keeps_the_patterns_already_held()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var health = await CreateHealthAsync(storage, time);
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, RemoteUrl = "ftp://example.test/blocklist.txt" },
            CancellationToken.None);
        await health.RecordContentBlocklistRemoteRefreshAsync(["*.exe"], CancellationToken.None);

        await CreateService(health, new NeverCalledHandler(), time).RefreshAsync(CancellationToken.None);

        var after = await health.GetContentBlocklistSettingsAsync(CancellationToken.None);
        Assert.Equal(["*.exe"], after.RemotePatterns);
    }

    [Fact]
    public async Task The_running_service_fetches_at_once_and_again_only_after_four_hours()
    {
        using var storage = TestStorage.Create();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var health = await CreateHealthAsync(storage, new FixedTimeProvider(clock.GetUtcNow()));
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, RemoteUrl = "https://example.test/blocklist.txt" },
            CancellationToken.None);
        var handler = new CountingHandler("*.exe\n");
        using var service = CreateService(health, handler, clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await handler.NextFetch());
            Assert.Equal(1, handler.Fetches);

            clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(59));
            // reason: proving that nothing more is fetched needs a short real wait; the clock is fake and cannot be waited on.
            Assert.False(await handler.NextFetch(TimeSpan.FromMilliseconds(300)));
            Assert.Equal(1, handler.Fetches);

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(await handler.NextFetch());
            Assert.Equal(2, handler.Fetches);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ContentBlocklistRemoteRefreshService CreateService(SqliteDownloadHealthRepository health, HttpMessageHandler handler, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDownloadHealthRepository>(health);
        var provider = services.BuildServiceProvider();
        return new ContentBlocklistRemoteRefreshService(
            NullLogger<ContentBlocklistRemoteRefreshService>.Instance,
            new SingleScopeFactory(provider),
            new Factory(handler),
            time);
    }

    private sealed class SingleScopeFactory(IServiceProvider services) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NonDisposingScope(services);

        private sealed class NonDisposingScope(IServiceProvider services) : IServiceScope
        {
            public IServiceProvider ServiceProvider => services;
            public void Dispose() { }
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        private readonly SemaphoreSlim _fetched = new(0);
        private int _fetches;

        public int Fetches => Volatile.Read(ref _fetches);

        public Task<bool> NextFetch(TimeSpan? wait = null) => _fetched.WaitAsync(wait ?? TimeSpan.FromSeconds(30));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);
            _fetched.Release();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated failure");
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No remote URL is configured; this must not make a network call.");
    }
}
