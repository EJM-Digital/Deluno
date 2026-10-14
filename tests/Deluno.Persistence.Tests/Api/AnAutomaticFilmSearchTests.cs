using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Search now" on a film, over the real route and databases, against a real (local) Torznab server and with only the download
/// client's reply stood in for: the best acceptable release is sent once and recorded, a second search while it downloads sends
/// nothing, and a search that finds nothing says so and sends nothing.
/// </summary>
public sealed class AnAutomaticFilmSearchTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP";

    [Fact]
    public async Task Search_now_sends_the_best_acceptable_release_once_records_it_and_a_second_search_sends_nothing_more()
    {
        await using var indexer = await TorznabServerAsync(Release);
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs);
        var filmId = await SeedAsync(app, indexer.BaseUrl);

        var first = await SearchAsync(app, filmId);

        Assert.Equal(Release, first.GetProperty("releaseName").GetString());
        Assert.Equal("sent", first.GetProperty("dispatchStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("outcome").GetString()));
        Assert.Equal(1, grabs.Count);
        Assert.Contains(Release, await app.Client.GetStringAsync("/api/download-dispatches"));
        var history = await app.Client.GetStringAsync("/api/movies/search-history");
        Assert.Contains(filmId, history);
        Assert.Contains(Release, history);
        Assert.Empty(Directory.GetFiles(app.DataRoot, "*.mkv", SearchOption.AllDirectories));

        var second = await SearchAsync(app, filmId);

        Assert.Equal(1, grabs.Count);
        Assert.NotEqual("sent", second.GetProperty("dispatchStatus").GetString());
    }

    [Fact]
    public async Task A_search_that_finds_nothing_says_so_and_sends_nothing()
    {
        await using var indexer = await TorznabServerAsync(releaseName: null);
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs);
        var filmId = await SeedAsync(app, indexer.BaseUrl);

        var result = await SearchAsync(app, filmId);

        Assert.Equal(0, grabs.Count);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("releaseName").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("summary").GetString()));
        Assert.DoesNotContain("sent", result.GetProperty("dispatchStatus").ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_now_on_a_film_that_does_not_exist_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/movies/no-such-film/search", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, string filmId)
    {
        using var response = await app.Client.PostAsync($"/api/movies/{filmId}/search", content: null);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static Task<ApplicationTestHost> StartAsync(CountingGrabService grabs)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
        });

    private static async Task<string> SeedAsync(ApplicationTestHost app, string indexerUrl)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", indexerUrl, "fixture-key", 10, "2000", "", "movies", true),
            CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Big Buck Bunny", releaseYear = 2008, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private sealed class FakeServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<FakeServer> TorznabServerAsync(string? releaseName)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.ContentType = "application/rss+xml";
            var item = releaseName is null
                ? string.Empty
                : $"""
                    <item>
                      <title>{releaseName}</title>
                      <link>https://fixture.invalid/bunny.torrent</link>
                      <size>8000000000</size>
                      <torznab:attr name="seeders" value="40" />
                      <torznab:attr name="peers" value="3" />
                    </item>
                    """;
            await context.Response.WriteAsync(
                $"""
                <?xml version="1.0"?>
                <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                  <channel>
                    {item}
                  </channel>
                </rss>
                """);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, address);
    }

    private sealed class CountingGrabService : IDownloadClientGrabService
    {
        public int Count { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the fake client.")
            {
                ExternalId = $"fake-hash-{Count}"
            });
        }
    }
}
