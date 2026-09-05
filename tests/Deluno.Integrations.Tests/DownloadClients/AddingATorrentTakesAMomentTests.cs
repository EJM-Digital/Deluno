using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A torrent that has not appeared yet is not a torrent the client already had.
///
/// <para>qBittorrent adds by URL asynchronously: it answers <c>Ok.</c> as soon
/// as it has accepted the job, then fetches the `.torrent` and only afterwards
/// lists the infohash. The adapter compared its hashes immediately before and
/// immediately after the POST, so it asked before the answer existed.</para>
///
/// <para>Every grab of a genuinely new torrent was therefore reported as
/// <i>"it already holds this release"</i>. Measured on the lab rig on
/// 2026-09-05: qBittorrent held nothing, Deluno grabbed, recorded the dispatch
/// failed — and qBittorrent then held the torrent. The release had arrived, the
/// film stayed Missing, and its page explained a duplicate that never
/// existed.</para>
///
/// <para>That misreading was the first domino. It produced the blocker, which
/// offered the force, which asked the client to forget a torrent that was there
/// because the add had in fact worked.</para>
/// </summary>
public sealed class AddingATorrentTakesAMomentTests
{
    [Fact]
    public async Task A_torrent_that_appears_a_moment_later_is_a_successful_grab()
    {
        var infoCalls = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("auth/login", StringComparison.Ordinal))
            {
                return Text("Ok.");
            }

            if (request.RequestUri!.AbsolutePath.Contains("torrents/info", StringComparison.Ordinal))
            {
                infoCalls++;
                // Empty before the add, and for the first look after it —
                // which is exactly how a real client behaves while it fetches
                // the .torrent from the URL.
                return Json(infoCalls <= 2 ? "[]" : """[{"hash":"abc123","name":"Arrival"}]""");
            }

            return Text("Ok.");
        });

        var result = await new QbittorrentDownloadClient(() => handler)
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        // And the hash it just took is recorded, which is only possible because
        // the check waited for it. Without this the dispatch carried no queue
        // item id and nothing downstream could follow the download.
        Assert.Equal("abc123", result.ExternalId);
    }

    /// <summary>
    /// And the check it replaced still has to work: a client that genuinely
    /// keeps the copy it already has must still be reported, or Deluno waits
    /// for a download that will never start.
    /// </summary>
    [Fact]
    public async Task A_client_that_never_adds_anything_is_still_reported_as_holding_it()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("torrents/info", StringComparison.Ordinal)
                ? Json("""[{"hash":"already-here","name":"Arrival"}]""")
                : Text("Ok."));

        var result = await new QbittorrentDownloadClient(() => handler)
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("already holds this release", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_release_url_is_still_a_failure()
    {
        // The login answers Ok.; only the add refuses.
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("torrents/info", StringComparison.Ordinal)
                ? Json("[]")
                : request.RequestUri!.AbsolutePath.Contains("auth/login", StringComparison.Ordinal)
                    ? Text("Ok.")
                    : Text("Fails."));

        var result = await new QbittorrentDownloadClient(() => handler)
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DownloadClientGrabRequest Request()
        => new("Arrival.2016.1080p", "http://indexer/arrival.torrent", "movies", "movies", "Lab Torznab");

    private static DownloadClientItem Client()
        => new(
            "client-1",
            "qbittorrent",
            "qbittorrent",
            "localhost",
            8080,
            "user",
            "secret",
            "http://localhost:8080/",
            "movies",
            "tv",
            null,
            1,
            true,
            "healthy",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
