using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A release goes into the category the routed client was set up with for Movies or for TV, so it lands in the folder the library
/// and its processor watch. Never a category Deluno made up ("movies") when the client was given another name. Each client is
/// the real one's recorded replies; the request each one is sent is what is under test.
/// </summary>
public sealed class TheDownloadGoesIntoTheCategoryTheClientIsSetUpWithTests
{
    private const string MoviesCategory = "family-films";
    private const string TvCategory = "weekly-shows";
    private const string Magnet = "magnet:?xt=urn:btih:8d878a21c3d4180ae08b4e044904667d9f30bec5&dn=Big.Buck.Bunny";

    [Theory]
    [InlineData("movies", MoviesCategory)]
    [InlineData("tv", TvCategory)]
    public async Task Qbittorrent_adds_the_release_to_the_category_set_up_for_its_media_type(string mediaType, string expected)
    {
        var bodies = new List<string>();
        var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("finished-single-file")],
            (request, body) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("torrents/add", StringComparison.Ordinal)) bodies.Add(body);
                return null;
            });

        await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(mediaType), CancellationToken.None);

        var body = Assert.Single(bodies);
        Assert.Contains($"category={expected}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("category=movies", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("movies", MoviesCategory)]
    [InlineData("tv", TvCategory)]
    public async Task Transmission_labels_the_release_with_the_category_set_up_for_its_media_type(string mediaType, string expected)
    {
        var sent = new List<string>();
        var handler = RecordedReplies.Handler("transmission", [RecordedReplies.Transmission("settings"), RecordedReplies.Transmission("finished-single-file")],
            (_, body) => { sent.Add(body); return null; });

        await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission"), Request(mediaType), CancellationToken.None);

        var add = sent.Single(body => body.Contains("\"torrent-add\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(add);
        Assert.Equal(expected, document.RootElement.GetProperty("arguments").GetProperty("labels")[0].GetString());
    }

    [Theory]
    [InlineData("movies", MoviesCategory)]
    [InlineData("tv", TvCategory)]
    public async Task Deluge_labels_the_release_with_the_category_set_up_for_its_media_type(string mediaType, string expected)
    {
        var sent = new List<string>();
        var handler = RecordedReplies.Handler("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("magnet-no-metadata"), RecordedReplies.Deluge("label-set")],
            (_, body) => { sent.Add(body); return null; });

        await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client("deluge"), Request(mediaType) with { DownloadUrl = Magnet }, CancellationToken.None);

        Assert.Contains(sent, body => body.Contains("label.set_torrent", StringComparison.Ordinal) && body.Contains($"\"{expected}\"", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, body => body.Contains("label.set_torrent", StringComparison.Ordinal) && body.Contains("\"movies\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("movies", MoviesCategory)]
    [InlineData("tv", TvCategory)]
    public async Task Sabnzbd_adds_the_release_to_the_category_set_up_for_its_media_type(string mediaType, string expected)
    {
        string? query = null;
        var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("queued")], (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("mode=addurl", StringComparison.Ordinal))
            {
                query = request.RequestUri.Query;
                return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
            }

            return null;
        });

        await new SabnzbdDownloadClient(new StubFactory(handler)).GrabAsync(Client("sabnzbd") with { Secret = "api-key" }, Request(mediaType), CancellationToken.None);

        Assert.Contains($"cat={expected}", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("movies", MoviesCategory)]
    [InlineData("tv", TvCategory)]
    public async Task NzbGet_appends_the_release_to_the_category_set_up_for_its_media_type(string mediaType, string expected)
    {
        var sent = new List<string>();
        var handler = RecordedReplies.Handler("nzbget", [RecordedReplies.Nzbget("queued")], (_, body) => { sent.Add(body); return null; });

        await new NzbGetDownloadClient(new StubFactory(handler)).GrabAsync(Client("nzbget"), Request(mediaType), CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(sent));
        // append(NZBFilename, Content, Category, Priority, ...)
        Assert.Equal(expected, document.RootElement.GetProperty("params")[2].GetString());
    }

    private static DownloadClientGrabRequest Request(string mediaType)
        => new("Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO", "http://indexer.test/download/1", mediaType, null, "Fixture indexer");

    private static DownloadClientItem Client(string protocol)
        => new(
            "client-1", protocol, protocol, "localhost", 8080, null, null, "http://localhost:8080/", false, null,
            MoviesCategory, TvCategory, null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
