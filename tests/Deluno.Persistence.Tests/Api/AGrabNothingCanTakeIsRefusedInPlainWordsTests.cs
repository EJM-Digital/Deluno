using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Sending a release by hand to a film whose library has no client that can take it is refused with a 400 that says why in plain
/// words and names the client or the missing link, never a 500 and never a silent "sent": no client linked at all, a Usenet client
/// for a torrent release, and a client switched off. Real host and databases; nothing is sent because nothing can take it.
/// </summary>
public sealed class AGrabNothingCanTakeIsRefusedInPlainWordsTests
{
    [Fact]
    public async Task A_library_with_no_download_client_linked_says_to_link_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (movieId, _) = await FilmInALibraryAsync(app);

        using var response = await GrabAsync(app, movieId, indexerId: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Link a download client to this library before grabbing a release.",
            await ReasonAsync(response));
    }

    [Fact]
    public async Task A_usenet_client_cannot_take_a_torrent_release_and_the_reason_names_the_library_and_the_source()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (movieId, libraryId) = await FilmInALibraryAsync(app);
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", "http://127.0.0.1:1/", "fixture-key", 10, "2000", "", "movies", true), CancellationToken.None);
        var usenet = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Usenet only", "sabnzbd", "localhost", 8080, null, "api-key", null, "movies", "tv", null, 1, true), CancellationToken.None);
        await RouteAsync(app, libraryId, usenet.Id);

        using var response = await GrabAsync(app, movieId, indexer.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var reason = await ReasonAsync(response);
        Assert.Contains("Films has no download client that can take a release from", reason);
        Assert.Contains("Library Routing", reason);
    }

    [Fact]
    public async Task A_client_that_is_switched_off_is_named_and_the_way_back_is_said()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (movieId, libraryId) = await FilmInALibraryAsync(app);
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", "http://127.0.0.1:1/", "fixture-key", 10, "2000", "", "movies", true), CancellationToken.None);
        var torrent = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Switched off", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, false), CancellationToken.None);
        await RouteAsync(app, libraryId, torrent.Id);

        using var response = await GrabAsync(app, movieId, indexer.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var reason = await ReasonAsync(response);
        Assert.Contains("is switched off, so nothing can be sent to it", reason);
        Assert.Contains("Download clients", reason);
    }

    private static Task<HttpResponseMessage> GrabAsync(ApplicationTestHost app, string movieId, string? indexerId)
        => app.Client.PostAsJsonAsync($"/api/movies/{movieId}/grab", new
        {
            releaseName = "Example.Film.2024.1080p.WEB-DL-GRP",
            indexerId,
            indexerName = "Fixture torznab",
            downloadUrl = "https://indexer.invalid/download/1",
            candidateQuality = "WEB 1080p"
        });

    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty("downloadClient")[0].GetString()!;
    }

    private static async Task RouteAsync(ApplicationTestHost app, string libraryId, string clientId)
    {
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
    }

    private static async Task<(string MovieId, string LibraryId)> FilmInALibraryAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Example Film", releaseYear = 2024, monitored = true });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        return (movieId, libraryId);
    }
}
