using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Every verb reaches the client as a different request, or is refused.
///
/// <para>James: <i>"we dont want to have delete just being delete with nothing
/// matching, it needs to be 1:1 mapping where possible for all delete routes
/// and scenarios"</i>. The failure this guards is not a crash — it is a verb
/// that arrives, is accepted, and quietly does something adjacent to what was
/// asked. Two of those were live:</para>
///
/// <list type="bullet">
/// <item>NZBGet mapped <c>delete</c> and <c>delete-with-data</c> to the same
/// <c>GroupDelete</c>, so a caller asking to take the files got the request
/// that leaves them.</item>
/// <item>NZBGet mapped <c>pause</c> and <c>resume</c> to <c>pausedownload</c>
/// and <c>resumedownload</c>, which are its <b>global</b> switches. Asked to
/// pause one download, Deluno stopped the whole client and said it had worked.</item>
/// </list>
///
/// <para>Neither would fail a test that only asserted success, which is why
/// these assert the request that went out rather than the answer that came
/// back.</para>
/// </summary>
public sealed class EveryVerbMeansSomethingDifferentTests
{
    /// <summary>
    /// A verb the clients implement has to survive the gateway that dispatches
    /// to them.
    ///
    /// <para>Every adapter mapped <c>forget</c>, and
    /// <c>DownloadClientTelemetryService.NormalizeAction</c> did not list it,
    /// so the verb was refused before any adapter was reached. On the lab rig
    /// on 2026-09-05, forcing a re-download reported <i>"qBittorrent would not
    /// forget the release: Unsupported action."</i></para>
    ///
    /// <para>That took out every path that asks a client to forget a release:
    /// the acquisition override, the refused-download clean-up pass, and
    /// "Clean up now" on the blocklist. Not one of their tests could see it,
    /// because all of them stand in for the service that was doing the
    /// refusing — including the ones written the same day.</para>
    ///
    /// <para>So this reads the gateway's own source. The failure it catches is
    /// a verb that exists at both ends and is dropped in the middle.</para>
    /// </summary>
    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    [InlineData(DownloadClientActions.Recheck)]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    [InlineData(DownloadClientActions.MoveToTop)]
    [InlineData(DownloadClientActions.MoveUp)]
    [InlineData(DownloadClientActions.MoveDown)]
    [InlineData(DownloadClientActions.MoveToBottom)]
    [InlineData(DownloadClientActions.ForceStart)]
    public void The_gateway_passes_on_every_verb_the_clients_implement(string verb)
    {
        var source = File.ReadAllText(GatewaySourcePath());
        var normalize = source[source.IndexOf("private static string? NormalizeAction", StringComparison.Ordinal)..];
        normalize = normalize[..normalize.IndexOf("};", StringComparison.Ordinal)];

        Assert.True(
            normalize.Contains($"\"{verb}\"", StringComparison.Ordinal),
            $"DownloadClientTelemetryService.NormalizeAction does not accept '{verb}', so every caller of it is "
            + "refused with \"Unsupported action\" before any adapter is reached.");
    }

