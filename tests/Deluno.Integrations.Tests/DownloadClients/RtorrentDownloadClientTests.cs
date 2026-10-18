using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #803: rTorrent's XML-RPC over HTTP, exercised the same way the other
/// adapters are — a handler answering by method name with rTorrent 0.16.25's
/// recorded replies (tests/fixtures/recorded/rtorrent, #1237; the whole run of
/// each state is replayed in RtorrentOnRecordingsTests), asserting the request
/// that went out and the queue item the response is read back into.
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

    /// <summary>#1252: a second removal of the same download is a plain failure, not "done" (rTorrent answers a hash it no longer holds with a fault).</summary>
    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    public async Task A_second_removal_is_a_failure_and_sends_no_second_erase(string action)
    {
        var handler = new RtorrentHandler(downloadListBefore: "[]", downloadListAfter: "[]");
        var client = new RtorrentDownloadClient(new StubFactory(handler));
        Assert.True((await client.ExecuteActionAsync(Client(), action, "abc123hash", CancellationToken.None)).Succeeded);

        var again = await client.ExecuteActionAsync(Client(), action, "abc123hash", CancellationToken.None);

        Assert.False(again.Succeeded);
        Assert.Contains("has no download with that id", again.Message);
        Assert.Single(handler.Requests, request => request.Contains("<methodName>d.erase</methodName>", StringComparison.Ordinal));
    }

    [Theory]
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
    /// #1306: a path rTorrent reports that climbs out ("..") is never deleted, even when it resolves to a real
    /// folder. This uses rTorrent's recorded
    /// d.base_path reply with the one value, the base path, swapped for a climbing path.
    /// </summary>
    [Fact]
    public async Task DeleteWithData_never_deletes_a_base_path_that_climbs_out_even_if_it_resolves_to_a_real_folder()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-rtorrent-{Guid.NewGuid():N}"));
        try
        {
            var victim = Directory.CreateDirectory(Path.Combine(scratch.FullName, "victim"));
            await File.WriteAllTextAsync(Path.Combine(victim.FullName, "keep.mkv"), "mine");
            Directory.CreateDirectory(Path.Combine(scratch.FullName, "downloads"));
            var handler = new RtorrentDeleteWithDataHandler(Path.Combine(scratch.FullName, "downloads", "..", "victim"));

            var result = await new RtorrentDownloadClient(new StubFactory(handler))
                .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123hash", CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(victim.FullName, "keep.mkv")));
            Assert.Contains("d.erase", handler.Methods);
            Assert.Contains("could not safely find its files", result.Message);
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    /// <summary>
    /// #1328: a base path rTorrent reports that is, holds or sits inside a library root is never deleted. A real library
    /// folder holding a real film, rTorrent's recorded d.base_path reply with only the base path swapped in.
    /// </summary>
    [Theory]
    [InlineData("inside")]
    [InlineData("is")]
    [InlineData("holds")]
    public async Task DeleteWithData_never_deletes_a_base_path_that_is_holds_or_sits_inside_a_library(string relation)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-rtorrent-{Guid.NewGuid():N}"));
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(scratch.FullName, "Media", "Movies"));
            var jobFolder = Directory.CreateDirectory(Path.Combine(library.FullName, "Some.Film"));
            var film = Path.Combine(jobFolder.FullName, "Some.Film.mkv");
            await File.WriteAllTextAsync(film, "mine");
            var reported = relation switch { "inside" => jobFolder.FullName, "is" => library.FullName, _ => library.Parent!.FullName };
            var handler = new RtorrentDeleteWithDataHandler(reported);

            var result = await new RtorrentDownloadClient(new StubFactory(handler), LibrariesAt(library.FullName))
                .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123hash", CancellationToken.None);

            Assert.True(File.Exists(film), "the film in the library is untouched");
            Assert.Contains("d.erase", handler.Methods);
            Assert.True(result.Succeeded, result.Message);
            Assert.Contains("inside a library, or holds one", result.FilesNote);
            Assert.Contains("left its files on disk", result.Message);
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    /// <summary>#1328 control: the same removal of a folder outside every library still deletes it.</summary>
    [Fact]
    public async Task DeleteWithData_still_deletes_a_base_path_outside_every_library()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-rtorrent-{Guid.NewGuid():N}"));
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(scratch.FullName, "Media", "Movies"));
            var jobFolder = Directory.CreateDirectory(Path.Combine(scratch.FullName, "Completed", "Some.Film"));
            await File.WriteAllTextAsync(Path.Combine(jobFolder.FullName, "Some.Film.mkv"), "x");

            var result = await new RtorrentDownloadClient(new StubFactory(new RtorrentDeleteWithDataHandler(jobFolder.FullName)), LibrariesAt(library.FullName))
                .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123hash", CancellationToken.None);

            Assert.False(Directory.Exists(jobFolder.FullName));
            Assert.Null(result.FilesNote);
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    private static Deluno.Libraries.Data.ILibrariesRepository LibrariesAt(string root)
    {
        var now = DateTimeOffset.UnixEpoch;
        var library = new Deluno.Libraries.Contracts.LibraryItem(
            "library-1", "Movies", "movies", "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
        var repository = new Mock<Deluno.Libraries.Data.ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        return repository.Object;
    }

    /// <summary>#1306: the folder rTorrent lists for a download is dropped when it climbs out (the recorded list row with one value swapped).</summary>
    [Fact]
    public async Task A_listed_download_folder_that_climbs_out_is_not_used()
    {
        var clean = await new RtorrentDownloadClient(new StubFactory(new RtorrentSnapshotHandler()))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        var climbing = await new RtorrentDownloadClient(new StubFactory(new RtorrentSnapshotHandler(directory: "/downloads/../etc")))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("/downloads/Example.Movie.2024.1080p", Assert.Single(clean!.Queue).SourcePath);
        Assert.Null(Assert.Single(climbing!.Queue).SourcePath);
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
    // Every reply below is rTorrent 0.16.25's recorded reply to the same call (tests/fixtures/recorded/rtorrent), with only the values a
    // test is about changed: the hash it lists, the path it reports, the message it carries.

    private static Recording R(string state) => RecordedReplies.Rtorrent(state);

    private static string Recorded(string state, int exchange) => R(state).Exchanges[exchange].Reply.Body!;

    /// <summary>What rTorrent answers a call that works and has nothing to return: the 0 of d.pause.</summary>
    private static string Done() => Recorded("paused", 0);

    /// <summary>What rTorrent answers d.name for a download it holds.</summary>
    private static string Name() => Recorded("downloading", 2);

    /// <summary>What rTorrent answers a call about a download it does not hold: a fault (d.name after the erase).</summary>
    private static string NoSuchDownload() => Recorded("removed-twice", 1);

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

    /// <summary>download_list as rTorrent answers it holding exactly these hashes (the recorded list holds one; none is the recorded empty one).</summary>
    private static string HashList(IEnumerable<string> hashes)
    {
        var held = hashes.ToArray();
        if (held.Length == 0)
        {
            return Recorded("idle", 1);
        }

        var recorded = Recorded("add-file", 2);
        var one = Regex.Match(recorded, "<value><string>[0-9A-F]{40}</string></value>").Value;
        return recorded.Replace(one, string.Concat(held.Select(hash => $"<value><string>{System.Security.SecurityElement.Escape(hash)}</string></value>")));
    }

    private static IEnumerable<string> HashesOf(string list)
        => list.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => value.Trim('"'));

    /// <summary>The recorded fault with the reason it gives replaced (the shape and code are rTorrent's own).</summary>
    private static string FaultResponse(string faultMessage)
        => Recorded("unknown-hash-fault", 0).Replace("invalid parameters: info-hash not found", System.Security.SecurityElement.Escape(faultMessage));

    /// <summary>
    /// Answers download_list with one array before the add and a different one after, and everything else with the recorded replies
    /// of the same calls: load.* with the 0 of load.raw_start, d.name with the name of a held download until d.erase, then the fault.
    /// </summary>
    private sealed class RtorrentHandler(string downloadListBefore, string downloadListAfter) : HttpMessageHandler
    {
        private bool _loaded;
        private bool _erased;

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

            // #1252: rTorrent holds the download until d.erase, and answers a hash it does not hold with a fault.
            if (method == "d.erase") _erased = true;
            if (method == "d.name")
            {
                return XmlResponse(_erased ? NoSuchDownload() : Name());
            }

            if (method.StartsWith("load.", StringComparison.Ordinal))
            {
                _loaded = true;
                return XmlResponse(Recorded("add-file", 1));
            }

            if (method == "download_list")
            {
                return XmlResponse(HashList(HashesOf(_loaded ? downloadListAfter : downloadListBefore)));
            }

            return XmlResponse(Done());
        }
    }

    /// <summary>Answers any load.* call with an XML-RPC fault carrying the given reason, everything else as the recorded rTorrent did.</summary>
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
                return XmlResponse(HashList([]));
            }

            return XmlResponse(Done());
        }
    }

    /// <summary>Answers d.base_path with the recorded reply carrying a path of the test's own and everything else (d.erase) with the recorded 0, recording every method called.</summary>
    private sealed class RtorrentDeleteWithDataHandler(string basePath) : HttpMessageHandler
    {
        private bool _erased;

        public List<string> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = MethodNameOf(body);
            Methods.Add(method);

            // #1252: rTorrent holds the download until d.erase, and answers a hash it does not hold with a fault.
            if (method == "d.erase") _erased = true;
            if (method == "d.name")
            {
                return XmlResponse(_erased ? NoSuchDownload() : Name());
            }

            if (method == "d.base_path")
            {
                return XmlResponse(Recorded("finished-single-file", 2).Replace("/downloads/complete/Recorded.Rtorrent.Film.2024.1080p.WEB-DL.mkv", System.Security.SecurityElement.Escape(basePath)));
            }

            return XmlResponse(Done());
        }
    }

    /// <summary>
    /// Answers d.multicall2 with the recorded row of a downloading torrent, in MapRow's own field order, with the values this test is about
    /// replaced; the tracker call is answered with the fault the real rTorrent gives (it has no t.message), and the version as recorded.
    /// </summary>
    private sealed class RtorrentSnapshotHandler(string? message = null, int peers = 0, string directory = "/downloads/Example.Movie.2024.1080p") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = MethodNameOf(body);

            if (method == "t.multicall")
            {
                return XmlResponse(Recorded("downloading", 4));
            }

            if (method == "system.client_version")
            {
                return XmlResponse(Recorded("version", 0));
            }

            return XmlResponse(Row(
                (0, "abcdef0123456789abcdef0123456789abcdef01"), (1, "Example.Movie.2024.1080p"), (2, 1), (3, 0), (4, 0), (5, 500_000_000L), (6, 1_000_000_000L),
                (7, 1_000_000L), (8, 0), (9, 1500), (10, message ?? string.Empty), (11, directory), (12, peers)));
        }
    }

    /// <summary>The recorded list row of a downloading torrent with the given fields (by position in MapRow's order) set to the test's own values.</summary>
    private static string Row(params (int Index, object Value)[] fields)
    {
        var document = System.Xml.Linq.XDocument.Parse(Recorded("downloading", 0));
        var values = document.Descendants("data").First().Elements("value").First().Descendants("data").First().Elements("value").ToList();
        foreach (var (index, value) in fields)
        {
            values[index].Elements().First().Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        }

        return document.Declaration + document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }
}
