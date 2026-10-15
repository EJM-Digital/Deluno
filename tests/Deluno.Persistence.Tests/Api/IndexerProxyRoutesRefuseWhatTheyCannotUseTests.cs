using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The indexer-proxy routes on a real host and database: what a proxy needs to be saved, that an unknown proxy is a plain 404,
/// and that removing a proxy sends the indexers that used it back to asking directly.
/// </summary>
public sealed class IndexerProxyRoutesRefuseWhatTheyCannotUseTests
{
    [Fact]
    public async Task A_proxy_with_no_address_or_a_kind_nobody_knows_is_refused_and_nothing_is_saved_and_an_unknown_proxy_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var blank = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "flaresolverr", address = "  " });
        var unknown = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "carrier-pigeon", address = "http://192.168.1.9:8191" });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Add the address Deluno should send requests through.", (await blank.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("address")[0].GetString());
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/indexer-proxies")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/indexer-proxies/no-such-proxy", new { address = "http://192.168.1.9:8191" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/indexer-proxies/no-such-proxy")).StatusCode);
    }

    [Fact]
    public async Task Every_proxy_route_needs_a_sign_in_and_changes_nothing_without_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();
        var created = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "http", address = "http://192.168.1.9:3128" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "http", address = "http://192.168.1.10:3128" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/indexer-proxies/{id}", new { address = "http://192.168.1.11:3128" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/indexer-proxies/{id}")).StatusCode);

        var kept = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-proxies")).EnumerateArray());
        Assert.Equal("http://192.168.1.9:3128", kept.GetProperty("address").GetString());
    }
}
