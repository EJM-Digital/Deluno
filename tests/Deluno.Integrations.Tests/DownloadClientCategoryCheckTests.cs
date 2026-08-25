using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests;

public sealed class DownloadClientCategoryCheckTests
{
    [Fact]
    public async Task Sabnzbd_category_check_reports_a_matching_category_as_ready()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Contains("mode=get_cats", request.RequestUri?.Query, StringComparison.OrdinalIgnoreCase);
            return JsonResponse("{\"categories\":[\"movies\",\"anime\"]}");
        });
        var client = CreateClient("sabnzbd");

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .CheckCategoryAsync(client, "anime", CancellationToken.None);

        Assert.True(result.Supported);
        Assert.True(result.Found);
        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
    }

    [Fact]
    public async Task Clients_that_cannot_list_categories_are_explicitly_marked_for_manual_verification()
    {
        var result = await new ManualCheckDownloadClient()
            .CheckCategoryAsync(CreateClient("other"), "anime", CancellationToken.None);

        Assert.False(result.Supported);
        Assert.False(result.Found);
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, result.Status);
    }

    private static DownloadClientItem CreateClient(string protocol)
        => new(
            "client-1",
            protocol == "sabnzbd" ? "SABnzbd" : "qBittorrent",
            protocol,
            "localhost",
            8080,
            null,
            protocol == "sabnzbd" ? "api-key" : null,
            $"http://localhost:8080/",
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

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class ManualCheckDownloadClient : DownloadClientBase
    {
        public override string Protocol => "other";

        public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(
            SupportsQueue: false,
            SupportsHistory: false,
            SupportsPauseResume: false,
            SupportsRemove: false,
            SupportsRecheck: false,
            SupportsImportPath: false,
            AuthMode: "none");

        public override Task<DownloadClientGrabResult> GrabAsync(
            DownloadClientItem client,
            DownloadClientGrabRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientGrabResult(client.Id, request.ReleaseName, false, "unsupported", "Not supported."));

        public override Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(
            DownloadClientItem client,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(null);

        public override Task<DownloadClientActionResult> ExecuteActionAsync(
            DownloadClientItem client,
            string action,
            string queueItemId,
            CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, false, "Not supported."));

        public override string NormalizeStatus(
            string? nativeStatus,
            double? progress,
            int? errorCode = null,
            string? errorMessage = null)
            => "unknown";
    }
}
