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
    public async Task A_grab_whose_hash_Deluno_could_not_know_and_uTorrent_never_shows_is_a_failure_not_a_success_with_no_id()
    {
        // #1062: uTorrent's add answers only with a build number, so a link
        // whose hash Deluno never learned and that never surfaces in the list
        // names nothing. It used to come back as a success with a null id that
        // could never be followed; it is now a plain failure.
        var handler = new SequencedHandler(beforeHashes: """{"torrents":[]}""", afterHashes: """{"torrents":[]}""");

        var result = await Grab(handler, mediaType: "tv");

        Assert.False(result.Succeeded);
        Assert.Contains("could not tell which torrent", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.SentPaths, path => path.Contains("action=setprops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grab_whose_hash_is_known_is_followed_by_it_even_when_uTorrent_is_slow_to_list_it()
    {
        const string hash = "abcdef0123456789abcdef0123456789abcdef01";
        var handler = new SequencedHandler(beforeHashes: """{"torrents":[]}""", afterHashes: """{"torrents":[]}""");

        var result = await Grab(handler, mediaType: "tv", knownHash: hash);

        Assert.True(result.Succeeded);
        Assert.Equal(hash, result.ExternalId);
        Assert.DoesNotContain(handler.SentPaths, path => path.Contains("action=setprops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grab_whose_hash_is_known_ignores_an_unrelated_torrent_added_at_the_same_moment()
    {
        // The diff is only a confirmation: another torrent appearing is never
        // mistaken for this grab.
        const string hash = "abcdef0123456789abcdef0123456789abcdef01";
        var handler = new SequencedHandler(
            beforeHashes: """{"torrents":[]}""",
            afterHashes: $$"""{"torrents":[["9999999999999999999999999999999999999999"],["{{hash.ToUpperInvariant()}}"]]}""");

        var result = await Grab(handler, mediaType: "tv", knownHash: hash);

        Assert.Equal(hash, result.ExternalId);
        Assert.Contains(handler.SentPaths, path => path.Contains("action=setprops", StringComparison.Ordinal)
            && path.Contains($"hash={hash.ToUpperInvariant()}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_torrents_appearing_at_once_with_no_known_hash_name_nothing()
    {
        var handler = new SequencedHandler(
            beforeHashes: """{"torrents":[]}""",
            afterHashes: """{"torrents":[["aaaa1111"],["bbbb2222"]]}""");

        var result = await Grab(handler, mediaType: "tv");

        Assert.False(result.Succeeded);
        Assert.Null(result.ExternalId);
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

    private static Task<DownloadClientGrabResult> Grab(HttpMessageHandler handler, string mediaType, string? knownHash = null)
        => new UTorrentDownloadClient(() => handler).GrabAsync(
            CreateClient(),
            new DownloadClientGrabRequest(
                "The.Show.S01E01.2023.720p.WEB-DL",
                "http://indexer.test/download/1",
                mediaType,
                null,
                "Lab Torznab",
                "dispatch-1")
            {
                KnownInfoHash = knownHash
            },
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
