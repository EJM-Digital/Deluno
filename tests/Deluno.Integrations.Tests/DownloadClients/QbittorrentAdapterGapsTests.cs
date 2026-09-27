using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

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
        var handler = new QbittorrentHandler("""
            [{"hash":"abc123","name":"Example","state":"uploading","progress":1,"availability":1.5,"last_activity":1750000100,"completion_on":1750000200}]
            """);

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
        var handler = new QbittorrentHandler("""
            [{"hash":"abc123","name":"Example","state":"downloading","progress":0.4,"last_activity":1750000100,"completion_on":-1}]
            """);

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Null(item.CompletionUtc);
    }

    [Fact]
    public async Task Sends_no_authorization_header_and_logs_in_when_a_username_is_set()
    {
        var handler = new RecordingHandler("[]");
        await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(QbitClient() with { Username = "admin", Secret = "hunter2" }, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains("auth/login", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Headers.Authorization is not null);
    }

    [Fact]
    public async Task Sends_a_bearer_token_and_skips_login_when_only_a_secret_is_set()
    {
        var handler = new RecordingHandler("[]");
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

        await new QbittorrentDownloadClient(() => new AddCapturingHandler(bodies))
            .GrabAsync(client, GrabRequest(), CancellationToken.None);

        var body = Assert.Single(bodies);
        Assert.Contains(expectedFragment, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_sends_the_clients_own_tags_on_the_torrent_itself()
    {
        var bodies = new List<string>();
        await new QbittorrentDownloadClient(() => new AddCapturingHandler(bodies))
            .GrabAsync(QbitClient() with { Tags = "anime,4k" }, GrabRequest(), CancellationToken.None);

        Assert.Contains("tags=anime%2C4k", Assert.Single(bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_sends_no_tags_field_for_an_untagged_client()
    {
        var bodies = new List<string>();
        await new QbittorrentDownloadClient(() => new AddCapturingHandler(bodies))
            .GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.DoesNotContain("tags=", Assert.Single(bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_sets_share_limits_on_the_newly_added_torrent()
    {
        var handler = new AddThenNewHashHandler(newHash: "abc123");
        var client = QbitClient() with { ShareRatioLimit = 2.5, ShareSeedingTimeLimitMinutes = 4320, ShareInactiveSeedingTimeLimitMinutes = 60 };

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(client, GrabRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(handler.ShareLimitsBody);
        Assert.Contains("hashes=abc123", handler.ShareLimitsBody, StringComparison.Ordinal);
        Assert.Contains("ratioLimit=2.5", handler.ShareLimitsBody, StringComparison.Ordinal);
        Assert.Contains("seedingTimeLimit=4320", handler.ShareLimitsBody, StringComparison.Ordinal);
        Assert.Contains("inactiveSeedingTimeLimit=60", handler.ShareLimitsBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_never_calls_setShareLimits_when_no_share_limit_is_configured()
    {
        var handler = new AddThenNewHashHandler(newHash: "abc123");

        await new QbittorrentDownloadClient(() => handler).GrabAsync(QbitClient(), GrabRequest(), CancellationToken.None);

        Assert.Null(handler.ShareLimitsBody);
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

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QbittorrentHandler(string torrentsInfo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var reply = path.Contains("auth/login", StringComparison.Ordinal)
                ? "Ok."
                : path.Contains("torrents/info", StringComparison.Ordinal)
                    ? torrentsInfo
                    : "{}";
            return Task.FromResult(JsonResponse(reply));
        }
    }

    /// <summary>Records every request it answers, so a test can check what was (or was not) sent.</summary>
    private sealed class RecordingHandler(string torrentsInfo) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var reply = path.Contains("auth/login", StringComparison.Ordinal)
                ? "Ok."
                : path.Contains("torrents/info", StringComparison.Ordinal)
                    ? torrentsInfo
                    : "{}";
            return Task.FromResult(JsonResponse(reply));
        }
    }

    /// <summary>Captures the body posted to torrents/add, answering an empty list before and after (no new hash, so only the request under test matters).</summary>
    private sealed class AddCapturingHandler(List<string> addBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                addBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
                return JsonResponse("Ok.");
            }

            if (path.Contains("auth/login", StringComparison.Ordinal)) return JsonResponse("Ok.");
            if (path.Contains("torrents/info", StringComparison.Ordinal)) return JsonResponse("[]");
            return JsonResponse("{}");
        }
    }

    /// <summary>
    /// Answers torrents/info with nothing, then the given hash once the add
    /// has gone through — the before/after honesty check (#606) reading a
    /// genuinely new torrent — and records whatever setShareLimits is asked.
    /// </summary>
    private sealed class AddThenNewHashHandler(string newHash) : HttpMessageHandler
    {
        private bool _added;

        public string? ShareLimitsBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                _added = true;
                return JsonResponse("Ok.");
            }

            if (path.Contains("setShareLimits", StringComparison.Ordinal))
            {
                ShareLimitsBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                return JsonResponse("{}");
            }

            if (path.Contains("auth/login", StringComparison.Ordinal)) return JsonResponse("Ok.");
            if (path.Contains("torrents/info", StringComparison.Ordinal))
            {
                return JsonResponse(_added ? $$"""[{"hash":"{{newHash}}","name":"Example"}]""" : "[]");
            }

            return JsonResponse("{}");
        }
    }
}
