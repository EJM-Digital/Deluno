using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// The usenet integration parity audit (#663, `artifacts/parity-audit/integration-usenet.md`):
/// priority and password sent on add when Deluno has them, native
/// post-processing stages read as "processing" rather than "downloading", and
/// low disk space folded into client health.
/// </summary>
public sealed class UsenetClientParityTests
{
    // ── Priority and password on add ────────────────────────────────────────

    [Fact]
    public async Task NzbGet_grab_sends_priority_and_unpack_password_when_Deluno_has_them()
    {
        var sent = new List<string>();
        var request = new DownloadClientGrabRequest(
            "Pioneer.One.S01E01.1080p", "https://indexer.invalid/get/1", "tv", "TV", "altHUB")
        {
            Priority = 50,
            Password = "hunter2"
        };

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(_ =>
            RecordedReplies.Nzbget("queued").Response("\"append\""), sent)))
            .GrabAsync(NzbGetClient(), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var body = Assert.Single(sent);
        using var document = JsonDocument.Parse(body);
        var parameters = document.RootElement.GetProperty("params");
        Assert.Equal(50, parameters[3].GetInt32());
        var pp = parameters[9];
        Assert.Equal(JsonValueKind.Array, pp.ValueKind);
        Assert.Equal("*Unpack:Password", pp[0].GetProperty("Name").GetString());
        Assert.Equal("hunter2", pp[0].GetProperty("Value").GetString());
    }

    [Fact]
    public async Task NzbGet_grab_defaults_priority_to_zero_and_sends_no_password_when_Deluno_has_none()
    {
        var sent = new List<string>();
        var request = new DownloadClientGrabRequest("Pioneer.One.S01E01.1080p", "https://indexer.invalid/get/1", "tv", "TV", "altHUB");

        await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(_ =>
            RecordedReplies.Nzbget("queued").Response("\"append\""), sent)))
            .GrabAsync(NzbGetClient(), request, CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(sent));
        var parameters = document.RootElement.GetProperty("params");
        Assert.Equal(0, parameters[3].GetInt32());
        Assert.Equal(0, parameters[9].GetArrayLength());
    }

    [Theory]
    [InlineData(50, "1")]
    [InlineData(-50, "-1")]
    public async Task Sabnzbd_grab_sends_priority_when_Deluno_has_one(int requested, string expectedSabPriority)
    {
        string? query = null;
        var handler = new StubHandler(request =>
        {
            query = request.RequestUri?.Query;
            return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
        }, []);
        var request = new DownloadClientGrabRequest(
            "Example.Show.S01E01", "http://indexer.test/download/42", "tv", "tv", "Fixture indexer")
        {
            Priority = requested
        };

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .GrabAsync(SabClient(), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains($"priority={expectedSabPriority}", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sabnzbd_grab_sends_password_only_when_Deluno_has_one()
    {
        string? query = null;
        var handler = new StubHandler(request =>
        {
            query = request.RequestUri?.Query;
            return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
        }, []);
        var request = new DownloadClientGrabRequest(
            "Example.Show.S01E01", "http://indexer.test/download/42", "tv", "tv", "Fixture indexer")
        {
            Password = "s3cret"
        };

        await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .GrabAsync(SabClient(), request, CancellationToken.None);

        Assert.Contains("password=s3cret", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sabnzbd_grab_omits_priority_and_password_when_Deluno_has_neither()
    {
        string? query = null;
        var handler = new StubHandler(request =>
        {
            query = request.RequestUri?.Query;
            return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
        }, []);
        var request = new DownloadClientGrabRequest(
            "Example.Show.S01E01", "http://indexer.test/download/42", "tv", "tv", "Fixture indexer");

        await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .GrabAsync(SabClient(), request, CancellationToken.None);

        Assert.DoesNotContain("priority=", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", query, StringComparison.OrdinalIgnoreCase);
    }

    // ── Post-processing stages read as "processing" ─────────────────────────

    [Theory]
    [InlineData("PP_QUEUED")]
    [InlineData("LOADING_PARS")]
    [InlineData("VERIFYING_SOURCES")]
    [InlineData("REPAIRING")]
    [InlineData("VERIFYING_REPAIRED")]
    [InlineData("RENAMING")]
    [InlineData("UNPACKING")]
    [InlineData("MOVING")]
    [InlineData("EXECUTING_SCRIPT")]
    public void NzbGet_post_processing_stages_are_read_as_processing_not_downloading(string nativeStatus)
        => Assert.Equal(DownloadQueueStatuses.Processing, new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(_ => JsonResponse("{}"), [])))
            .NormalizeStatus(nativeStatus, progress: 100));

    [Theory]
    [InlineData("Verifying")]
    [InlineData("Repairing")]
    [InlineData("Extracting")]
    [InlineData("Moving")]
    [InlineData("Running")]
    public void Sabnzbd_post_processing_stages_are_read_as_processing_not_downloading(string nativeStatus)
        => Assert.Equal(DownloadQueueStatuses.Processing, new SabnzbdDownloadClient(new StubHttpClientFactory(new StubHandler(_ => JsonResponse("{}"), [])))
            .NormalizeStatus(nativeStatus, progress: 100));

    [Fact]
    public void Sabnzbd_ordinary_downloading_is_still_downloading()
        => Assert.Equal(DownloadQueueStatuses.Downloading, new SabnzbdDownloadClient(new StubHttpClientFactory(new StubHandler(_ => JsonResponse("{}"), [])))
            .NormalizeStatus("Downloading", progress: 40));

    // ── Low disk space folded into client health ────────────────────────────

    [Fact]
    public async Task NzbGet_snapshot_is_degraded_when_free_disk_space_is_low()
    {
        var handler = new RoutedNzbGetHandler(NzbGetList, NzbGetStatus(("result.FreeDiskSpaceMB", 500)), NzbGetHistory, NzbGetVersion);

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("GB free", snapshot.LastHealthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NzbGet_snapshot_stays_healthy_with_plenty_of_disk_space()
    {
        var handler = new RoutedNzbGetHandler(NzbGetList, NzbGetStatus(("result.FreeDiskSpaceMB", 500000)), NzbGetHistory, NzbGetVersion);

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_is_degraded_when_free_disk_space_is_low()
    {
        var handler = new RoutedSabHandler(SabQueue(("queue.diskspace1", "3.2"), ("queue.diskspacetotal1", "500.0")), SabHistory);

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("GB free", snapshot.LastHealthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_stays_healthy_with_plenty_of_disk_space()
    {
        var handler = new RoutedSabHandler(SabQueue(("queue.diskspace1", "925.3"), ("queue.diskspacetotal1", "2000.0")), SabHistory);

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
    }

    // ── Standby/pause state and client version (#812) ───────────────────────

    [Fact]
    public async Task NzbGet_snapshot_reads_paused_when_downloads_are_paused()
    {
        var handler = new RoutedNzbGetHandler(NzbGetList, NzbGetStatus(("result.FreeDiskSpaceMB", 500000), ("result.ServerStandBy", true), ("result.DownloadPaused", true), ("result.PostPaused", false), ("result.ScanPaused", false)), NzbGetHistory, NzbGetVersion);

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.ClientPaused);
        Assert.Equal(VersionOf(NzbGetVersion), snapshot.ClientVersion);
    }

    [Fact]
    public async Task NzbGet_standby_alone_with_nothing_explicitly_paused_is_not_read_as_paused()
    {
        var handler = new RoutedNzbGetHandler(NzbGetList, NzbGetStatus(("result.FreeDiskSpaceMB", 500000), ("result.ServerStandBy", true), ("result.DownloadPaused", false), ("result.PostPaused", false), ("result.ScanPaused", false)), NzbGetHistory, NzbGetVersion);

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(snapshot!.ClientPaused);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_reads_paused_and_version()
    {
        var handler = new RoutedSabHandler(SabQueue(("queue.diskspace1", "925.3"), ("queue.diskspacetotal1", "2000.0"), ("queue.paused", true)), SabHistory, SabVersion);

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.ClientPaused);
        Assert.Equal(VersionOf(SabVersion), snapshot.ClientVersion);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_is_not_paused_when_the_client_says_so()
    {
        var handler = new RoutedSabHandler(SabQueue(("queue.diskspace1", "925.3"), ("queue.diskspacetotal1", "2000.0"), ("queue.paused", false)), SabHistory, SabVersion);

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(snapshot!.ClientPaused);
    }

    // ── SABnzbd's own queue-level warnings (#813) ────────────────────────────

    [Fact]
    public async Task Sabnzbd_snapshot_reads_have_warnings_and_folds_them_into_health()
    {
        var handler = new RoutedSabWarningsHandler(
            SabQueue(("queue.diskspace1", "925.3"), ("queue.diskspacetotal1", "2000.0"), ("queue.have_warnings", "1")),
            SabHistory,
            RecordedReplies.Sabnzbd("settings").Body("mode=warnings", ("warnings", new System.Text.Json.Nodes.JsonArray("WARNING: Disk full"))));

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("WARNING: Disk full", snapshot.LastHealthMessage);
        Assert.Equal(["WARNING: Disk full"], snapshot.ClientWarnings);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_does_not_call_warnings_when_have_warnings_is_zero()
    {
        var handler = new RoutedSabWarningsHandler(
            SabQueue(("queue.diskspace1", "925.3"), ("queue.diskspacetotal1", "2000.0"), ("queue.have_warnings", "0")),
            SabHistory,
            RecordedReplies.Sabnzbd("settings").Body("mode=warnings", ("warnings", new System.Text.Json.Nodes.JsonArray("should never be fetched"))));

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
        Assert.Null(snapshot.ClientWarnings);
        Assert.False(handler.WarningsCalled);
    }

    [Fact]
    public async Task Sabnzbd_clear_warnings_calls_the_clear_api()
    {
        var handler = new RecordingSabClearHandler();

        var cleared = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).ClearWarningsAsync(SabClient(), CancellationToken.None);

        Assert.True(cleared);
        Assert.Contains("mode=warnings", handler.LastQuery);
        Assert.Contains("name=clear", handler.LastQuery);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    // Each client's own replies when it holds nothing (recorded, #1237); a test changes only the value it is about.
    private static string NzbGetList => RecordedReplies.Nzbget("removed-without-data").Body("listgroups", ("result", new System.Text.Json.Nodes.JsonArray()));
    private static string NzbGetHistory => RecordedReplies.Nzbget("removed-with-data").Body("\"history\"", ("result", new System.Text.Json.Nodes.JsonArray()));
    private static string NzbGetStatus(params (string Path, object? Value)[] edits) => RecordedReplies.Nzbget("settings").Body("\"status\"", edits);
    private static string NzbGetVersion => RecordedReplies.Nzbget("version").Body("version");
    private static string SabQueue(params (string Path, object? Value)[] edits) => RecordedReplies.Sabnzbd("idle").Body("mode=queue&output", edits);
    private static string SabHistory => RecordedReplies.Sabnzbd("idle").Body("mode=history&output");
    private static string SabVersion => RecordedReplies.Sabnzbd("version").Body("mode=version");
    private static string VersionOf(string body) => System.Text.Json.Nodes.JsonNode.Parse(body)![body.Contains("\"result\"") ? "result" : "version"]!.GetValue<string>();

    private static DownloadClientItem NzbGetClient()
        => new("client-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "secret", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DownloadClientItem SabClient()
        => new("client-1", "SABnzbd", "sabnzbd", "localhost", 8080, null, "api-key", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send, List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return send(request);
        }
    }

    /// <summary>Answers NZBGet's JSON-RPC calls by method name, as a snapshot poll makes four of them.</summary>
    private sealed class RoutedNzbGetHandler(string listgroups, string status, string history, string version) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var method = document.RootElement.GetProperty("method").GetString();
            // Each answer is NZBGet's whole recorded reply to that method.
            var reply = method switch
            {
                "listgroups" => listgroups,
                "status" => status,
                "history" => history,
                "version" => version,
                _ => NzbGetVersion
            };
            return JsonResponse(reply);
        }
    }

    /// <summary>Answers SABnzbd's <c>mode=</c> calls, as a snapshot poll makes queue, history and version calls.</summary>
    private sealed class RoutedSabHandler(string queue, string history, string? version = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var body = query.Contains("mode=history", StringComparison.OrdinalIgnoreCase)
                ? history
                : query.Contains("mode=version", StringComparison.OrdinalIgnoreCase)
                    ? version ?? SabVersion
                    : queue;
            return Task.FromResult(JsonResponse(body));
        }
    }

    /// <summary>Answers SABnzbd's queue/history/version/warnings calls, tracking whether warnings was ever asked for.</summary>
    private sealed class RoutedSabWarningsHandler(string queue, string history, string warnings) : HttpMessageHandler
    {
        public bool WarningsCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("mode=history", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(JsonResponse(history));
            if (query.Contains("mode=version", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(JsonResponse(SabVersion));
            if (query.Contains("mode=warnings", StringComparison.OrdinalIgnoreCase))
            {
                WarningsCalled = true;
                return Task.FromResult(JsonResponse(warnings));
            }

            return Task.FromResult(JsonResponse(queue));
        }
    }

    /// <summary>Records the query string of the one call ClearWarningsAsync makes.</summary>
    private sealed class RecordingSabClearHandler : HttpMessageHandler
    {
        public string? LastQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastQuery = request.RequestUri?.Query;
            return Task.FromResult(RecordedReplies.Sabnzbd("warnings-cleared").Response("name=clear"));
        }
    }
}
