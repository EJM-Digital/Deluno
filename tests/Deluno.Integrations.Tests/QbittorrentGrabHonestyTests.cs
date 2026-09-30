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

    /// <summary>
    /// #877, cleanup sheet B3: qBittorrent already holding a release is
    /// nobody's fault. Usenet clients already follow the existing item instead
    /// of failing the grab (#663); a magnet whose infohash Deluno can read and
    /// already sees in the list must do the same, not report a failed download
    /// and leave a person to remove it by hand.
    /// </summary>
    [Fact]
    public async Task A_release_qbittorrent_already_holds_by_a_known_hash_is_followed_not_failed()
    {
        const string hash = "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111";
        var handler = new SequencedHandler(
            beforeHashes: $$"""[{"hash":"{{hash}}"}]""",
            addBody: "Ok.",
            afterHashes: $$"""[{"hash":"{{hash}}"}]""");

        var result = await Grab(handler, downloadUrl: $"magnet:?xt=urn:btih:{hash}");

        Assert.True(result.Succeeded);
        Assert.Equal("sent", result.Status);
        Assert.Equal(hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("already holds this release", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("following", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// qBittorrent 5.1 and later answer 409 when nothing was added. For a
    /// torrent it already holds that is the #877 case, so Deluno follows the
    /// copy instead of treating the client as unreachable (#1062).
    /// </summary>
    [Fact]
    public async Task A_409_for_a_torrent_qbittorrent_already_holds_is_followed()
    {
        const string hash = "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222";
        var handler = new SequencedHandler(
            beforeHashes: $$"""[{"hash":"{{hash}}"}]""",
            addBody: "Fails.",
            afterHashes: $$"""[{"hash":"{{hash}}"}]""",
            addStatus: HttpStatusCode.Conflict);

        var result = await Grab(handler, downloadUrl: $"magnet:?xt=urn:btih:{hash}");

        Assert.True(result.Succeeded);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_409_for_a_new_torrent_is_a_refused_release_not_an_unreachable_client()
    {
        const string hash = "cccc3333cccc3333cccc3333cccc3333cccc3333";
        var handler = new SequencedHandler(
            beforeHashes: "[]",
            addBody: "Fails.",
            afterHashes: "[]",
            addStatus: HttpStatusCode.Conflict);

        var result = await Grab(handler, downloadUrl: $"magnet:?xt=urn:btih:{hash}");

        Assert.False(result.Succeeded);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(409, result.ResponseCode);
    }

    /// <summary>
    /// From 5.1 a successful add answers JSON, not "Ok."; a torrent that is
    /// slow to appear in the list must not be failed as a silent rejection.
    /// </summary>
    [Fact]
    public async Task A_json_answer_that_took_the_torrent_is_not_failed_when_the_list_is_slow()
    {
        const string hash = "dddd4444dddd4444dddd4444dddd4444dddd4444";
        var handler = new SequencedHandler(
            beforeHashes: "[]",
            addBody: $$"""{"success_count":1,"failure_count":0,"pending_count":0,"added_torrent_ids":["{{hash}}"]}""",
            afterHashes: "[]");
        var delay = QbittorrentDownloadClient.ExtendedConfirmationDelay;
        QbittorrentDownloadClient.ExtendedConfirmationDelay = TimeSpan.Zero;
        try
        {
            var result = await Grab(handler, downloadUrl: $"magnet:?xt=urn:btih:{hash}");

            Assert.True(result.Succeeded);
            Assert.Equal(hash, result.ExternalId, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            QbittorrentDownloadClient.ExtendedConfirmationDelay = delay;
        }
    }

    [Theory]
    [InlineData("Ok.", true)]
    [InlineData("Fails.", false)]
    [InlineData("""{"success_count":1,"failure_count":0,"pending_count":0}""", true)]
    [InlineData("""{"success_count":0,"failure_count":0,"pending_count":1}""", true)]
    [InlineData("""{"success_count":0,"failure_count":1,"pending_count":0}""", false)]
    [InlineData("""{"success_count":0,"failure_count":0,"pending_count":0,"added_torrent_ids":["EEEE5555EEEE5555EEEE5555EEEE5555EEEE5555"]}""", true)]
    [InlineData("{not json", false)]
    public void What_counts_as_qbittorrent_taking_the_torrent(string payload, bool taken)
        => Assert.Equal(taken, QbittorrentDownloadClient.AnswerSaysItWasTaken(payload, "eeee5555eeee5555eeee5555eeee5555eeee5555"));

    // ------------------------------------------------------------------ helpers

    private static Task<DownloadClientGrabResult> Grab(HttpMessageHandler handler, string? downloadUrl = null)
        => new QbittorrentDownloadClient(() => handler).GrabAsync(
            CreateClient(),
            new DownloadClientGrabRequest(
                "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
                downloadUrl ?? "http://indexer.test/download/1",
                "movies",
                "movies",
                "Lab Torznab",
                "dispatch-1"),
            CancellationToken.None);

    /// <summary>
    /// Answers the login, the two torrent-list reads and the add. The list
    /// answer changes after the add, which is the whole point.
    /// </summary>
    private sealed class SequencedHandler(string beforeHashes, string addBody, string? afterHashes, HttpStatusCode addStatus = HttpStatusCode.OK) : HttpMessageHandler
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
                return Task.FromResult(new HttpResponseMessage(addStatus) { Content = new StringContent(addBody) });
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
            false,
            null,
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
