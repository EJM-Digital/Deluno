using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

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
    [InlineData(DownloadClientActions.Forget)]
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

    /// <summary>
    /// And pausing one download is not pausing the client.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("deluge")]
    [InlineData("transmission")]
    [InlineData("utorrent")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task Pausing_names_the_download_being_paused(string protocol)
    {
        var sent = await CaptureAsync(protocol, DownloadClientActions.Pause);

        Assert.NotEmpty(sent);
        Assert.Contains(sent, request => request.Contains(QueueItemId(protocol), StringComparison.OrdinalIgnoreCase));
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
        var forgotten = await CaptureAsync(protocol, DownloadClientActions.Forget);

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
        const string wrongShape = "0123456789abcdef0123456789abcdef01234567";

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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            // Sign-in traffic is not the verb under test.
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!path.Contains("auth/login", StringComparison.OrdinalIgnoreCase) && !body.Contains("auth.login", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("token.html", StringComparison.OrdinalIgnoreCase))
            {
                sent.Add($"{path} {body}".Trim());
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
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
            }

            // qBittorrent answers its action endpoints in plain text, not JSON.
            if (path.Contains("api/v2/", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":true,\"result\":true}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
