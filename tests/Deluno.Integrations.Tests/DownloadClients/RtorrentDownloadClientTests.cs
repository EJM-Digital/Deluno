using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #803: rTorrent's XML-RPC over HTTP, exercised the same way the other
/// adapters are — a fake handler answering by method name rather than a live
/// instance (this session had none), asserting the request that went out and
/// the queue item the response is read back into.
/// </summary>
public sealed class RtorrentDownloadClientTests
{
    [Fact]
    public async Task Grab_by_url_sends_load_start_with_the_url()
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        var loadCall = Assert.Single(handler.Requests, request => request.Contains("<methodName>load.start</methodName>", StringComparison.Ordinal));
        Assert.Contains("http://indexer.test/download/1", loadCall);
        Assert.True(result.Succeeded || result.Message.Contains("added no torrent", StringComparison.Ordinal), result.Message);
    }

    [Fact]
    public async Task Grab_with_AddPaused_loads_without_starting()
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        await new RtorrentDownloadClient(new StubFactory(handler))
            .GrabAsync(Client() with { AddPaused = true }, Request(), CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Contains("<methodName>load.normal</methodName>", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Contains("<methodName>load.start</methodName>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grab_reports_the_newly_added_hash()
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[\"ABCDEF0123456789ABCDEF0123456789ABCDEF01\"]");
        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", result.ExternalId);
    }

    [Fact]
    public async Task Grab_reports_no_torrent_added_when_the_hash_list_is_unchanged()
    {
        var handler = new RtorrentHandler(downloadListBefore: "[\"same\"]", downloadListAfter: "[\"same\"]");
        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("added no torrent", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #877, cleanup sheet B3: rTorrent already holding the release is
    /// nobody's fault -- Deluno should follow the copy it has, the way
    /// qBittorrent and the Usenet clients already do, rather than reporting
    /// a failed grab.
    /// </summary>
    [Fact]
    public async Task A_magnet_rtorrent_already_holds_is_followed_instead_of_failed()
    {
        const string hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
        var handler = new RtorrentHandler(downloadListBefore: $"[\"{hash}\"]", downloadListAfter: $"[\"{hash}\"]");
        var request = new DownloadClientGrabRequest(
            "Example.Movie.2024.1080p", $"magnet:?xt=urn:btih:{hash}&dn=Example", "movies", "movies", "Fixture indexer");

        var result = await new RtorrentDownloadClient(new StubFactory(handler)).GrabAsync(Client(), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(hash.ToLowerInvariant(), result.ExternalId);
        Assert.Contains("already holds this release", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #874/#942, cleanup sheet B2: rTorrent answers a load call it could
    /// not honour with an XML-RPC &lt;fault&gt; — it looked at the exact
    /// file or link it was handed and refused it outright, in the same
    /// request. Read as a dead release the way qBittorrent's "Fails." and
    /// NZBGet's RPC error already are, not a generic failure.
    /// </summary>
    [Fact]
    public async Task A_load_fault_is_read_as_a_dead_release()
    {
        var handler = new RtorrentFaultHandler("Could not create download: invalid torrent file.");
        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("invalid torrent file", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #980: rTorrent's RPC almost always sits behind a webserver at a path
    /// (ruTorrent's /RPC2, or a custom mount) -- Deluno's own client drawer
    /// now exposes the URL base for rTorrent, not just Usenet clients, and
    /// this is the adapter side of that: the saved URL base has to actually
    /// land in the request rTorrent's RPC calls go out to, the same generic
    /// path ResolveEndpoint already gives every other client.
    /// </summary>
    [Fact]
    public async Task The_saved_URL_base_is_used_for_every_rtorrent_request()
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        var client = Client() with { EndpointUrl = null, UrlBase = "/RPC2" };

        await new RtorrentDownloadClient(new StubFactory(handler))
            .GrabAsync(client, Request(), CancellationToken.None);

        Assert.NotEmpty(handler.RequestUris);
        Assert.All(handler.RequestUris, uri => Assert.Equal("/RPC2/", uri!.AbsolutePath));
    }

    [Theory]
    [InlineData(DownloadClientActions.Pause, "d.pause")]
    [InlineData(DownloadClientActions.Resume, "d.resume")]
    [InlineData(DownloadClientActions.Recheck, "d.check_hash")]
    [InlineData(DownloadClientActions.Delete, "d.erase")]
    [InlineData(DownloadClientActions.RemoveRefusedCopy, "d.erase")]
    public async Task Every_verb_maps_to_its_own_rtorrent_method(string action, string expectedMethod)
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .ExecuteActionAsync(Client(), action, "abc123hash", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(handler.Requests, request => request.Contains($"<methodName>{expectedMethod}</methodName>", StringComparison.Ordinal));
        Assert.Contains("abc123hash", sent, StringComparison.Ordinal);
    }

    /// <summary>
    /// #803: rTorrent has no delete-with-data of its own. Deluno asks
    /// d.base_path for the download's own folder, releases rTorrent's hold
    /// with d.erase, and deletes exactly that folder itself.
    /// </summary>
    [Fact]
    public async Task DeleteWithData_asks_rtorrent_for_the_path_and_deletes_it_itself()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-rtorrent-{Guid.NewGuid():N}"));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "movie.mkv"), "content");
        try
        {
            var handler = new RtorrentDeleteWithDataHandler(directory.FullName);
            var result = await new RtorrentDownloadClient(new StubFactory(handler))
                .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123hash", CancellationToken.None);

            Assert.True(result.Succeeded, result.Message);
            Assert.Contains("d.base_path", string.Join(' ', handler.Methods));
            Assert.Contains("d.erase", handler.Methods);
            Assert.False(Directory.Exists(directory.FullName));
            Assert.Contains(directory.FullName, result.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory.FullName))
            {
                Directory.Delete(directory.FullName, recursive: true);
            }
        }
    }

    /// <summary>
    /// A path rTorrent cannot be trusted on -- here, one that simply does not
    /// exist on this machine, the same as a remote rTorrent Deluno cannot see
    /// the files of -- is never deleted. rTorrent's own hold is still
    /// released, and the limit is stated rather than silently doing nothing.
    /// </summary>
    [Fact]
    public async Task DeleteWithData_states_the_limit_when_the_path_cannot_be_safely_deleted()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"deluno-rtorrent-missing-{Guid.NewGuid():N}");
        var handler = new RtorrentDeleteWithDataHandler(missingPath);

        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123hash", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("d.erase", handler.Methods);
        Assert.Contains("left on disk", result.Message, StringComparison.Ordinal);
    }

    /// <summary>rTorrent reporting its own filesystem root is a misconfiguration, never this download's own path.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("C:\\")]
    public async Task DeleteWithData_never_deletes_a_filesystem_root(string root)
    {
        var handler = new RtorrentDeleteWithDataHandler(root);

        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123hash", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("left on disk", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DownloadClientActions.MoveToTop)]
    [InlineData(DownloadClientActions.ForceStart)]
    public async Task Unsupported_verbs_are_refused(string action)
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        var result = await new RtorrentDownloadClient(new StubFactory(handler))
            .ExecuteActionAsync(Client(), action, "abc123hash", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Peers_are_every_connected_peer_seeds_included()
    {
        var snapshot = await new RtorrentDownloadClient(new StubFactory(new RtorrentSnapshotHandler(peers: 7)))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(7, Assert.Single(snapshot!.Queue).Peers);
    }

    [Fact]
    public async Task Snapshot_reads_the_multicall_response_into_a_queue_item()
    {
        var handler = new RtorrentSnapshotHandler();
        var snapshot = await new RtorrentDownloadClient(new StubFactory(handler))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", item.Id);
        Assert.Equal("Example.Movie.2024.1080p", item.ReleaseName);
        Assert.Equal(50.0, item.Progress);
        Assert.Equal(500_000_000L, item.DownloadedBytes);
        Assert.Equal(1_000_000_000L, item.SizeBytes);
        Assert.Equal(1.5, item.Ratio);
        Assert.Equal("/downloads/Example.Movie.2024.1080p", item.SourcePath);
    }

    [Fact]
    public async Task A_message_from_rtorrent_itself_is_read_as_a_setup_problem()
    {
        var handler = new RtorrentSnapshotHandler(message: "Couldn't open file (disk full)");
        var snapshot = await new RtorrentDownloadClient(new StubFactory(handler))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.True(item.IsSetupProblem);
        Assert.Equal("Couldn't open file (disk full)", item.ErrorMessage);
    }

    /// <summary>
    /// #878, cleanup sheet C5: rTorrent's own d.multicall2 never carries a
    /// tracker's message, so a tracker's "torrent not registered" answer read
    /// through t.multicall must map onto the same Unregistered status
    /// qBittorrent/Transmission/Deluge already produce, not stay Unknown.
    /// </summary>
    [Fact]
    public async Task An_unregistered_tracker_message_is_read_as_unregistered()
    {
        var handler = new RtorrentSnapshotHandler(trackerMessage: "torrent not registered with this tracker");
        var snapshot = await new RtorrentDownloadClient(new StubFactory(handler))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(Deluno.Contracts.DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task A_healthy_tracker_message_reads_as_ok()
    {
        var handler = new RtorrentSnapshotHandler(trackerMessage: string.Empty);
        var snapshot = await new RtorrentDownloadClient(new StubFactory(handler))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(Deluno.Contracts.DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A tracker rTorrent cannot reach at all is a local/outage problem, never a bad release (#901).</summary>
    [Fact]
    public async Task A_tracker_call_that_fails_stays_unknown_rather_than_unregistered()
    {
        var handler = new RtorrentSnapshotHandler(trackerCallFails: true);
        var snapshot = await new RtorrentDownloadClient(new StubFactory(handler))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(Deluno.Contracts.DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }

    [Fact]
    public async Task Basic_auth_is_sent_when_a_username_and_secret_are_configured()
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        await new RtorrentDownloadClient(new StubFactory(handler))
            .ExecuteActionAsync(Client() with { Username = "admin", Secret = "hunter2" }, DownloadClientActions.Pause, "abc123hash", CancellationToken.None);

        Assert.Contains(handler.AuthorizationHeaders, header => header is { Scheme: "Basic" });
    }

    [Fact]
    public void Normalizes_hash_checking_paused_complete_and_downloading_states()
    {
        var client = new RtorrentDownloadClient(new StubFactory(new RtorrentHandler("[]", "[]")));
        Assert.Equal(DownloadQueueStatuses.NotTransferring, client.NormalizeStatus("hash_checking", 40));
        Assert.Equal(DownloadQueueStatuses.Stalled, client.NormalizeStatus("error", 40));
        Assert.Equal(DownloadQueueStatuses.ImportReady, client.NormalizeStatus("complete", 100));
        Assert.Equal(DownloadQueueStatuses.Paused, client.NormalizeStatus("paused", 40));
        Assert.Equal(DownloadQueueStatuses.Downloading, client.NormalizeStatus("downloading", 40));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static DownloadClientGrabRequest Request()
        => new("Example.Movie.2024.1080p", "http://indexer.test/download/1", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem Client()
        => new(
            "client-1", "rTorrent", "rtorrent", "localhost", 8000, null, null, "http://localhost:8000/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage XmlResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private static string MethodNameOf(string requestXml)
        => Regex.Match(requestXml, "<methodName>(.*?)</methodName>", RegexOptions.Singleline).Groups[1].Value;

    /// <summary>
    /// Answers download_list with one array before the add and a different
    /// one after, and everything else (load.*, d.*) with a bare integer
    /// success value -- the shape every one-arg rTorrent command returns.
    /// </summary>
    private sealed class RtorrentHandler(string downloadListBefore, string downloadListAfter) : HttpMessageHandler
    {
        private bool _loaded;

        public List<string> Requests { get; } = [];

        public List<System.Net.Http.Headers.AuthenticationHeaderValue?> AuthorizationHeaders { get; } = [];

        public List<Uri?> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(body);
            AuthorizationHeaders.Add(request.Headers.Authorization);
            RequestUris.Add(request.RequestUri);
            var method = MethodNameOf(body);

            if (method.StartsWith("load.", StringComparison.Ordinal))
            {
                _loaded = true;
                return XmlResponse(IntResponse(0));
            }

            if (method == "download_list")
            {
                var hashes = _loaded ? downloadListAfter : downloadListBefore;
                var values = hashes.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => value.Trim('"'));
                return XmlResponse(MethodResponse(AsValue(ArrayOf(values.Select(hash => AsValue(TypedString(hash)))))));
            }

            return XmlResponse(IntResponse(0));
        }
    }

    /// <summary>Answers any load.* call with an XML-RPC &lt;fault&gt; carrying the given message, everything else with success.</summary>
    private sealed class RtorrentFaultHandler(string faultMessage) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = MethodNameOf(body);

            if (method.StartsWith("load.", StringComparison.Ordinal))
            {
                return XmlResponse(FaultResponse(faultMessage));
            }

            if (method == "download_list")
            {
                return XmlResponse(MethodResponse(AsValue(ArrayOf([]))));
            }

            return XmlResponse(IntResponse(0));
        }
    }

    private static string FaultResponse(string faultMessage)
        => $"""<?xml version="1.0"?><methodResponse><fault>{AsValue(TypedString(faultMessage))}</fault></methodResponse>""";

    /// <summary>Answers d.base_path with a fixed path and everything else (d.erase) with success, recording every method called.</summary>
    private sealed class RtorrentDeleteWithDataHandler(string basePath) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = MethodNameOf(body);
            Methods.Add(method);

            if (method == "d.base_path")
            {
                return XmlResponse(MethodResponse(AsValue(TypedString(basePath))));
            }

            return XmlResponse(IntResponse(0));
        }
    }

    /// <summary>
    /// Answers d.multicall2 with one row of canned per-torrent values, in
    /// MapRow's own field order, and t.multicall (#878) with a single
    /// tracker's own enabled flag and message -- or a failure, when asked to
    /// simulate a tracker rTorrent cannot reach at all.
    /// </summary>
    private sealed class RtorrentSnapshotHandler(string? message = null, string? trackerMessage = null, bool trackerCallFails = false, int peers = 0) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = MethodNameOf(body);

            if (method == "t.multicall")
            {
                if (trackerCallFails)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var tracker = AsValue(ArrayOf([AsValue(TypedInt(1)), AsValue(TypedString(trackerMessage ?? string.Empty))]));
                return XmlResponse(MethodResponse(AsValue(ArrayOf([tracker]))));
            }

            var fields = new[]
            {
                AsValue(TypedString("abcdef0123456789abcdef0123456789abcdef01")),
                AsValue(TypedString("Example.Movie.2024.1080p")),
                AsValue(TypedInt(1)), // is_active
                AsValue(TypedInt(0)), // is_hash_checking
                AsValue(TypedInt(0)), // complete
                AsValue(TypedInt(500_000_000)), // bytes_done
                AsValue(TypedInt(1_000_000_000)), // size_bytes
                AsValue(TypedInt(1_000_000)), // down.rate
                AsValue(TypedInt(0)), // up.rate
                AsValue(TypedInt(1500)), // ratio (x1000)
                AsValue(TypedString(message ?? string.Empty)),
                AsValue(TypedString("/downloads/Example.Movie.2024.1080p")),
                AsValue(TypedInt(peers)) // peers_connected (seeds included)
            };
            var row = AsValue(ArrayOf(fields));
            return XmlResponse(MethodResponse(AsValue(ArrayOf([row]))));
        }
    }

    private static string MethodResponse(string valueXml)
        => $"""<?xml version="1.0"?><methodResponse><params><param>{valueXml}</param></params></methodResponse>""";

    private static string IntResponse(int value) => MethodResponse(AsValue(TypedInt(value)));

    /// <summary>Wraps an already-typed fragment (e.g. &lt;string&gt;x&lt;/string&gt;) as an XML-RPC &lt;value&gt;.</summary>
    private static string AsValue(string typed) => $"<value>{typed}</value>";

    private static string TypedString(string value) => $"<string>{value}</string>";

    private static string TypedInt(long value) => $"<i4>{value}</i4>";

    /// <summary>An &lt;array&gt; built from already-&lt;value&gt;-wrapped items.</summary>
    private static string ArrayOf(IEnumerable<string> valueFragments) => $"<array><data>{string.Concat(valueFragments)}</data></array>";
}
