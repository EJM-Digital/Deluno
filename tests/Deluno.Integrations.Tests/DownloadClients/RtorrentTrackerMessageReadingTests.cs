using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #878, cleanup sheet C5: rTorrent's own d.multicall2 never carries a tracker's message, so Deluno asks <c>t.multicall</c> for
/// <c>t.is_enabled=</c> and <c>t.message=</c> and reads "torrent not registered" from the message. rTorrent 0.16.25 has no <c>t.message</c>
/// (it faults the call: the recorded reply in <c>downloading</c>, tested in RtorrentOnRecordingsTests, where Deluno keeps the tracker status
/// unknown), so no recorded rTorrent gives the reply these tests need: a tracker row that carries a message. They are DELIBERATE hand-built
/// replies (tests/recorded-reply-allow-list.json) for an rTorrent that does answer t.message, until it is shown which rTorrent that is.
/// The list row beside them is the recorded one.
/// </summary>
public sealed class RtorrentTrackerMessageReadingTests
{
    private static DownloadClientItem Client()
        => new("client-1", "rTorrent", "rtorrent", "localhost", 8000, null, null, "http://localhost:8000/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage XmlResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    /// <summary>The recorded list row; a tracker row with a message (the reply no recorded rTorrent gives) or a failing tracker call.</summary>
    private sealed class Handler(string? trackerMessage = null, bool trackerCallFails = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = Regex.Match(body, "<methodName>(.*?)</methodName>", RegexOptions.Singleline).Groups[1].Value;
            if (method == "t.multicall")
            {
                if (trackerCallFails)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return XmlResponse($"""<?xml version="1.0"?><methodResponse><params><param><value><array><data><value><array><data><value><i8>1</i8></value><value><string>{trackerMessage}</string></value></data></array></value></data></array></value></param></params></methodResponse>""");
            }

            return XmlResponse(method == "system.client_version"
                ? RecordedReplies.Rtorrent("version").Single.Reply.Body!
                : RecordedReplies.Rtorrent("downloading").Exchanges[0].Reply.Body!);
        }
    }

    [Fact]
    public async Task An_unregistered_tracker_message_is_read_as_unregistered()
    {
        var snapshot = await new RtorrentDownloadClient(new StubFactory(new Handler(trackerMessage: "torrent not registered with this tracker")))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task A_healthy_tracker_message_reads_as_ok()
    {
        var snapshot = await new RtorrentDownloadClient(new StubFactory(new Handler(trackerMessage: string.Empty)))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A tracker rTorrent cannot reach at all is a local/outage problem, never a bad release (#901).</summary>
    [Fact]
    public async Task A_tracker_call_that_fails_stays_unknown_rather_than_unregistered()
    {
        var snapshot = await new RtorrentDownloadClient(new StubFactory(new Handler(trackerCallFails: true)))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }
}
