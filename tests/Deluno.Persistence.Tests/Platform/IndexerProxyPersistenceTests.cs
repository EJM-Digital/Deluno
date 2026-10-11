using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #748: a dedicated Indexer Proxies page, more than one proxy configurable
/// at once, and each indexer assignable to one of them in place of the old
/// single global Cloudflare-helper address.
/// </summary>
public sealed class IndexerProxyPersistenceTests
{
    private static async Task<SqliteConnectionsRepository> CreateRepositoryAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
    }

    private static CreateIndexerRequest BaseIndexerRequest(string name = "Test Indexer") =>
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

    // ── CreateProxyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProxyAsync_persists_proxy_with_correct_field_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateProxyAsync(
            new CreateProxyRequest("Home SOCKS5", "socks5", "socks5://192.168.1.1:1080", "user", "pass"),
            CancellationToken.None);

        Assert.Equal("SOCKS5 proxy on 192.168.1.1", created.Name);
        Assert.Equal("socks5", created.ProxyType);
        Assert.Equal("socks5://192.168.1.1:1080", created.Address);
        Assert.Equal("user", created.Username);
        Assert.True(created.IsEnabled);
        Assert.True(created.HasPassword);
        Assert.Empty(created.AssignedIndexerNames);
    }

    [Fact]
    public async Task CreateProxyAsync_normalizes_an_unknown_type_to_http()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateProxyAsync(
            new CreateProxyRequest("Odd", "not-a-real-type", "http://proxy.example.test:8080", null, null),
            CancellationToken.None);

        Assert.Equal(ProxyTypes.Http, created.ProxyType);
    }

    [Fact]
    public async Task CreateProxyAsync_multiple_proxies_can_all_be_configured_at_once()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        await repo.CreateProxyAsync(new CreateProxyRequest("First", "http", "http://a.test:8080", null, null), CancellationToken.None);
        await repo.CreateProxyAsync(new CreateProxyRequest("Second", "socks5", "socks5://b.test:1080", null, null), CancellationToken.None);
        await repo.CreateProxyAsync(new CreateProxyRequest("Third", "flaresolverr", "http://c.test:8191", null, null), CancellationToken.None);

        var list = await repo.ListProxiesAsync(CancellationToken.None);
        Assert.Equal(3, list.Count);
    }

    // ── UpdateProxyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateProxyAsync_can_change_fields_and_clear_the_password()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateProxyAsync(
            new CreateProxyRequest("Proxy", "http", "http://a.test:8080", "user", "pass"),
            CancellationToken.None);

        var updated = await repo.UpdateProxyAsync(
            created.Id,
            new UpdateProxyRequest("Renamed", "socks4", "socks4://b.test:1080", null, null, ClearPassword: true, IsEnabled: false),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("SOCKS4 proxy on b.test", updated.Name);
        Assert.Equal("socks4", updated.ProxyType);
        Assert.Equal("socks4://b.test:1080", updated.Address);
        Assert.False(updated.HasPassword);
        Assert.False(updated.IsEnabled);
    }

    [Fact]
    public async Task UpdateProxyAsync_returns_null_for_unknown_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var result = await repo.UpdateProxyAsync(
            "nonexistent-id",
            new UpdateProxyRequest(null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateProxyAsync_null_fields_preserve_existing_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateProxyAsync(
            new CreateProxyRequest("Proxy", "http", "http://a.test:8080", "user", "pass"),
            CancellationToken.None);

        var updated = await repo.UpdateProxyAsync(
            created.Id,
            new UpdateProxyRequest(null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("HTTP proxy on a.test", updated.Name);
        Assert.Equal("http", updated.ProxyType);
        Assert.Equal("http://a.test:8080", updated.Address);
        Assert.True(updated.HasPassword);
    }

    // ── DeleteProxyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProxyAsync_removes_proxy_from_list()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateProxyAsync(
            new CreateProxyRequest("Proxy", "http", "http://a.test:8080", null, null),
            CancellationToken.None);

        var deleted = await repo.DeleteProxyAsync(created.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(await repo.ListProxiesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteProxyAsync_returns_false_for_unknown_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        Assert.False(await repo.DeleteProxyAsync("nonexistent-id", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteProxyAsync_unassigns_indexers_that_were_using_it_without_touching_others()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var proxyA = await repo.CreateProxyAsync(new CreateProxyRequest("A", "http", "http://a.test:8080", null, null), CancellationToken.None);
        var proxyB = await repo.CreateProxyAsync(new CreateProxyRequest("B", "http", "http://b.test:8080", null, null), CancellationToken.None);
        var usesA = await repo.CreateIndexerAsync(BaseIndexerRequest("Uses A") with { ProxyId = proxyA.Id }, CancellationToken.None);
        var usesB = await repo.CreateIndexerAsync(BaseIndexerRequest("Uses B") with { ProxyId = proxyB.Id }, CancellationToken.None);

        await repo.DeleteProxyAsync(proxyA.Id, CancellationToken.None);

        var indexers = await repo.ListIndexersAsync(CancellationToken.None);
        Assert.Null(indexers.Single(i => i.Id == usesA.Id).ProxyId);
        Assert.Equal(proxyB.Id, indexers.Single(i => i.Id == usesB.Id).ProxyId);
    }

    // ── Indexer <-> proxy assignment ─────────────────────────────────────────

    [Fact]
    public async Task CreateIndexerAsync_persists_the_assigned_proxy()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var proxy = await repo.CreateProxyAsync(new CreateProxyRequest("Proxy", "socks5", "socks5://a.test:1080", null, null), CancellationToken.None);

        var created = await repo.CreateIndexerAsync(BaseIndexerRequest() with { ProxyId = proxy.Id }, CancellationToken.None);

        Assert.Equal(proxy.Id, created.ProxyId);
        var persisted = Assert.Single(await repo.ListIndexersAsync(CancellationToken.None));
        Assert.Equal(proxy.Id, persisted.ProxyId);
    }

    [Fact]
    public async Task CreateIndexerAsync_leaves_proxy_null_when_not_given()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateIndexerAsync(BaseIndexerRequest(), CancellationToken.None);

        Assert.Null(created.ProxyId);
    }

    [Fact]
    public async Task UpdateIndexerAsync_can_assign_and_clear_the_proxy()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var proxy = await repo.CreateProxyAsync(new CreateProxyRequest("Proxy", "http", "http://a.test:8080", null, null), CancellationToken.None);
        var created = await repo.CreateIndexerAsync(BaseIndexerRequest(), CancellationToken.None);

        var assigned = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null, ProxyId: proxy.Id),
            CancellationToken.None);
        Assert.NotNull(assigned);
        Assert.Equal(proxy.Id, assigned.ProxyId);

        var cleared = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null, ClearProxy: true),
            CancellationToken.None);
        Assert.NotNull(cleared);
        Assert.Null(cleared.ProxyId);
    }

    [Fact]
    public async Task ListProxiesAsync_reports_which_indexers_use_each_proxy()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var proxy = await repo.CreateProxyAsync(new CreateProxyRequest("Proxy", "http", "http://a.test:8080", null, null), CancellationToken.None);
        await repo.CreateIndexerAsync(BaseIndexerRequest("Uses proxy") with { ProxyId = proxy.Id }, CancellationToken.None);
        await repo.CreateIndexerAsync(BaseIndexerRequest("Direct"), CancellationToken.None);

        var list = await repo.ListProxiesAsync(CancellationToken.None);

        var only = Assert.Single(list);
        Assert.Equal(["Uses proxy"], only.AssignedIndexerNames);
    }
}
