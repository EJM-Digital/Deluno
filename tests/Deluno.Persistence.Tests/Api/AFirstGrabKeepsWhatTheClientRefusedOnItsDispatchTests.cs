using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1470, from the golden-VM proof: qBittorrent 5.2.1 refused the save folder after a normal first grab, and the download's row
/// never said so, because the refusal was only kept when the request already named a dispatch, and a first grab makes its dispatch
/// afterwards. The grab hands its refusals back in the result and each path writes them when it records the dispatch. The grab
/// service is faked here to give the refusals qBittorrent's recorded <c>location-refused</c> reply produces (see
/// <c>TheGrabSaysWhatTheClientRefusedTests</c>); these tests are about the path keeping them.
/// </summary>
public sealed class AFirstGrabKeepsWhatTheClientRefusedOnItsDispatchTests
{
    private const string Refused = """
        [{"call":"setLocation","what":"the save folder Z:\no-such-drive","reason":"Cannot make save path","fix":"Make the folder in qBittorrent (or pick one it can write to) or change the library's downloads folder, then send it again."}]
        """;

    [Fact]
    public async Task A_film_sent_by_hand_keeps_the_refusal_on_its_dispatch()
    {
        await using var app = await StartAsync(Refused);
        var (libraryId, clientId, indexerId) = await LibraryAsync(app);
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Example Film", releaseYear = 2024, monitored = true });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var response = await app.Client.PostAsJsonAsync($"/api/movies/{movieId}/grab", new
        {
            releaseName = "Example.Film.2024.1080p.WEB-DL-GRP",
            indexerId,
            indexerName = "Fixture torznab",
            downloadUrl = "https://indexer.invalid/download/1",
            candidateQuality = "WEB 1080p"
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await AssertKeptAsync(app, clientId);
        Assert.NotEmpty(libraryId);
    }

    [Fact]
    public async Task A_release_sent_from_the_Search_page_keeps_the_refusal_on_its_dispatch()
    {
        await using var app = await StartAsync(Refused);
        var (_, clientId, _) = await LibraryAsync(app);

        using var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType = "movie"
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await AssertKeptAsync(app, clientId);
    }

    [Fact]
    public async Task A_grab_the_client_took_whole_keeps_nothing_on_its_dispatch()
    {
        await using var app = await StartAsync(null);
        var (_, clientId, _) = await LibraryAsync(app);

        using var response = await app.Client.PostAsJsonAsync("/api/search/grab", new
        {
            releaseName = "Example.Release.WEB.1080p",
            downloadUrl = "https://fixture.invalid/x.torrent",
            searchType = "movie"
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var dispatch = Assert.Single((await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .QueryDispatchesAsync(new DispatchQueryFilter { ClientId = clientId }, new DispatchPaginationOptions(), CancellationToken.None)).Items);
        Assert.DoesNotContain("followUpRefusals", dispatch.NotesJson ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task AssertKeptAsync(ApplicationTestHost app, string clientId)
    {
        var dispatch = Assert.Single((await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .QueryDispatchesAsync(new DispatchQueryFilter { ClientId = clientId }, new DispatchPaginationOptions(), CancellationToken.None)).Items);
        var notes = JsonDocument.Parse(dispatch.NotesJson!).RootElement;
        var kept = Assert.Single(notes.GetProperty("followUpRefusals").EnumerateArray().ToArray());
        Assert.Equal("setLocation", kept.GetProperty("call").GetString());
        Assert.Equal("Cannot make save path", kept.GetProperty("reason").GetString());
        // The rest of what the path wrote in the notes is still there.
        Assert.True(notes.EnumerateObject().Count() > 1, dispatch.NotesJson);
    }

    private static Task<ApplicationTestHost> StartAsync(string? refusalsJson)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(new RefusedGrabService(refusalsJson));
        });

    private static async Task<(string LibraryId, string ClientId, string IndexerId)> LibraryAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", "http://127.0.0.1:1/", "fixture-key", 10, "2000", "", "movies", true), CancellationToken.None);
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        return (libraryId, client.Id, indexer.Id);
    }

    private sealed class RefusedGrabService(string? refusalsJson) : IDownloadClientGrabService
    {
        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the client.")
            {
                ExternalId = "hash-from-the-client",
                FollowUpRefusalsJson = refusalsJson
            });
    }
}
