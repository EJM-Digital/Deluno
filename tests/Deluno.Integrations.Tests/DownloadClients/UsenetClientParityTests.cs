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

    /// <summary>Answers NZBGet's JSON-RPC calls by method name, as a snapshot poll makes three of them.</summary>
    private sealed class RoutedNzbGetHandler(string listgroups, string status, string history) : HttpMessageHandler
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
                _ => "null"
            };
            return JsonResponse($$"""{"version":"1.1","result":{{result}}}""");
        }
    }

    /// <summary>Answers SABnzbd's <c>mode=</c> calls, as a snapshot poll makes queue and history calls.</summary>
    private sealed class RoutedSabHandler(string queue, string history) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var body = query.Contains("mode=history", StringComparison.OrdinalIgnoreCase) ? history : queue;
            return Task.FromResult(JsonResponse(body));
        }
    }
}
