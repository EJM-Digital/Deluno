using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests;

/// <summary>
/// A grab reports what qBittorrent did, not that it was asked.
///
/// <para><b>Seen on the lab.</b> Deluno reported
/// <c>dispatchStatus: "sent"</c> and "Release URL sent to qBittorrent" while
/// qBittorrent quietly kept a torrent it already had — one stuck in
/// <c>missingFiles</c> from an earlier run — and added nothing. Deluno then
/// tracked that dead torrent as downloading, and the queue reported it as in
/// flight indefinitely.</para>
///
/// <para>The cause is that <c>/api/v2/torrents/add</c> answers <c>200</c> with
/// a body of <c>Ok.</c> or <c>Fails.</c>, and a torrent whose infohash it
/// already holds is not an error to it — it keeps the one it has and says
/// <c>Ok.</c> Checking only the status code turns both of those into success.
/// </para>
///
/// <para>So the grab now compares what qBittorrent was holding before and
/// after. Silence about a blocker is the thing being fixed: a download that
/// will never start has to say so at the moment it is asked for, not become a
/// queue row nobody can explain.</para>
/// </summary>
public sealed class QbittorrentGrabHonestyTests
{
    [Fact]
    public async Task A_torrent_qbittorrent_actually_added_is_a_send()
    {
        var handler = new SequencedHandler(
            beforeHashes: "[]",
            addBody: "Ok.",
            afterHashes: """[{"hash":"aaaa1111"}]""");

        var result = await Grab(handler);

        Assert.True(result.Succeeded);
        Assert.Equal("sent", result.Status);
    }

    /// <summary>
    /// The lab case: 200, "Ok.", and nothing added.
    /// </summary>
    [Fact]
    public async Task A_release_qbittorrent_already_holds_is_not_reported_as_sent()
    {
        var handler = new SequencedHandler(
            beforeHashes: """[{"hash":"aaaa1111"}]""",
            addBody: "Ok.",
            afterHashes: """[{"hash":"aaaa1111"}]""");

        var result = await Grab(handler);

        Assert.False(result.Succeeded);
        // The message has to say what to do about it, not merely that something
        // is wrong.
        Assert.Contains("already holds this release", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("force a re-download", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// qBittorrent's own refusal, which also arrives as HTTP 200.
    /// </summary>
    [Fact]
    public async Task A_refusal_in_the_body_is_a_failure_despite_the_200()
    {
        var handler = new SequencedHandler(
            beforeHashes: "[]",
            addBody: "Fails.",
            afterHashes: "[]");

        var result = await Grab(handler);

        Assert.False(result.Succeeded);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(200, result.ResponseCode);
    }

    /// <summary>
    /// A client that will not answer the follow-up question is not evidence
    /// that the add failed. "I could not read the list" and "the list did not
    /// change" lead to opposite conclusions, and only one of them is a failure.
    /// </summary>
    [Fact]
    public async Task An_unreadable_torrent_list_does_not_turn_a_send_into_a_failure()
    {
        var handler = new SequencedHandler(
            beforeHashes: "[]",
            addBody: "Ok.",
            afterHashes: null);

        var result = await Grab(handler);

        Assert.True(result.Succeeded);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<DownloadClientGrabResult> Grab(HttpMessageHandler handler)
        => new QbittorrentDownloadClient(() => handler).GrabAsync(
            CreateClient(),
            new DownloadClientGrabRequest(
                "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
                "http://indexer.test/download/1",
                "movies",
                "movies",
                "Lab Torznab",
                "dispatch-1"),
            CancellationToken.None);

    /// <summary>
    /// Answers the login, the two torrent-list reads and the add. The list
    /// answer changes after the add, which is the whole point.
    /// </summary>
    private sealed class SequencedHandler(string beforeHashes, string addBody, string? afterHashes) : HttpMessageHandler
    {
        private bool _added;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("auth/login", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Text("Ok."));
            }

            if (path.Contains("torrents/add", StringComparison.OrdinalIgnoreCase))
            {
                _added = true;
                return Task.FromResult(Text(addBody));
            }

            if (path.Contains("torrents/info", StringComparison.OrdinalIgnoreCase))
            {
                if (!_added)
                {
                    return Task.FromResult(Json(beforeHashes));
                }

                return afterHashes is null
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                    : Task.FromResult(Json(afterHashes));
            }

            return Task.FromResult(Text("Ok."));
        }

        private static HttpResponseMessage Text(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body) };

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
    }

    private static DownloadClientItem CreateClient()
        => new(
            "client-1",
            "qBittorrent",
            "qbittorrent",
            "localhost",
            8080,
            null,
            null,
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
}
