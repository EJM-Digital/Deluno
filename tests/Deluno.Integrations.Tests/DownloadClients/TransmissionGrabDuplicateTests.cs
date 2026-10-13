using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #877/#911, cleanup sheet B3: Transmission's own <c>torrent-add</c> RPC
/// says outright, in <c>torrent-duplicate</c>, when it already held this
/// exact torrent — no before/after hash guessing needed, unlike qBittorrent,
/// Deluge, rTorrent and uTorrent. Deluno now reads that signal and follows
/// the existing item with a plain-words message, instead of reporting a
/// generic "sent".
/// </summary>
public sealed class TransmissionGrabDuplicateTests
{
    [Fact]
    public async Task A_transmission_duplicate_answer_is_followed_instead_of_reported_as_an_ordinary_send()
    {
        var handler = Answering(RecordedReplies.Transmission("duplicate-add").Response("torrent-add"));
        var result = await new TransmissionDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("04d6d32adaa36ddd9877f0734a7027a4a0055042", result.ExternalId);
        Assert.True(result.IsDuplicateFollow);
        Assert.Contains("already holds this release", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #874/#942, cleanup sheet B2: Transmission's own <c>torrent-add</c>
    /// answers "success" or a reason in words — a reason is Transmission
    /// looking at the exact link or file it was handed and refusing it
    /// outright, in the same request, the same shape qBittorrent's "Fails."
    /// and NZBGet's RPC error already read as a dead release rather than a
    /// generic failure.
    /// </summary>
    [Fact]
    public async Task A_transmission_refusal_reason_is_read_as_a_dead_release()
    {
        var handler = Answering(RecordedReplies.Transmission("add-refused").Response("torrent-add"));
        var result = await new TransmissionDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("unrecognized info", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_added_torrent_is_not_read_as_a_duplicate()
    {
        var handler = Answering(RecordedReplies.Transmission("finished-single-file").Response("torrent-add"));
        var result = await new TransmissionDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.IsDuplicateFollow);
        Assert.DoesNotContain("already holds this release", result.Message, StringComparison.Ordinal);
    }

    private static DownloadClientGrabRequest Request()
        => new("Example.Movie.2024.1080p", "http://indexer.test/download/1", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem Client()
        => new(
            "client-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Transmission 4.1.3 answering the add with the recorded reply and every other call (its category lookup and label) with its recorded
    /// settings and label replies.
    /// </summary>
    private static HttpMessageHandler Answering(HttpResponseMessage addReply)
        => RecordedReplies.Handler(
            "transmission",
            [RecordedReplies.Transmission("settings"), RecordedReplies.Transmission("label-set")],
            (_, body) => body.Contains("\"torrent-add\"", StringComparison.Ordinal) ? addReply : null);
}
