using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A µTorrent grab used to send no label at all (#728): `action=add-url`/
/// `add-file` take no category parameter of their own, so
/// <c>InferMediaType</c> then defaulted every unlabeled item to "movies",
/// silently misclassifying every TV grab. A grab now diffs the hash list the
/// same way qBittorrent's own grab does (#606) to learn the new torrent's
/// hash, then sends a follow-up <c>setprops</c> call to label it with the
/// category Deluno resolves for the release.
/// </summary>
public sealed class UTorrentGrabLabelTests
{
    [Fact]
    public async Task A_tv_grab_labels_the_new_torrent_tv()
    {
        var handler = new SequencedHandler(beforeHashes: """{"torrents":[]}""", afterHashes: """{"torrents":[["aaaa1111"]]}""");

        var result = await Grab(handler, mediaType: "tv");

        Assert.True(result.Succeeded);
        Assert.Contains(handler.SentPaths, path => path.Contains("action=setprops", StringComparison.Ordinal)
            && path.Contains("hash=aaaa1111", StringComparison.Ordinal)
            && path.Contains("s=label", StringComparison.Ordinal)
            && path.Contains("v=tv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_movie_grab_labels_the_new_torrent_movies()
    {
        var handler = new SequencedHandler(beforeHashes: """{"torrents":[]}""", afterHashes: """{"torrents":[["aaaa1111"]]}""");

        var result = await Grab(handler, mediaType: "movies");

        Assert.True(result.Succeeded);
        Assert.Contains(handler.SentPaths, path => path.Contains("action=setprops", StringComparison.Ordinal)
            && path.Contains("v=movies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grab_that_never_surfaces_a_new_hash_still_reports_as_sent()
    {
        // qBittorrent's own honesty rule (#606) is about the client silently
        // taking nothing; here the release URL was accepted and uTorrent
        // simply has not surfaced the new hash in the poll window yet. That
        // must not fail the grab, only skip the label call.
        var handler = new SequencedHandler(beforeHashes: """{"torrents":[]}""", afterHashes: """{"torrents":[]}""");

        var result = await Grab(handler, mediaType: "tv");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(handler.SentPaths, path => path.Contains("action=setprops", StringComparison.Ordinal));
    }

    /// <summary>
    /// #877/#911, cleanup sheet B3: uTorrent already holding the release is
    /// nobody's fault — Deluno should follow the copy it has, the way
    /// qBittorrent, Deluge and rTorrent already do, rather than reporting a
    /// generic "sent" with no external id the queue can never follow up on.
    /// </summary>
    [Fact]
    public async Task A_magnet_utorrent_already_holds_is_followed_instead_of_reported_generically()
    {
        const string hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
        var handler = new SequencedHandler(
            beforeHashes: $$"""{"torrents":[["{{hash}}"]]}""",
            afterHashes: $$"""{"torrents":[["{{hash}}"]]}""");

        var result = await new UTorrentDownloadClient(() => handler).GrabAsync(
            CreateClient(),
            new DownloadClientGrabRequest(
                "Example.Movie.2024.1080p", $"magnet:?xt=urn:btih:{hash}&dn=Example", "movies", null, "Lab Torznab"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("already holds this release", result.Message, StringComparison.Ordinal);
    }

    private static Task<DownloadClientGrabResult> Grab(HttpMessageHandler handler, string mediaType)
        => new UTorrentDownloadClient(() => handler).GrabAsync(
            CreateClient(),
            new DownloadClientGrabRequest(
                "The.Show.S01E01.2023.720p.WEB-DL",
                "http://indexer.test/download/1",
                mediaType,
                null,
                "Lab Torznab",
                "dispatch-1"),
            CancellationToken.None);

    private static DownloadClientItem CreateClient()
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>
    /// Answers the token handshake, the add call, and `list=1` — with the
    /// list changing after the add is sent, same shape as qBittorrent's own
    /// grab-honesty handler.
    /// </summary>
    private sealed class SequencedHandler(string beforeHashes, string afterHashes) : HttpMessageHandler
    {
        private bool _added;
        public List<string> SentPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            SentPaths.Add(uri);

            if (uri.Contains("token.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<div id='token'>test-token</div>", Encoding.UTF8, "text/html")
                });
            }

            if (uri.Contains("action=add-url", StringComparison.Ordinal))
            {
                _added = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }

            if (uri.Contains("list=1", StringComparison.Ordinal))
            {
                var body = _added ? afterHashes : beforeHashes;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }

            if (uri.Contains("action=setprops", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
