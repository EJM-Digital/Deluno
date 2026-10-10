using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1062: Transmission's numeric torrent id belongs to the running session, so
/// Deluno files and acts on a torrent by its info-hash. Transmission accepts a
/// hash string wherever it accepts a number in a request's ids.
/// </summary>
public sealed class TransmissionIsKeyedOnTheTorrentHashTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task The_queue_files_a_torrent_by_its_hash_and_keeps_the_number_only_as_its_old_id()
    {
        var sent = new List<string>();
        var reply = "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":7,\"hashString\":\"" + Hash.ToUpperInvariant() + "\","
            + "\"name\":\"Sherlock.Jr.1924.1080p.BluRay\",\"status\":6,\"percentDone\":1,\"totalSize\":5,\"downloadedEver\":5,\"error\":0}]}}";

        var snapshot = await new TransmissionDownloadClient(new Factory(new Handler(sent, reply)))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(Hash, item.Id);
        Assert.Equal("7", item.LegacyId);
        Assert.Contains("hashString", sent.First(body => body.Contains("torrent-get", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_daemon_that_sends_no_hash_still_works_by_number()
    {
        var reply = "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":7,\"name\":\"Old.Daemon.1924\",\"status\":6,\"percentDone\":1,\"error\":0}]}}";

        var snapshot = await new TransmissionDownloadClient(new Factory(new Handler([], reply)))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal("7", item.Id);
        Assert.Null(item.LegacyId);
    }

    [Theory]
    [InlineData("pause", "torrent-stop")]
    [InlineData("delete-with-data", "torrent-remove")]
    [InlineData("move-to-top", "queue-move-top")]
    public async Task Actions_address_the_torrent_by_its_hash(string action, string method)
    {
        var sent = new List<string>();

        var result = await new TransmissionDownloadClient(new Factory(new Handler(sent, "{\"result\":\"success\",\"arguments\":{}}")))
            .ExecuteActionAsync(Client(), action, Hash.ToUpperInvariant(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        // A removal with its data first reads the torrent's name and folder (#1224), so the action is the last request.
        using var document = JsonDocument.Parse(sent[^1]);
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        Assert.Equal(Hash, document.RootElement.GetProperty("arguments").GetProperty("ids")[0].GetString());
    }

    [Fact]
    public async Task An_id_that_is_neither_a_number_nor_a_hash_is_refused_without_a_request()
    {
        var sent = new List<string>();

        var result = await new TransmissionDownloadClient(new Factory(new Handler(sent, "{}")))
            .ExecuteActionAsync(Client(), "pause", "not-an-id", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_grab_reports_the_torrents_hash_as_its_id_including_when_the_client_already_had_it()
    {
        var added = "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{\"id\":12,\"hashString\":\"" + Hash + "\"}}}";
        var duplicate = "{\"result\":\"success\",\"arguments\":{\"torrent-duplicate\":{\"id\":12,\"hashString\":\"" + Hash.ToUpperInvariant() + "\"}}}";

        var first = await Grab(added);
        var second = await Grab(duplicate);

        Assert.Equal(Hash, first.ExternalId);
        Assert.False(first.IsDuplicateFollow);
        Assert.Equal(Hash, second.ExternalId);
        Assert.True(second.IsDuplicateFollow);
    }

    private static Task<DownloadClientGrabResult> Grab(string reply)
        => new TransmissionDownloadClient(new Factory(new Handler([], reply)))
            .GrabAsync(Client(), new DownloadClientGrabRequest(
                "Sherlock.Jr.1924.1080p.BluRay", "magnet:?xt=urn:btih:" + Hash, "movies", null, "Fixture"), CancellationToken.None);

    private static DownloadClientItem Client()
        => new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(List<string> sent, string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
