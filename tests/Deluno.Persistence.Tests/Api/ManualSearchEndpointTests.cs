using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #753: the dedicated multi-indexer manual Search endpoint -- a free-text
/// query fanned out to a chosen set of enabled indexers, with no title
/// required, returning the same enriched columns (protocol, age,
/// seeders/leechers) the per-title interactive search results show.
/// </summary>
public sealed class ManualSearchEndpointTests
{
    [Fact]
    public async Task An_empty_query_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "  ", searchType = "movie" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_enabled_indexers_returns_an_empty_result_rather_than_an_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Example", searchType = "movie" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("indexersQueried").GetInt32());
        Assert.Empty(body.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task Fans_out_to_a_real_indexer_and_returns_enriched_columns()
    {
        await using var server = await TorznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", server.BaseUrl, "fixture-key", 10, "2000", "", "movies", true),
            CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Example", searchType = "movie", indexerIds = new[] { indexer.Id } });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        Assert.Equal(1, body.GetProperty("indexersQueried").GetInt32());
        var candidatesArray = body.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.True(candidatesArray.Length == 1, $"Expected 1 candidate, got {candidatesArray.Length}. Raw: {raw}");
        var candidate = candidatesArray[0];
        Assert.Equal("Example.Release.WEB.1080p", candidate.GetProperty("releaseName").GetString());
        Assert.Equal("torznab", candidate.GetProperty("indexerProtocol").GetString());
        Assert.Equal(12, candidate.GetProperty("seeders").GetInt32());
        Assert.Equal(3, candidate.GetProperty("leechers").GetInt32());
    }

    [Fact]
    public async Task Grabbing_without_a_download_url_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = (string?)null,
            downloadClientId = "client-1",
            searchType = "movie"
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Grabbing_without_a_chosen_download_client_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = "https://fixture.invalid/x.torrent",
            downloadClientId = "",
            searchType = "movie"
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class FakeServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<FakeServer> TorznabServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.ContentType = "application/rss+xml";
            await context.Response.WriteAsync(
                """
                <?xml version="1.0"?>
                <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                  <channel>
                    <item>
                      <title>Example.Release.WEB.1080p</title>
                      <link>https://fixture.invalid/x.torrent</link>
                      <torznab:attr name="seeders" value="12" />
                      <torznab:attr name="peers" value="3" />
                    </item>
                  </channel>
                </rss>
                """);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, address);
    }
}
