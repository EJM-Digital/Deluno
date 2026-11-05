using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1494, over the real grab route: a release sent by hand while the top-priority client is at its own download limit goes to the next
/// client with a free slot, and the decision row says which was full and where it went, after the tag reason when the title's tag chose
/// the client that took it.
/// </summary>
public sealed class AHandSendPassesOverAFullClientTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP";

    private sealed class Loads(Func<DownloadClientItem, DownloadClientLoad?> answer) : IDownloadClientLoadReader
    {
        public Task<DownloadClientLoad?> ReadAsync(DownloadClientItem client, CancellationToken cancellationToken) => Task.FromResult(answer(client));
    }

    private sealed class RecordingGrabs : IDownloadClientGrabService
    {
        public List<string> ClientIds { get; } = [];

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            ClientIds.Add(clientId);
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the fake client.") { ExternalId = "fake-hash" });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hand_send_goes_to_the_next_client_when_the_first_is_full_and_the_decision_says_why(bool bothAreTaggedForTheTitle)
    {
        var grabs = new RecordingGrabs();
        string? fullId = null;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
            services.RemoveAll<IDownloadClientLoadReader>();
            services.AddSingleton<IDownloadClientLoadReader>(new Loads(client => client.Id == fullId ? new DownloadClientLoad(3, 3) : new DownloadClientLoad(0, 3)));
        });
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = (await (await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var first = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("First", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true, Tags: bothAreTaggedForTheTitle ? "kids" : null), CancellationToken.None);
        var second = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Second", "transmission", "localhost", 1235, null, null, null, "movies", "tv", null, 2, true, Tags: bothAreTaggedForTheTitle ? "kids" : null), CancellationToken.None);
        fullId = first.Id;
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[]
            {
                new { downloadClientId = first.Id, priority = 1, category = (string?)null },
                new { downloadClientId = second.Id, priority = 2, category = (string?)null },
            }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Big Buck Bunny", releaseYear = 2008, monitored = true, libraryId, searchForMissingNow = false });
        var filmId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        if (bothAreTaggedForTheTitle)
        {
            var tagged = await app.Client.PostAsJsonAsync("/api/movies/bulk/tags", new { movieIds = new[] { filmId }, tags = "kids", mode = "add" });
            Assert.True(tagged.IsSuccessStatusCode, await tagged.Content.ReadAsStringAsync());
        }

        var sent = await app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", new
        {
            releaseName = Release, indexerName = "Fixture", downloadUrl = "https://fixture.invalid/x.torrent", candidateQuality = "Bluray 1080p",
        });

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(second.Id, Assert.Single(grabs.ClientIds));
        var decisions = await app.Client.GetFromJsonAsync<JsonElement>($"/api/decisions?relatedEntityType=movie&relatedEntityId={filmId}");
        var items = decisions.ValueKind == JsonValueKind.Array ? decisions : decisions.GetProperty("items");
        var row = Assert.Single(items.EnumerateArray(), item => item.GetProperty("scope").GetString() == "movie.grab.manual");
        var reason = row.GetProperty("inputs").GetProperty("downloadClientRoutingReason").GetString();
        Assert.Contains("was full (3 of 3 running), so this went to", reason);
        Assert.Equal(bothAreTaggedForTheTitle, reason!.Contains("kids", StringComparison.OrdinalIgnoreCase));
    }
}