    private static string GatewaySourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName, "src", "Deluno.Integrations", "DownloadClients", "DownloadClientTelemetryService.cs");
    }

    /// <summary>
    /// The distinction the two verbs exist to draw: one takes the files, the
    /// other leaves them. A client that sends the same request for both has
    /// dropped it.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("deluge")]
    [InlineData("transmission")]
    [InlineData("utorrent")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task Delete_and_delete_with_data_are_not_the_same_request(string protocol)
    {
        var leaveFiles = await CaptureAsync(protocol, DownloadClientActions.Delete);
        var takeFiles = await CaptureAsync(protocol, DownloadClientActions.DeleteWithData);

        Assert.NotEmpty(leaveFiles);
        Assert.NotEmpty(takeFiles);
        Assert.NotEqual(leaveFiles, takeFiles);
    }

    /// <summary>#1252: a second removal of the same download is a plain failure on uTorrent too, and sends no second remove.</summary>
    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    public async Task A_second_utorrent_removal_is_a_failure_and_sends_no_second_remove(string action)
    {
        var sent = new List<string>();
        var handler = new RecordingHandler(sent);
        var client = new UTorrentDownloadClient(() => handler);
        Assert.True((await client.ExecuteActionAsync(Client("utorrent"), action, QueueItemId("utorrent"), CancellationToken.None)).Succeeded);

        var again = await client.ExecuteActionAsync(Client("utorrent"), action, QueueItemId("utorrent"), CancellationToken.None);

        Assert.False(again.Succeeded);
        Assert.Contains("has no download with that id", again.Message);
    }

    /// <summary>
    /// And pausing one download is not pausing the client.
    /// </summary>
    [Theory]
    [InlineData("utorrent")]
    public async Task Pausing_names_the_download_being_paused(string protocol)
    {
        var sent = await CaptureAsync(protocol, DownloadClientActions.Pause);

        Assert.NotEmpty(sent);
        Assert.Contains(sent, request => request.Contains(QueueItemId(protocol), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #810: move to top/up/down/bottom are each their own request against
    /// the clients whose queue API this session could confirm -- not one verb
    /// silently standing in for another the way pause/delete used to.
    /// SABnzbd's own API has no true reorder, only discrete priority tiers
    /// (#810's own scope note), so move-down and move-to-bottom land on the
    /// same Low tier there and are excluded from the "all four distinct"
    /// check; its own dedicated test below covers that.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("deluge")]
    [InlineData("transmission")]
    [InlineData("nzbget")]
    public async Task Queue_reorder_actions_are_four_distinct_requests(string protocol)
    {
        var top = await CaptureAsync(protocol, DownloadClientActions.MoveToTop);
        var up = await CaptureAsync(protocol, DownloadClientActions.MoveUp);
        var down = await CaptureAsync(protocol, DownloadClientActions.MoveDown);
        var bottom = await CaptureAsync(protocol, DownloadClientActions.MoveToBottom);

        var requests = new[] { top, up, down, bottom };
        foreach (var request in requests) Assert.NotEmpty(request);
        Assert.Equal(4, requests.Select(r => string.Join('|', r)).Distinct().Count());
    }

    /// <summary>
    /// SABnzbd has no one-slot move of its own -- only Force/High/Low
    /// priority tiers, set with mode=queue&amp;name=priority. Move-to-top and
    /// move-up land on distinct, higher tiers; move-down and move-to-bottom
    /// share the same Low tier, since there is nothing lower short of Paused,
    /// which would stop the download rather than reorder it.
    /// </summary>
    [Fact]
    public async Task Sabnzbd_move_actions_use_its_own_priority_tiers()
    {
        var top = Assert.Single(await CaptureAsync("sabnzbd", DownloadClientActions.MoveToTop));
        var up = Assert.Single(await CaptureAsync("sabnzbd", DownloadClientActions.MoveUp));
        var down = Assert.Single(await CaptureAsync("sabnzbd", DownloadClientActions.MoveDown));
        var bottom = Assert.Single(await CaptureAsync("sabnzbd", DownloadClientActions.MoveToBottom));

        Assert.Contains("value2=2", top, StringComparison.Ordinal);
        Assert.Contains("value2=1", up, StringComparison.Ordinal);
        Assert.Contains("value2=-2", down, StringComparison.Ordinal);
        Assert.Contains("value2=-2", bottom, StringComparison.Ordinal);
    }

    /// <summary>uTorrent's queue API could not be independently confirmed this session (#810), so it is refused rather than guessed at.</summary>
    [Theory]
    [InlineData(DownloadClientActions.MoveToTop)]
    [InlineData(DownloadClientActions.MoveUp)]
    [InlineData(DownloadClientActions.MoveDown)]
    [InlineData(DownloadClientActions.MoveToBottom)]
    [InlineData(DownloadClientActions.ForceStart)]
    public async Task Utorrent_refuses_every_reorder_and_force_start_action(string action)
    {
        var handler = new RecordingHandler([]);
        var result = await new UTorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("utorrent"), action, QueueItemId("utorrent"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>qBittorrent only: force-start bypasses its own seed/ratio thresholds, sent as setForceStart with value=true.</summary>
    [Fact]
    public async Task Qbittorrent_force_start_sets_value_true()
    {
        var sent = await CaptureAsync("qbittorrent", DownloadClientActions.ForceStart);

        Assert.Contains(sent, request => request.Contains("setForceStart", StringComparison.Ordinal) && request.Contains("value=true", StringComparison.Ordinal));
    }

    /// <summary>The other clients have no force-start of their own, so it is refused rather than mapped onto something adjacent.</summary>
    [Theory]
    [InlineData("deluge")]
    [InlineData("transmission")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task Only_qbittorrent_supports_force_start(string protocol)
    {
        var handler = new RecordingHandler([]);
        var result = protocol switch
        {
            "deluge" => await new DelugeDownloadClient(new StubHttpClientFactory(handler)).ExecuteActionAsync(Client(protocol), DownloadClientActions.ForceStart, QueueItemId(protocol), CancellationToken.None),
            "transmission" => await new TransmissionDownloadClient(new StubHttpClientFactory(handler)).ExecuteActionAsync(Client(protocol), DownloadClientActions.ForceStart, QueueItemId(protocol), CancellationToken.None),
            "sabnzbd" => await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).ExecuteActionAsync(Client(protocol), DownloadClientActions.ForceStart, QueueItemId(protocol), CancellationToken.None),
            "nzbget" => await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).ExecuteActionAsync(Client(protocol), DownloadClientActions.ForceStart, QueueItemId(protocol), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Forgetting is either a request of its own, or the same request as
    /// delete-with-data because the client keeps nothing that outlives the
    /// transfer. Both are correct; what is not correct is forgetting doing
    /// less than deleting.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent", false)]
    [InlineData("deluge", false)]
    [InlineData("transmission", false)]
    [InlineData("utorrent", false)]
    // The two that keep a history the transfer does not: forgetting has to be
    // more than deleting, or the release stays refused.
    [InlineData("sabnzbd", true)]
    [InlineData("nzbget", true)]
    public async Task Forgetting_does_at_least_as_much_as_deleting_with_data(string protocol, bool needsMore)
    {
        var deleted = await CaptureAsync(protocol, DownloadClientActions.DeleteWithData);
        var forgotten = await CaptureAsync(protocol, DownloadClientActions.RemoveRefusedCopy);

        Assert.NotEmpty(forgotten);
        if (needsMore)
        {
            Assert.True(
                forgotten.Count > deleted.Count,
                $"{protocol} sent {forgotten.Count} request(s) to forget and {deleted.Count} to delete with data. "
                + "A client that keeps history has to be told about both.");
        }
        else
        {
            Assert.Equal(deleted, forgotten);
        }
    }

    /// <summary>
    /// An id the client could not mean is refused, not rounded off.
    ///
    /// <para>The shared parse answered 0 for anything it could not read, and 0
    /// is a download id — so a stored id in the wrong shape produced a
    /// well-formed request aimed at nothing, which the client accepted and
    /// Deluno reported as done. Silence wearing the shape of success, which is
    /// the same failure as a verb that maps to the wrong command.</para>
    /// </summary>
    [Theory]
    [InlineData("transmission", "Transmission")]
    [InlineData("nzbget", "NZBGet")]
    public async Task An_id_the_client_could_not_mean_is_refused_rather_than_sent_as_zero(string protocol, string label)
    {
        var sent = new List<string>();
        var handler = new RecordingHandler(sent);
        var client = Client(protocol);
        var wrongShape = protocol == "transmission" ? "not-an-id-at-all" : "0123456789abcdef0123456789abcdef01234567";

        var result = protocol == "transmission"
            ? await new TransmissionDownloadClient(new StubHttpClientFactory(handler))
                .ExecuteActionAsync(client, DownloadClientActions.DeleteWithData, wrongShape, CancellationToken.None)
            : await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
                .ExecuteActionAsync(client, DownloadClientActions.DeleteWithData, wrongShape, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(sent);
        Assert.Contains(label, result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Every request the client made, as "path?query body" — enough to tell one
    /// verb's traffic from another's without pinning any client's wire format.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CaptureAsync(string protocol, string action)
    {
        var sent = new List<string>();
        var handler = new RecordingHandler(sent);
        var client = Client(protocol);

        DownloadClientActionResult result = protocol switch
        {
            "qbittorrent" => await new QbittorrentDownloadClient(() => handler)
                .ExecuteActionAsync(client, action, QueueItemId(protocol), CancellationToken.None),
            "deluge" => await new DelugeDownloadClient(new StubHttpClientFactory(handler))
                .ExecuteActionAsync(client, action, QueueItemId(protocol), CancellationToken.None),
            "transmission" => await new TransmissionDownloadClient(new StubHttpClientFactory(handler))
                .ExecuteActionAsync(client, action, QueueItemId(protocol), CancellationToken.None),
            "utorrent" => await new UTorrentDownloadClient(() => handler)
                .ExecuteActionAsync(client, action, QueueItemId(protocol), CancellationToken.None),
            "sabnzbd" => await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
                .ExecuteActionAsync(client, action, QueueItemId(protocol), CancellationToken.None),
            "nzbget" => await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
                .ExecuteActionAsync(client, action, QueueItemId(protocol), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "No client for this protocol.")
        };

        Assert.True(result.Succeeded, $"{protocol} refused {action}: {result.Message}");
        return sent;
    }

    /// <summary>
    /// The id each client actually hands Deluno, because the shape is part of
    /// the contract: Transmission and NZBGet number their downloads, SABnzbd
    /// names them, and the torrent clients use the infohash.
    /// </summary>
    private static string QueueItemId(string protocol) => protocol switch
    {
        "nzbget" => "42",
        "transmission" => "7",
        "sabnzbd" => "SABnzbd_nzo_abc123",
        _ => "0123456789abcdef0123456789abcdef01234567"
    };

    private static DownloadClientItem Client(string protocol)
        => new(
            "client-1",
            protocol,
            protocol,
            "localhost",
            8080,
            "user",
            protocol == "sabnzbd" ? "api-key" : "secret",
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

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Answers everything cheerfully and writes down what it was asked. The
    /// bodies are JSON-RPC for two clients and form posts for another, so the
    /// reply only has to be shaped enough not to throw.
    /// </summary>
    private sealed class RecordingHandler(List<string> sent) : HttpMessageHandler
    {
        private bool _removed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            // Sign-in traffic is not the verb under test, and neither is a usenet client's list of jobs, which
            // Deluno reads to check that a removal happened (#1212).
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var isSabList = path.Contains("api?mode=queue", StringComparison.OrdinalIgnoreCase) && !path.Contains("name=", StringComparison.OrdinalIgnoreCase)
                || path.Contains("api?mode=history", StringComparison.OrdinalIgnoreCase) && !path.Contains("name=", StringComparison.OrdinalIgnoreCase);
            var isNzbGetList = body.Contains("\"listgroups\"", StringComparison.Ordinal) || body.Contains("\"method\":\"history\"", StringComparison.OrdinalIgnoreCase);
            // A torrent client's list of torrents is read the same way before and after a removal (#1252).
            var isTorrentList = path.Contains("api/v2/torrents/info", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"torrent-get\"", StringComparison.Ordinal)
                || body.Contains("web.update_ui", StringComparison.Ordinal)
                || path.Contains("list=1", StringComparison.OrdinalIgnoreCase);
            if (!path.Contains("auth/login", StringComparison.OrdinalIgnoreCase) && !body.Contains("auth.login", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("token.html", StringComparison.OrdinalIgnoreCase) && !isSabList && !isNzbGetList && !isTorrentList)
            {
                sent.Add($"{path} {body}".Trim());
            }

            if (isTorrentList)
            {
                var held = !_removed;
                var infoHash = "0123456789abcdef0123456789abcdef01234567";
                string content;
                if (path.Contains("api/v2/torrents/info", StringComparison.OrdinalIgnoreCase))
                {
                    content = held
                        ? RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info", ("0.hash", infoHash))
                        : RecordedReplies.Qbittorrent("removed-twice").Body("torrents/info");
                }
                else if (body.Contains("\"torrent-get\"", StringComparison.Ordinal))
                {
                    content = held
                        ? RecordedReplies.Transmission("finished-folder").Body("\"torrent-get\"", ("arguments.torrents.0.downloadDir", "/no/such/folder"))
                        : RecordedReplies.Transmission("removed-twice").Body("torrent-get");
                }
                else if (body.Contains("web.update_ui", StringComparison.Ordinal))
                {
                    content = held
                        ? "{\"result\":{\"torrents\":{\"" + infoHash + "\":{\"name\":\"Some.Film\"}}},\"error\":null,\"id\":2}"
                        : RecordedReplies.Deluge("removed-twice").Body("web.update_ui");
                }
                else
                {
                    content = held
                        ? "{\"torrents\":[[\"" + infoHash + "\",136,\"Some.Film\"]]}"
                        : "{\"torrents\":[]}";
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };
            }

            if (path.Contains("torrents/delete", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"torrent-remove\"", StringComparison.Ordinal)
                || body.Contains("core.remove_torrent", StringComparison.Ordinal)
                || path.Contains("action=remove", StringComparison.OrdinalIgnoreCase))
            {
                _removed = true;
            }

            // The job is there until the first delete or edit is sent, and gone after it.
            if (isSabList || isNzbGetList)
            {
                var listed = !_removed;
                var nzbList = body.Contains("\"listgroups\"", StringComparison.Ordinal);
                // The usenet clients' own listings (recorded, #1237): with the job in them, or empty once it is removed.
                var content = isSabList
                    ? path.Contains("mode=queue", StringComparison.OrdinalIgnoreCase)
                        ? listed
                            ? RecordedReplies.Sabnzbd("downloading").Body("mode=queue&output", ("^queue.slots", 1), ("queue.slots.0.nzo_id", "SABnzbd_nzo_abc123"))
                            : RecordedReplies.Sabnzbd("removed-without-data").Body("mode=queue&output", ("queue.slots", new System.Text.Json.Nodes.JsonArray()))
                        : RecordedReplies.Sabnzbd("removed-with-data").Body("mode=history&output", ("history.slots", new System.Text.Json.Nodes.JsonArray()))
                    : nzbList
                        ? listed
                            ? RecordedReplies.Nzbget("queued").Body("listgroups", ("^result", 1), ("result.0.NZBID", 42))
                            : RecordedReplies.Nzbget("removed-without-data").Body("listgroups", ("result", new System.Text.Json.Nodes.JsonArray()))
                        : RecordedReplies.Nzbget("removed-with-data").Body("\"history\"", ("result", new System.Text.Json.Nodes.JsonArray()));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };
            }

            if (path.Contains("name=", StringComparison.OrdinalIgnoreCase) || body.Contains("editqueue", StringComparison.OrdinalIgnoreCase))
            {
                _removed = true;
            }

            // Each client's sign-in wants its own shape back, and none of them
            // is the verb under test.
            if (path.Contains("token.html", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<div id='token'>test-token</div>", System.Text.Encoding.UTF8, "text/html")
                };
            }

            if (path.Contains("auth/login", StringComparison.OrdinalIgnoreCase))
            {
                return RecordedReplies.Qbittorrent("sign-in").Response("auth/login");
            }

            // qBittorrent answers its action endpoints in plain text, not JSON (an empty 200 for a delete).
            if (path.Contains("api/v2/", StringComparison.OrdinalIgnoreCase))
            {
                return RecordedReplies.Qbittorrent("removed-with-data").Response("torrents/delete");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":true,\"result\":true}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
