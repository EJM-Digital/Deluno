using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1173: the show-level preview has no target episode, but a release still says which episode it is.
/// Its SxxEyy is compared with its own title (the provider's titles for that SxxEyy) and a mismatch is
/// refused with the same reason the episode-level Choose gives. Driven through the real
/// <c>POST /api/series/{id}/search?mode=preview</c>.
/// </summary>
public sealed class AWholeShowPreviewChecksEachReleaseAgainstItsOwnEpisodeTests
{
    private const string Mislabelled = "The.Simpsons.S38E02.The.Childrens.Book.Job.1080p.DSNP.WEB-DL.DDP5.1.H.264-FLUX";
    private const string Right = "The.Simpsons.S38E02.Diary.of.a.Chimpy.Kid.1080p.WEB-DL.DDP5.1.H.264-ETHEL";

    [Fact]
    public async Task The_preview_of_the_whole_show_refuses_a_release_that_carries_another_episodes_title()
    {
        await using var indexer = await TorznabServerAsync(Mislabelled, Right);
        await using var app = await ApplicationTestHost.StartAsync();
        var seriesId = await SeedAsync(app, indexer.BaseUrl);

        var response = await app.Client.PostAsync($"/api/series/{seriesId}/search?mode=preview", null);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var candidates = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray().ToArray();

        var wrong = candidates.Single(item => item.GetProperty("releaseName").GetString() == Mislabelled);
        Assert.Equal("rejected", wrong.GetProperty("decisionStatus").GetString());
        Assert.Contains("The name says S38E02, but its title is S38E01's", wrong.ToString(), StringComparison.Ordinal);

        var correct = candidates.Single(item => item.GetProperty("releaseName").GetString() == Right);
        Assert.NotEqual("rejected", correct.GetProperty("decisionStatus").GetString());
    }

    private static async Task<string> SeedAsync(ApplicationTestHost app, string indexerUrl)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Shows", mediaType = "tv", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", indexerUrl, "fixture-key", 10, "5000", "", "tv", true),
            CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        var created = await app.Client.PostAsJsonAsync("/api/series/", new CreateSeriesRequest("The Simpsons", 1989, "tt0096697", LibraryId: libraryId));
        created.EnsureSuccessStatusCode();
        var series = (await created.Content.ReadFromJsonAsync<SeriesListItem>())!;
        await app.Services.GetRequiredService<ISeriesCatalogRepository>().SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(38, 1, "The Children's Book Job", null, DateTimeOffset.Parse("2026-09-27T00:00:00Z")),
                new CatalogueEpisodeItem(38, 2, "Diary of a Chimpy Kid", null, DateTimeOffset.Parse("2026-10-04T00:00:00Z"))
            ],
            "tmdb",
            CancellationToken.None);
        return series.Id;
    }

    private sealed class FakeServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<FakeServer> TorznabServerAsync(params string[] releaseNames)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var items = string.Concat(releaseNames.Select((name, index) =>
            $"<item><title>{name}</title><link>https://fixture.invalid/s{index}.torrent</link><size>1500000000</size>"
            + "<torznab:attr name=\"seeders\" value=\"40\" /><torznab:attr name=\"peers\" value=\"3\" /></item>"));
        var feed = "<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>" + items + "</channel></rss>";
        app.Run(async context =>
        {
            context.Response.ContentType = "application/rss+xml";
            await context.Response.WriteAsync(feed);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, address);
    }
}
