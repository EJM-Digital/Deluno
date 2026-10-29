using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The connection writes in the cases the other route tests do not walk (coverage register, area connections, low rows): the live-update
/// push each indexer and download-client write sends, and what a protocol or proxy kind Deluno cannot use is told.
/// </summary>
public sealed class ConnectionWritesAndRefusalsLowRowsTests
{
    private static object Client(string name, string protocol = "qbittorrent")
        => new { name, protocol, host = "192.168.1.20", port = 8080, username = "deluno", password = "a-test-password", moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = false };

    [Fact]
    public async Task Every_indexer_and_download_client_write_pushes_its_own_change_event_naming_the_one_that_changed()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(publisher)));
        string[] Pushed(string type) => publisher.Published.Where(entry => entry.Type == type).Select(entry => entry.Id).ToArray();

        var indexer = await app.Client.PostAsJsonAsync("/api/indexers", new { name = "A feed", protocol = "torznab", privacy = "public", baseUrl = "http://indexer.invalid:9117", apiKey = "k", priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = false });
        Assert.True(indexer.IsSuccessStatusCode, await indexer.Content.ReadAsStringAsync());
        var indexerId = (await indexer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal([indexerId], Pushed("Indexer"));

        Assert.True((await app.Client.PutAsJsonAsync($"/api/indexers/{indexerId}", new { priority = 30 })).IsSuccessStatusCode);
        Assert.Equal([indexerId, indexerId], Pushed("Indexer"));
        Assert.True((await app.Client.PostAsync($"/api/indexers/{indexerId}/reset-circuit", null)).IsSuccessStatusCode);
        Assert.Equal([indexerId, indexerId, indexerId], Pushed("Indexer"));
        Assert.True((await app.Client.DeleteAsync($"/api/indexers/{indexerId}")).IsSuccessStatusCode);
        Assert.Equal(4, Pushed("Indexer").Length);
        Assert.All(Pushed("Indexer"), id => Assert.Equal(indexerId, id));

        var client = await app.Client.PostAsJsonAsync("/api/download-clients", Client("A client"));
        Assert.True(client.IsSuccessStatusCode, await client.Content.ReadAsStringAsync());
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal([clientId], Pushed("DownloadClient"));
        Assert.True((await app.Client.PutAsJsonAsync($"/api/download-clients/{clientId}", new { priority = 20 })).IsSuccessStatusCode);
        Assert.True((await app.Client.DeleteAsync($"/api/download-clients/{clientId}")).IsSuccessStatusCode);
        Assert.Equal([clientId, clientId, clientId], Pushed("DownloadClient"));
    }

    [Fact]
    public async Task A_download_client_protocol_Deluno_cannot_send_to_is_refused_on_create_and_on_update_and_a_proxy_kind_outside_the_four_is_refused_with_the_choices()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsJsonAsync("/api/download-clients", Client("Carrier", "carrier-pigeon"));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var createdProblem = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("protocol")[0].GetString()!;
        Assert.StartsWith("Choose a supported download client protocol", createdProblem, StringComparison.Ordinal);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/download-clients")).Replace(" ", ""));

        var good = await app.Client.PostAsJsonAsync("/api/download-clients", Client("Real client"));
        var id = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var updated = await app.Client.PutAsJsonAsync($"/api/download-clients/{id}", new { protocol = "carrier-pigeon" });
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.StartsWith("Choose a supported download client protocol", (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("protocol")[0].GetString()!, StringComparison.Ordinal);
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/download-clients")).EnumerateArray().Single();
        Assert.Equal("qbittorrent", stored.GetProperty("protocol").GetString());

        var proxy = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "carrier-pigeon", address = "http://192.168.1.9:3128" });
        Assert.Equal(HttpStatusCode.BadRequest, proxy.StatusCode);
        Assert.Equal("Choose HTTP, SOCKS4, SOCKS5 or FlareSolverr/Byparr.", (await proxy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("proxyType")[0].GetString());
    }
}
