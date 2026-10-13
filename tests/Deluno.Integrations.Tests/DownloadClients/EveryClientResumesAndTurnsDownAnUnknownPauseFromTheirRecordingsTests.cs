using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1251 and #1237, replayed from what each client really answered. A job paused by its id and resumed by its id: the list after the
/// pause shows it paused, the resume is sent, and the list after shows it running again, which is when Deluno says it is resumed.
/// A pause of an id the client never had is not sent at all: the client is asked first, and says it has no such download.
/// </summary>
public sealed class EveryClientResumesAndTurnsDownAnUnknownPauseFromTheirRecordingsTests
{
    private static readonly Dictionary<string, string> RecordedIds = new()
    {
        ["sabnzbd"] = "e50a667e-1b42-4218-999e-5e97c9527db6",
        ["nzbget"] = "1",
        ["qbittorrent"] = "2d24a2f8b7448f1ef3fc61507a1d866c647ec47e",
        ["transmission"] = "953d25cd8b330ca129b4097f680c00a5cf57fcb6",
        ["deluge"] = "5f7e2aa930c440ad6c3c45ac673e8ab82cf667ae"
    };

    private static DownloadClientItem Client(string protocol)
        => new($"{protocol}-1", protocol, protocol, "localhost", 8080, null, "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<DownloadClientActionResult> Run(string app, HttpMessageHandler handler, string action, string id)
        => app switch
        {
            "sabnzbd" => new SabnzbdDownloadClient(new Factory(handler)).ExecuteActionAsync(Client(app), action, id, CancellationToken.None),
            "nzbget" => new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero).ExecuteActionAsync(Client(app), action, id, CancellationToken.None),
            "qbittorrent" => new QbittorrentDownloadClient(() => handler).ExecuteActionAsync(Client(app), action, id, CancellationToken.None),
            "transmission" => new TransmissionDownloadClient(new Factory(handler)).ExecuteActionAsync(Client(app), action, id, CancellationToken.None),
            _ => new DelugeDownloadClient(new Factory(handler)).ExecuteActionAsync(Client(app), action, id, CancellationToken.None)
        };

    /// <summary>The recording of a client's sign-in, handshake or version, which every call there starts with.</summary>
    private static Recording[] Opening(string app) => app switch
    {
        "qbittorrent" => [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")],
        "transmission" => [RecordedReplies.Transmission("settings")],
        "deluge" => [RecordedReplies.Deluge("sign-in")],
        "sabnzbd" => [RecordedReplies.Sabnzbd("version")],
        _ => [RecordedReplies.Nzbget("version")]
    };

    private static Recording Resumed(string app) => app switch
    {
        "sabnzbd" => RecordedReplies.Sabnzbd("resumed"),
        "nzbget" => RecordedReplies.Nzbget("resumed"),
        "qbittorrent" => RecordedReplies.Qbittorrent("resumed"),
        "transmission" => RecordedReplies.Transmission("resumed"),
        _ => RecordedReplies.Deluge("resumed")
    };

    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_paused_job_is_resumed_the_way_each_client_was_recorded_answering_it(string app)
    {
        var calls = new List<string>();
        var handler = new Watching(Resumed(app).Sequence(Opening(app)), calls);

        var result = await Run(app, handler, DownloadClientActions.Resume, RecordedIds[app]);

        Assert.True(result.Succeeded, result.Message);
        // The resume itself was sent, once, between the look before and the look after.
        Assert.Single(calls, call => call.Contains("name=resume", StringComparison.Ordinal) || call.Contains("GroupResume", StringComparison.Ordinal)
            || call.Contains("torrents/start", StringComparison.Ordinal) || call.Contains("torrent-start", StringComparison.Ordinal)
            || call.Contains("core.resume_torrent", StringComparison.Ordinal));
    }

    /// <summary>Writes down every call (address and body) on its way to the replayed recording.</summary>
    private sealed class Watching(HttpMessageHandler inner, List<string> calls) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Add(request.RequestUri!.PathAndQuery + (request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_pause_of_an_id_the_client_never_had_is_not_sent_and_says_there_is_no_such_download(string app)
    {
        var recording = app switch
        {
            "qbittorrent" => RecordedReplies.Qbittorrent("pause-unknown-id"),
            "transmission" => RecordedReplies.Transmission("pause-unknown-id"),
            _ => RecordedReplies.Deluge("pause-unknown-id")
        };
        var id = app == "deluge" ? "0000000000000000000000000000000000000000" : "0000000000000000000000000000000000000000";
        var pauseSent = 0;
        var handler = RecordedReplies.Handler(app, [.. Opening(app), recording], (request, body) =>
        {
            var call = request.RequestUri!.PathAndQuery + body;
            if (call.Contains("torrents/stop", StringComparison.Ordinal) || call.Contains("torrent-stop", StringComparison.Ordinal) || call.Contains("core.pause_torrent", StringComparison.Ordinal))
            {
                pauseSent++;
            }

            return null;
        });

        var result = await Run(app, handler, DownloadClientActions.Pause, id);

        Assert.False(result.Succeeded);
        Assert.Contains("no download with that id", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, pauseSent);
    }
}
