using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

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
            JsonResponse("""{"version":"1.1","result":42}"""), sent)))
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
            JsonResponse("""{"version":"1.1","result":42}"""), sent)))
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
            return JsonResponse("""{"status":true,"nzo_ids":["nzo-1"]}""");
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
            return JsonResponse("""{"status":true,"nzo_ids":["nzo-1"]}""");
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
            return JsonResponse("""{"status":true,"nzo_ids":["nzo-1"]}""");
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
        var handler = new RoutedNzbGetHandler(
            listgroups: "[]",
            status: """{"DownloadRate":0,"FreeDiskSpaceMB":500}""",
            history: "[]");

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("GB free", snapshot.LastHealthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NzbGet_snapshot_stays_healthy_with_plenty_of_disk_space()
    {
        var handler = new RoutedNzbGetHandler(
            listgroups: "[]",
            status: """{"DownloadRate":0,"FreeDiskSpaceMB":500000}""",
            history: "[]");

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_is_degraded_when_free_disk_space_is_low()
    {
        var handler = new RoutedSabHandler(
            queue: """{"queue":{"speed":"0","slots":[],"diskspace1":"3.2","diskspacetotal1":"500.0"}}""",
            history: """{"history":{"slots":[]}}""");

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("GB free", snapshot.LastHealthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_stays_healthy_with_plenty_of_disk_space()
    {
        var handler = new RoutedSabHandler(
            queue: """{"queue":{"speed":"0","slots":[],"diskspace1":"925.3","diskspacetotal1":"2000.0"}}""",
            history: """{"history":{"slots":[]}}""");

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
    }

    // ── Standby/pause state and client version (#812) ───────────────────────

    [Fact]
    public async Task NzbGet_snapshot_reads_paused_when_downloads_are_paused()
    {
        var handler = new RoutedNzbGetHandler(
            listgroups: "[]",
            status: """{"DownloadRate":0,"FreeDiskSpaceMB":500000,"ServerStandBy":true,"DownloadPaused":true,"PostPaused":false,"ScanPaused":false}""",
            history: "[]",
            version: "\"21.1\"");

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.ClientPaused);
        Assert.Equal("21.1", snapshot.ClientVersion);
    }

    [Fact]
    public async Task NzbGet_standby_alone_with_nothing_explicitly_paused_is_not_read_as_paused()
    {
        var handler = new RoutedNzbGetHandler(
            listgroups: "[]",
            status: """{"DownloadRate":0,"FreeDiskSpaceMB":500000,"ServerStandBy":true,"DownloadPaused":false,"PostPaused":false,"ScanPaused":false}""",
            history: "[]",
            version: "\"21.1\"");

        var snapshot = await new NzbGetDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(NzbGetClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(snapshot!.ClientPaused);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_reads_paused_and_version()
    {
        var handler = new RoutedSabHandler(
            queue: """{"queue":{"speed":"0","slots":[],"diskspace1":"925.3","diskspacetotal1":"2000.0","paused":true}}""",
            history: """{"history":{"slots":[]}}""",
            version: """{"version":"4.3.2"}""");

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.ClientPaused);
        Assert.Equal("4.3.2", snapshot.ClientVersion);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_is_not_paused_when_the_client_says_so()
    {
        var handler = new RoutedSabHandler(
            queue: """{"queue":{"speed":"0","slots":[],"diskspace1":"925.3","diskspacetotal1":"2000.0","paused":false}}""",
            history: """{"history":{"slots":[]}}""",
            version: """{"version":"4.3.2"}""");

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(snapshot!.ClientPaused);
    }

    // ── SABnzbd's own queue-level warnings (#813) ────────────────────────────

    [Fact]
    public async Task Sabnzbd_snapshot_reads_have_warnings_and_folds_them_into_health()
    {
        var handler = new RoutedSabWarningsHandler(
            queue: """{"queue":{"speed":"0","slots":[],"diskspace1":"925.3","diskspacetotal1":"2000.0","have_warnings":"1"}}""",
            history: """{"history":{"slots":[]}}""",
            warnings: """{"warnings":["WARNING: Disk full"]}""");

        var snapshot = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Contains("WARNING: Disk full", snapshot.LastHealthMessage);
        Assert.Equal(["WARNING: Disk full"], snapshot.ClientWarnings);
    }

    [Fact]
    public async Task Sabnzbd_snapshot_does_not_call_warnings_when_have_warnings_is_zero()
    {
        var handler = new RoutedSabWarningsHandler(
            queue: """{"queue":{"speed":"0","slots":[],"diskspace1":"925.3","diskspacetotal1":"2000.0","have_warnings":"0"}}""",
            history: """{"history":{"slots":[]}}""",
            warnings: """{"warnings":["should never be fetched"]}""");

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
    private sealed class RoutedNzbGetHandler(string listgroups, string status, string history, string version = "\"0.0\"") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var method = document.RootElement.GetProperty("method").GetString();
            var result = method switch
            {
                "listgroups" => listgroups,
                "status" => status,
                "history" => history,
                "version" => version,
                _ => "null"
            };
            return JsonResponse($$"""{"version":"1.1","result":{{result}}}""");
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
                    ? version ?? """{"version":""}"""
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
            if (query.Contains("mode=version", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(JsonResponse("""{"version":""}"""));
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
            return Task.FromResult(JsonResponse("{}"));
        }
    }
}
