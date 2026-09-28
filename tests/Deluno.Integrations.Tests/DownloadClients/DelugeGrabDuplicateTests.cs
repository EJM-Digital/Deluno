using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #877, cleanup sheet B3: Deluge's add calls answer with a null result both
/// when a release is malformed and when Deluge already holds this exact
/// torrent -- nothing in the RPC tells the two apart. Deluno now checks a
/// magnet's own hash against Deluge's session state and follows the copy
/// already there, the way qBittorrent and the Usenet clients already do,
/// instead of reporting a failed grab.
/// </summary>
public sealed class DelugeGrabDuplicateTests
{
    private const string Hash = "abcd1234abcd1234abcd1234abcd1234abcd1234";

    [Fact]
    public async Task A_magnet_deluge_already_holds_is_followed_instead_of_failed()
    {
        var handler = new DelugeHandler(addResult: "null", sessionHashes: [Hash]);
        var request = new DownloadClientGrabRequest(
            "Example.Movie.2024.1080p", $"magnet:?xt=urn:btih:{Hash}&dn=Example", "movies", "movies", "Fixture indexer");

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("already holds this release", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_add_result_for_a_hash_deluge_does_not_hold_stays_a_failure()
    {
        var handler = new DelugeHandler(addResult: "null", sessionHashes: []);
        var request = new DownloadClientGrabRequest(
            "Example.Movie.2024.1080p", $"magnet:?xt=urn:btih:{Hash}&dn=Example", "movies", "movies", "Fixture indexer");

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("did not add the release", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #874/#942, cleanup sheet B2: an explicit RPC error object is Deluge
    /// looking at the exact link or file it was handed and refusing it
    /// outright, in the same request — distinct from the ambiguous null
    /// result with no error above, which is just as likely an already-held
    /// duplicate Deluno could not verify. Read as a dead release the way
    /// qBittorrent's "Fails." and NZBGet's RPC error already are.
    /// </summary>
    [Fact]
    public async Task An_explicit_add_error_is_read_as_a_dead_release()
    {
        var handler = new DelugeHandler(addResult: "null", sessionHashes: [], addErrorMessage: "invalid torrent file");
        var request = new DownloadClientGrabRequest(
            "Example.Movie.2024.1080p", "http://indexer.test/download/1.torrent", "movies", "movies", "Fixture indexer");

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client(), request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("invalid torrent file", result.Message, StringComparison.Ordinal);
    }

    private static DownloadClientItem Client()
        => new(
            "client-1", "Deluge", "deluge", "localhost", 8112, null, "secret", "http://localhost:8112/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelugeHandler(string addResult, IReadOnlyList<string> sessionHashes, string? addErrorMessage = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var error = addErrorMessage is null ? "null" : $$"""{"message":"{{addErrorMessage}}"}""";
            var answer = body.Contains("core.add_torrent_", StringComparison.Ordinal)
                ? $$"""{"result":{{addResult}},"error":{{error}},"id":2}"""
                : body.Contains("core.get_session_state", StringComparison.Ordinal)
                    ? $$"""{"result":[{{string.Join(",", sessionHashes.Select(hash => $"\"{hash}\""))}}],"error":null,"id":9}"""
                    : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
