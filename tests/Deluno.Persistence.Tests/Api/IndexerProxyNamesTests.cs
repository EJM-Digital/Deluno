using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1076: an indexer proxy is named from its kind and host ("FlareSolverr on RIG"),
/// never typed, and indexers keep pointing at it by id.
/// </summary>
public sealed class IndexerProxyNamesTests
{
    private static readonly string Machine = DelunoHostName.Current;

    [Theory]
    [InlineData("flaresolverr", "http://localhost:8191", "FlareSolverr on {machine}")]
    [InlineData("byparr", "http://192.168.1.9:8191/", "FlareSolverr on 192.168.1.9")]
    [InlineData("socks5", "socks5://192.168.1.9:1080", "SOCKS5 proxy on 192.168.1.9")]
    [InlineData("socks4", "socks4://127.0.0.1:1080", "SOCKS4 proxy on {machine}")]
    [InlineData("http", "http://proxy.example.test:8080", "HTTP proxy on proxy.example.test")]
    [InlineData("http", "proxy.example.test:3128", "HTTP proxy on proxy.example.test")]
    public void Each_proxy_kind_is_named_from_its_kind_and_host(string kind, string address, string expected)
    {
        Assert.Equal(expected.Replace("{machine}", Machine), ProxyTypes.AutomaticName(kind, address).Name);
    }

    [Fact]
    public async Task The_api_ignores_a_typed_name_and_names_two_proxies_on_one_host_apart()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await CreateAsync(app, "My typed name", "flaresolverr", "http://192.168.1.9:8191");
        var second = await CreateAsync(app, "Another typed name", "flaresolverr", "http://192.168.1.9:8192");
        var nameless = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "socks5", address = "socks5://192.168.1.9:1080" });

        Assert.Equal("FlareSolverr on 192.168.1.9", first);
        Assert.Equal("FlareSolverr on 192.168.1.9:8192", second);
        Assert.True(nameless.IsSuccessStatusCode);
        Assert.Equal("SOCKS5 proxy on 192.168.1.9", (await nameless.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        var id = (await app.Services.GetRequiredService<IConnectionsRepository>().ListProxiesAsync(CancellationToken.None)).First(p => p.Name == first).Id;
        var renamed = await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{id}", new { name = "Sneaky", address = "http://192.168.1.20:8191" });
        Assert.Equal("FlareSolverr on 192.168.1.20", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_upgrade_renames_existing_proxies_and_indexers_still_point_at_them_by_id()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var flare = await repository.CreateProxyAsync(new CreateProxyRequest(null, "flaresolverr", "http://localhost:8191", null, null), CancellationToken.None);
        var socks = await repository.CreateProxyAsync(new CreateProxyRequest(null, "socks5", "socks5://192.168.1.9:1080", null, null), CancellationToken.None);
        var indexer = await repository.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture", "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", IsEnabled: true, ProxyId: flare.Id),
            CancellationToken.None);

        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE indexer_proxies SET name = 'Old typed ' || id;";
            await stale.ExecuteNonQueryAsync();

            await using var transaction = await connection.BeginTransactionAsync();
            await new V0135IndexerProxyNamesFollowTheirAddress().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var names = (await repository.ListProxiesAsync(CancellationToken.None)).ToDictionary(p => p.Id, p => p.Name);
        Assert.Equal($"FlareSolverr on {Machine}", names[flare.Id]);
        Assert.Equal("SOCKS5 proxy on 192.168.1.9", names[socks.Id]);
        Assert.Equal(flare.Id, (await repository.ListIndexersAsync(CancellationToken.None)).Single(i => i.Id == indexer.Id).ProxyId);
    }

    private static async Task<string> CreateAsync(ApplicationTestHost app, string typedName, string kind, string address)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { name = typedName, proxyType = kind, address });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
    }
}
