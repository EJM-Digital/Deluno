using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #877, cleanup sheet B3, and #1237 from Deluge 2.2.0's recording of a torrent added twice: Deluge answers the second add with an
/// error object ("Torrent already in session (hash)", code 4) and a null result. The tests first used a bare null result and no
/// error, which Deluge 2.2.0 does not send for a duplicate; they now use the recorded error. A release whose hash Deluno knows
/// (a magnet, or a .torrent it fetched) is checked against Deluge's session state and followed; a release Deluno holds only a link
/// for has no hash to check, so it is followed by the hash Deluge names in its own message.
/// </summary>
public sealed class DelugeGrabDuplicateTests
{
    private const string Hash = "d8185091144732e990e3595f7db0e4410b1d9424";

    private static DownloadClientGrabRequest Magnet()
        => new("Example.Movie.2024.1080p", $"magnet:?xt=urn:btih:{Hash}&dn=Example", "movies", "movies", "Fixture indexer");

    private static DownloadClientGrabRequest Link()
        => new("Example.Movie.2024.1080p", "http://indexer.test/download/1.torrent", "movies", "movies", "Fixture indexer");

    private static DownloadClientGrabRequest File()
        => new("Example.Movie.2024.1080p", "https://indexer.test/download/1.torrent", "movies", "movies", "Fixture indexer")
        {
            TorrentFile = System.Text.Encoding.Latin1.GetBytes("d4:infod6:lengthi5eee"),
            KnownInfoHash = Hash
        };

    /// <summary>Deluge 2.2.0's own words for a torrent it already holds: the recorded error object.</summary>
    private static HttpResponseMessage RecordedDuplicate() => RecordedReplies.Deluge("duplicate-add").Response("core.add_torrent_file");

    /// <summary>The recorded reply with its error taken out: a null result and nothing else, as an older Deluge answers.</summary>
    private static HttpResponseMessage NullResultNoError()
        => RecordedReplies.Json(RecordedReplies.Deluge("duplicate-add").Body("core.add_torrent_file", ("error", null)));

    [Theory]
    [InlineData("magnet")]
    [InlineData("file")]
    public async Task A_release_deluge_already_holds_is_followed_instead_of_failed(string kind)
    {
        var handler = new DelugeHandler(RecordedDuplicate(), sessionHashes: [Hash]);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), kind == "magnet" ? Magnet() : File(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.IsDuplicateFollow);
        Assert.Contains("already holds this release", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A link Deluno holds no hash for: Deluge's add answered with its error object and said which torrent it already had. That is not a
    /// dead release (the torrent is there), and must not be blocklisted; it is followed by the hash in Deluge's message.
    /// </summary>
    [Fact]
    public async Task A_link_deluge_already_holds_is_followed_by_the_hash_in_its_message_not_blocklisted()
    {
        var handler = new DelugeHandler(RecordedDuplicate(), sessionHashes: [Hash]);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), Link(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Equal(Hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.IsDuplicateFollow);
    }

    [Fact]
    public async Task A_magnet_deluge_already_holds_is_followed_when_it_answers_a_bare_null_as_older_versions_do()
    {
        var handler = new DelugeHandler(NullResultNoError(), sessionHashes: [Hash]);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), Magnet(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("already holds this release", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_add_result_for_a_hash_deluge_does_not_hold_stays_a_failure()
    {
        var handler = new DelugeHandler(NullResultNoError(), sessionHashes: []);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), Magnet(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("did not add the release", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #874/#942, cleanup sheet B2: an explicit RPC error object is Deluge looking at the exact link or file it was handed and refusing
    /// it outright, in the same request. Deluge 2.2.0's recorded refusal of a file that is not a torrent.
    /// </summary>
    [Fact]
    public async Task An_explicit_add_error_is_read_as_a_dead_release()
    {
        var handler = new DelugeHandler(RecordedReplies.Deluge("add-refused").Response("core.add_torrent_file"), sessionHashes: []);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), Link(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("decoding filedump failed", result.Message, StringComparison.Ordinal);
    }

    private static DownloadClientItem Client()
        => new(
            "client-1", "Deluge", "deluge", "localhost", 8112, null, "secret", "http://localhost:8112/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Deluge 2.2.0 as recorded: its sign-in, the add's own reply, and its session state holding the given hashes.</summary>
    private sealed class DelugeHandler(HttpResponseMessage addReply, IReadOnlyList<string> sessionHashes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("core.add_torrent_", StringComparison.Ordinal))
            {
                return addReply;
            }

            if (body.Contains("core.get_session_state", StringComparison.Ordinal))
            {
                return RecordedReplies.Json(RecordedReplies.Deluge("settings").Body(
                    "core.get_session_state", ("result", new System.Text.Json.Nodes.JsonArray(sessionHashes.Select(hash => (System.Text.Json.Nodes.JsonNode?)hash).ToArray()))));
            }

            return body.Contains("auth.login", StringComparison.Ordinal) ? RecordedReplies.Deluge("sign-in").Response("auth.login")
                : body.Contains("web.connected", StringComparison.Ordinal) ? RecordedReplies.Deluge("sign-in").Response("web.connected")
                : RecordedReplies.Deluge("label-set").Response("label.set_torrent");
        }
    }
}
