using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #811: qBittorrent's availability/last_activity/completion_on telemetry
/// fields, and API-key (Bearer token) authentication for qBittorrent 5.2.0+
/// used alongside the existing form-based login.
/// </summary>
public sealed class QbittorrentAdapterGapsTests
{
    [Fact]
    public async Task Reads_availability_last_activity_and_completion_on()
    {
        // qBittorrent 5.2.1's recorded list of a finished torrent, with the figures under test.
        var handler = RecordedQbittorrentRig.Handler(RecordedQbittorrentRig.Info(
            RecordedQbittorrentRig.Finished, ("hash", "abc123"), ("name", "Example"), ("state", "uploading"), ("progress", 1),
            ("availability", 1.5), ("last_activity", 1750000100), ("completion_on", 1750000200)));

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(1.5, item.Availability);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000100), item.LastActivityUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000200), item.CompletionUtc);
    }

    [Fact]
    public async Task Completion_on_of_minus_one_reads_as_not_completed_rather_than_epoch()
    {
        var handler = RecordedQbittorrentRig.Handler(RecordedQbittorrentRig.Info(
            RecordedQbittorrentRig.Downloading, ("hash", "abc123"), ("name", "Example"), ("state", "downloading"), ("progress", 0.4),
            ("last_activity", 1750000100), ("completion_on", -1)));

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Null(item.CompletionUtc);
    }

    [Fact]
    public async Task Sends_no_authorization_header_and_logs_in_when_a_username_is_set()
    {
        var handler = new RecordedQbittorrentLog();
        await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient() with { Username = "admin", Secret = "hunter2" }, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains("auth/login", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Headers.Authorization is not null);
    }

    [Fact]
    public async Task Sends_a_bearer_token_and_skips_login_when_only_a_secret_is_set()
    {
        var handler = new RecordedQbittorrentLog();
        await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient() with { Username = null, Secret = "api-key-value" }, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains("auth/login", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request =>
            request.Headers.Authorization is { Scheme: "Bearer", Parameter: "api-key-value" });
    }

    // ── Add-time options and tags (#811) ─────────────────────────────────────

    [Theory]
    [InlineData(false, false, false, "skip_checking=false&sequentialDownload=false&firstLastPiecePrio=false")]
    [InlineData(true, true, true, "skip_checking=true&sequentialDownload=true&firstLastPiecePrio=true")]
    public async Task Grab_sends_the_configured_add_time_options(bool skip, bool sequential, bool firstLast, string expectedFragment)
    {
        var bodies = new List<string>();
        var client = QbitClient() with { SkipHashCheck = skip, SequentialDownload = sequential, FirstLastPiecePrio = firstLast };

        await new QbittorrentDownloadClient(() => new RecordedAddCapturingHandler(bodies))
            .GrabAsync(client, GrabRequest(), CancellationToken.None);

        var body = Assert.Single(bodies);
        Assert.Contains(expectedFragment, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_sends_the_clients_own_tags_on_the_torrent_itself()
    {
        var bodies = new List<string>();
        await new QbittorrentDownloadClient(() => new RecordedAddCapturingHandler(bodies))
            .GrabAsync(QbitClient() with { Tags = "anime,4k" }, GrabRequest(), CancellationToken.None);

        Assert.Contains("tags=anime%2C4k", Assert.Single(bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_sends_no_tags_field_for_an_untagged_client()
    {
        var bodies = new List<string>();
        await new QbittorrentDownloadClient(() => new RecordedAddCapturingHandler(bodies))
            .GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.DoesNotContain("tags=", Assert.Single(bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_sets_share_limits_on_the_newly_added_torrent()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");
        var client = QbitClient() with { ShareRatioLimit = 2.5, ShareSeedingTimeLimitMinutes = 4320, ShareInactiveSeedingTimeLimitMinutes = 60 };

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(client, GrabRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(handler.ShareLimitsBody);
        // qBittorrent 5.2.1 refuses share limits sent without a share-limit action (400, recorded); a refusal here is swallowed
        // by the grab, so the limits would silently never apply.
        Assert.True(handler.ShareLimitsAccepted, "qBittorrent refused the share limits: " + handler.ShareLimitsBody);
        Assert.Contains("hashes=abc123", handler.ShareLimitsBody, StringComparison.Ordinal);
        Assert.Contains("ratioLimit=2.5", handler.ShareLimitsBody, StringComparison.Ordinal);
        Assert.Contains("seedingTimeLimit=4320", handler.ShareLimitsBody, StringComparison.Ordinal);
        Assert.Contains("inactiveSeedingTimeLimit=60", handler.ShareLimitsBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_never_calls_setShareLimits_when_no_share_limit_is_configured()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");

        await new QbittorrentDownloadClient(() => handler).GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.Null(handler.ShareLimitsBody);
    }

    [Fact]
    public async Task Grab_sends_savepath_at_add_time_and_reasserts_it_with_setLocation()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");
        var client = QbitClient() with { Location = @"D:\Downloads\Movies" };

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(client, GrabRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(handler.AddBody);
        Assert.Contains("savepath=", handler.AddBody, StringComparison.Ordinal);
        Assert.NotNull(handler.LocationBody);
        Assert.Contains("hashes=abc123", handler.LocationBody, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(client.Location!), handler.LocationBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_without_a_save_path_asks_for_automatic_management_so_the_category_folder_is_used()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("autoTMM=true", handler.AddBody, StringComparison.Ordinal);
        Assert.NotNull(handler.AutoManagementBody);
        Assert.Contains("hashes=abc123", handler.AutoManagementBody, StringComparison.Ordinal);
        Assert.Contains("enable=true", handler.AutoManagementBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_with_a_save_path_keeps_its_own_folder_and_does_not_turn_automatic_management_on()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");
        var client = QbitClient() with { Location = @"D:\Downloads\Movies" };

        await new QbittorrentDownloadClient(() => handler).GrabAsync(client, GrabRequest(), CancellationToken.None);

        Assert.DoesNotContain("autoTMM", handler.AddBody, StringComparison.Ordinal);
        Assert.Null(handler.AutoManagementBody);
    }

    [Fact]
    public async Task Grab_never_calls_setLocation_when_no_save_path_is_configured()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");

        await new QbittorrentDownloadClient(() => handler).GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.Null(handler.LocationBody);
    }

    [Fact]
    public async Task Grab_reasserts_the_resolved_category_with_setCategory()
    {
        var handler = new RecordedAddThenNewHashHandler(newHash: "abc123");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(handler.CategoryBody);
        Assert.Contains("hashes=abc123", handler.CategoryBody, StringComparison.Ordinal);
        Assert.Contains("category=movies", handler.CategoryBody, StringComparison.Ordinal);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static DownloadClientGrabRequest GrabRequest()
        => new("Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO", "http://indexer.test/download/1", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem QbitClient()
        => new(
            Id: "qbit-1", Name: "qBittorrent", Protocol: "qbittorrent", Host: "localhost", Port: 8080,
            Username: null, Secret: null, EndpointUrl: "http://localhost:8080/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    /// <summary>qBittorrent 5.2.1's recorded answer to a call that changes something, which is an empty 200 (the recorded category move).</summary>
    private static HttpResponseMessage Changed() => RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");

    /// <summary>qBittorrent 5.2.1's recorded sign-in and version, which every call here starts with.</summary>
    private static HttpResponseMessage? SignIn(string path)
        => path.Contains("auth/login", StringComparison.Ordinal) ? RecordedReplies.Qbittorrent("sign-in").Response("auth/login")
            : path.Contains("app/version", StringComparison.Ordinal) ? RecordedReplies.Qbittorrent("version").Response("app/version")
            : null;

    /// <summary>The recorded answer to a list of torrents when qBittorrent has none.</summary>
    private static HttpResponseMessage NoTorrents() => RecordedReplies.Json(RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info"));

    /// <summary>Records every request it answers, so a test can check what was (or was not) sent. It answers from the recordings with an empty list.</summary>
    private sealed class RecordedQbittorrentLog : HttpMessageHandler
    {
        private readonly HttpMessageHandler _inner = RecordedQbittorrentRig.Handler(RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info"));

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var invoker = new HttpMessageInvoker(_inner, disposeHandler: false);
            return invoker.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Captures the body posted to torrents/add, answering an empty list before and after (no new hash, so only the request under test matters).</summary>
    private sealed class RecordedAddCapturingHandler(List<string> addBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                addBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
                return RecordedReplies.Qbittorrent("finished-single-file").Response("torrents/add");
            }

            return SignIn(path) ?? (path.Contains("torrents/info", StringComparison.Ordinal)
                ? NoTorrents()
                : throw new InvalidOperationException("qBittorrent was asked something no recording answers: " + path));
        }
    }

    /// <summary>
    /// Answers torrents/info with nothing, then the given hash once the add has gone through — the before/after honesty check (#606)
    /// reading a genuinely new torrent — and records whatever the grab then sets. Every reply is qBittorrent 5.2.1's recorded one,
    /// including its recorded refusal of share limits sent without a share-limit action.
    /// </summary>
    private sealed class RecordedAddThenNewHashHandler(string newHash) : HttpMessageHandler
    {
        private bool _added;

        public string? ShareLimitsBody { get; private set; }

        /// <summary>Whether qBittorrent took the share limits (it refuses them without a share-limit action, as recorded).</summary>
        public bool ShareLimitsAccepted { get; private set; }

        public string? AddBody { get; private set; }

        public string? LocationBody { get; private set; }

        public string? CategoryBody { get; private set; }

        public string? AutoManagementBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                _added = true;
                AddBody = body;
                return RecordedReplies.Qbittorrent("finished-single-file").Response("torrents/add");
            }

            if (path.Contains("setShareLimits", StringComparison.Ordinal))
            {
                ShareLimitsBody = body;
                ShareLimitsAccepted = body.Contains("shareLimitAction=", StringComparison.Ordinal);
                return ShareLimitsAccepted ? Changed() : RecordedReplies.Qbittorrent("share-limits").Response("setShareLimits");
            }

            if (path.Contains("setLocation", StringComparison.Ordinal))
            {
                LocationBody = body;
                return RecordedReplies.Qbittorrent("location-moved").Response("setLocation");
            }

            if (path.Contains("setCategory", StringComparison.Ordinal))
            {
                CategoryBody = body;
                return Changed();
            }

            if (path.Contains("createCategory", StringComparison.Ordinal))
            {
                return RecordedReplies.Qbittorrent("category-moved").Response("createCategory");
            }

            if (path.Contains("setAutoManagement", StringComparison.Ordinal))
            {
                // qBittorrent 5.2.4's own answer to setAutoManagement, recorded: an empty 200.
                AutoManagementBody = body;
                return RecordedReplies.Qbittorrent("auto-management-set").Response("torrents/setAutoManagement");
            }

            if (SignIn(path) is { } signIn) return signIn;
            if (path.Contains("torrents/info", StringComparison.Ordinal))
            {
                return _added
                    ? RecordedReplies.Json(RecordedQbittorrentRig.Info(RecordedQbittorrentRig.Finished, ("hash", newHash), ("name", "Example")))
                    : NoTorrents();
            }

            throw new InvalidOperationException("qBittorrent was asked something no recording answers: " + path);
        }
    }
}
