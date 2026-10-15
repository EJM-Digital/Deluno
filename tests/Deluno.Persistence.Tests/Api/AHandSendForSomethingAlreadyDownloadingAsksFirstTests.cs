using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1175: sending a release by hand for an episode (or film) that already has a download on its way
/// is refused with a question first. Replace removes the first download, Send as well keeps both, and
/// a different episode is never held up. Driven through the real grab routes.
/// </summary>
public sealed class AHandSendForSomethingAlreadyDownloadingAsksFirstTests
{
    private const string FirstRelease = "The.Simpsons.S00E78.Extreme.Makeover.Homer.Edition.2026.1080p.DSNP.WEB-DL-GRP";
    private const string SameEpisodeAgain = "The.Simpsons.S00E78.Extreme.Makeover.Homer.Edition.2026.2160p.WEB-DL-OTHER";
    private const string OtherEpisode = "The.Simpsons.S00E79.Another.Special.2026.1080p.WEB-DL-GRP";

    [Fact]
    public async Task A_second_hand_send_for_the_same_episode_asks_first_and_another_episode_does_not()
    {
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var seriesId = await SeedAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, seriesId, FirstRelease)).StatusCode);
        Assert.Equal(1, grabs.Count);

        var asked = await GrabAsync(app, seriesId, SameEpisodeAgain);
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await asked.Content.ReadAsStringAsync());
        Assert.Equal("already-downloading", body.GetProperty("code").GetString());
        Assert.Contains("S00E78 is already downloading in", body.GetProperty("message").GetString());
        Assert.Equal(1, grabs.Count);

        // A different episode of the same show is not held up.
        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, seriesId, OtherEpisode)).StatusCode);
        Assert.Equal(2, grabs.Count);

        // Send as well keeps both.
        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, seriesId, SameEpisodeAgain, "send-as-well")).StatusCode);
        Assert.Equal(3, grabs.Count);
    }

    [Fact]
    public async Task A_hand_send_for_a_show_is_recorded_as_a_dispatch_and_leaves_the_show_downloading()
    {
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var seriesId = await SeedAsync(app);
        Assert.NotEqual("downloading", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{seriesId}")).GetProperty("wantedStatus").GetString());

        var sent = await GrabAsync(app, seriesId, FirstRelease);

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Contains(FirstRelease, await app.Client.GetStringAsync("/api/download-dispatches"), StringComparison.Ordinal);
        Assert.Equal("downloading", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{seriesId}")).GetProperty("wantedStatus").GetString());
        Assert.Equal(1, grabs.Count);
    }

    [Fact]
    public async Task A_release_a_show_would_not_pick_is_refused_with_the_force_hint_and_sent_only_when_forced()
    {
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var seriesId = await SeedAsync(app);
        const string risky = "The.Simpsons.S00E78.Extreme.Makeover.Homer.Edition.2026.HDCAM.x264-GRP";

        using var refused = await app.Client.PostAsJsonAsync($"/api/series/{seriesId}/grab", new { releaseName = risky, indexerName = "Fixture", downloadUrl = "https://fixture.invalid/x.torrent", candidateQuality = "CAM" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("requires force override", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, grabs.Count);

        using var forced = await app.Client.PostAsJsonAsync($"/api/series/{seriesId}/grab", new { releaseName = risky, indexerName = "Fixture", downloadUrl = "https://fixture.invalid/x.torrent", candidateQuality = "CAM", force = true, overrideReason = "Only copy." });
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.Equal(1, grabs.Count);
        Assert.Contains("Only copy.", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_removes_the_first_download_from_its_client_before_sending_the_new_one()
    {
        var grabs = new CountingGrabService();
        var removed = new List<(string ClientId, string Item, bool FromClient, bool Blocklist)>();
        await using var app = await StartAsync(grabs, removed);
        var seriesId = await SeedAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, seriesId, FirstRelease)).StatusCode);

        var replaced = await GrabAsync(app, seriesId, SameEpisodeAgain, "replace");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var gone = Assert.Single(removed);
        Assert.Equal("fake-hash", gone.Item);
        Assert.True(gone.FromClient);
        Assert.False(gone.Blocklist);
        Assert.Equal(2, grabs.Count);
    }

    [Fact]
    public async Task Searching_for_an_episode_sends_the_best_release_once_and_lists_nothing_to_choose_from()
    {
        await using var indexer = await TorznabServerAsync();
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var (seriesId, episodeId) = await SeedWithIndexerAsync(app, indexer.BaseUrl);

        var first = await SearchAsync(app, seriesId, episodeId, "send");
        Assert.Equal(1, first.GetProperty("sentCount").GetInt32());
        Assert.Equal(1, grabs.Count);
        var episode = Assert.Single(first.GetProperty("episodes").EnumerateArray());
        Assert.Empty(episode.GetProperty("candidates").EnumerateArray());

        // The episode is now downloading, so the same search never sends a second release.
        var second = await SearchAsync(app, seriesId, episodeId, "send");
        Assert.Equal(0, second.GetProperty("sentCount").GetInt32());
        Assert.Equal(1, grabs.Count);
        Assert.NotEmpty(second.GetProperty("alreadyDownloading").EnumerateArray());
    }

    [Fact]
    public async Task Choosing_a_release_for_an_episode_sends_nothing_and_lists_what_was_found()
    {
        await using var indexer = await TorznabServerAsync();
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var (seriesId, episodeId) = await SeedWithIndexerAsync(app, indexer.BaseUrl);

        var result = await SearchAsync(app, seriesId, episodeId, "choose");
        Assert.Equal(0, result.GetProperty("sentCount").GetInt32());
        Assert.Equal(0, grabs.Count);
        var episode = Assert.Single(result.GetProperty("episodes").EnumerateArray());
        Assert.NotEmpty(episode.GetProperty("candidates").EnumerateArray());
    }

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, string seriesId, string episodeId, string mode)
    {
        var response = await app.Client.PostAsJsonAsync($"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, mode });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static async Task<(string SeriesId, string EpisodeId)> SeedWithIndexerAsync(ApplicationTestHost app, string indexerUrl)
    {
        var seriesId = await SeedAsync(app);
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", indexerUrl, "fixture-key", 10, "5000", "", "tv", true),
            CancellationToken.None);
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries");
        var libraryId = libraries.EnumerateArray().First().GetProperty("id").GetString()!;
        var routing = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/routing");
        var clientId = routing.GetProperty("downloadClients").EnumerateArray().First().GetProperty("downloadClientId").GetString()!;
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        await connections.UpdateDownloadClientHealthAsync(clientId, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var inventory = await app.Services.GetRequiredService<ISeriesCatalogRepository>().GetInventoryDetailAsync(seriesId, CancellationToken.None);
        return (seriesId, inventory!.Episodes.Single(item => item.SeasonNumber == 0 && item.EpisodeNumber == 78).EpisodeId);
    }

    private sealed class FakeServer(Microsoft.AspNetCore.Builder.WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<FakeServer> TorznabServerAsync()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
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
                      <title>The.Simpsons.S00E78.Extreme.Makeover.Homer.Edition.2026.1080p.DSNP.WEB-DL.DDP5.1.H.264-GRP</title>
                      <link>https://fixture.invalid/simpsons.torrent</link>
                      <size>1500000000</size>
                      <torznab:attr name="seeders" value="40" />
                      <torznab:attr name="peers" value="3" />
                    </item>
                  </channel>
                </rss>
                """);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, address);
    }

    private static Task<HttpResponseMessage> GrabAsync(ApplicationTestHost app, string seriesId, string releaseName, string? duplicateAction = null)
        => app.Client.PostAsJsonAsync($"/api/series/{seriesId}/grab", new
        {
            releaseName,
            indexerName = "Fixture",
            downloadUrl = "https://fixture.invalid/x.torrent",
            candidateQuality = "WEB 1080p",
            duplicateAction
        });

    private static async Task<ApplicationTestHost> StartAsync(
        CountingGrabService grabs,
        List<(string ClientId, string Item, bool FromClient, bool Blocklist)>? removed)
        => await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
            if (removed is not null)
            {
                services.RemoveAll<IDownloadClientTelemetryService>();
                services.AddSingleton(TelemetryStub.Create(removed));
            }
        });

    private static async Task<string> SeedAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Shows", mediaType = "tv", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        var created = await app.Client.PostAsJsonAsync("/api/series/", new CreateSeriesRequest("The Simpsons", 1989, "tt0096697", LibraryId: libraryId));
        created.EnsureSuccessStatusCode();
        var series = (await created.Content.ReadFromJsonAsync<SeriesListItem>())!;
        await app.Services.GetRequiredService<ISeriesCatalogRepository>().SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(0, 78, "Extreme Makeover Homer Edition", null, DateTimeOffset.Parse("2026-10-01T00:00:00Z")),
                new CatalogueEpisodeItem(0, 79, "Another Special", null, DateTimeOffset.Parse("2026-10-02T00:00:00Z"))
            ],
            "tmdb",
            CancellationToken.None);
        return series.Id;
    }

    private sealed class CountingGrabService : IDownloadClientGrabService
    {
        public int Count { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the fake client.")
            {
                ExternalId = Count == 1 ? "fake-hash" : $"fake-hash-{Count}"
            });
        }
    }

    /// <summary>Answers only the removal a Replace asks for; every other call is a harmless empty answer.</summary>
    public class TelemetryStub : DispatchProxy
    {
        private List<(string ClientId, string Item, bool FromClient, bool Blocklist)> removed = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result = null;
            if (targetMethod!.Name == "RemoveWithOptionsAsync")
            {
                removed.Add(((string)args![0]!, (string)args[1]!, (bool)args[2]!, (bool)args[3]!));
                result = new QueueRemovalResult((string)args[0]!, (string)args[1]!, "removed", true, "Removed.", false, false, null, null);
            }

            var returns = targetMethod.ReturnType;
            if (returns == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returns.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [result]);
            }

            return returns.IsValueType ? Activator.CreateInstance(returns) : result;
        }

        public static IDownloadClientTelemetryService Create(List<(string ClientId, string Item, bool FromClient, bool Blocklist)> removed)
        {
            var proxy = Create<IDownloadClientTelemetryService, TelemetryStub>();
            ((TelemetryStub)(object)proxy).removed = removed;
            return proxy;
        }
    }
}
