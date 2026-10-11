using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Scenario suite on rc.30.22 (#1225, #1207): an NZBGet job stuck at 99.3% read "downloading" at one check and "queued"
/// at the next, and was never struck, though the same client's job stuck at 0 bytes was. NZBGet says QUEUED for a job
/// that has no article in flight at that moment, which a hung article does between its retries. The job's own reply is
/// NZBGet's recorded one for a job stuck near its end (stalled-near-end); only the status is set to what the run saw.
/// A job that is queued behind a client that is moving bytes, paused, or never started, is still just waiting.
/// </summary>
public sealed class AnNzbgetJobStuckNearTheEndIsNotCalledQueuedTests
{
    [Fact]
    public async Task A_job_with_data_that_is_queued_while_the_client_moves_nothing_reads_as_downloading()
    {
        var item = Assert.Single((await Snapshot("QUEUED", activeDownloads: 0, clientRate: 0, clientPaused: false)).Queue);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
        Assert.True(item.Progress < 100d);
    }

    [Theory]
    [InlineData("QUEUED", 5_000_000, false)]
    [InlineData("QUEUED", 0, true)]
    [InlineData("PAUSED", 0, false)]
    public async Task A_job_waiting_behind_a_moving_client_or_paused_or_a_paused_client_stays_queued(string status, long rate, bool clientPaused)
    {
        var item = Assert.Single((await Snapshot(status, activeDownloads: 0, clientRate: rate, clientPaused: clientPaused)).Queue);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    [Fact]
    public async Task A_job_that_has_not_started_stays_queued_even_when_nothing_moves()
    {
        var item = Assert.Single((await Snapshot("QUEUED", activeDownloads: 0, clientRate: 0, clientPaused: false, startedBytes: 0)).Queue);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    private static async Task<DownloadClientTelemetrySnapshot> Snapshot(string status, int activeDownloads, long clientRate, bool clientPaused, long? startedBytes = null)
    {
        // NZBGet's recorded listing for a job stuck near its end: a fetch group and the stuck job; the status is the run's.
        var list = RecordedReplies.Nzbget("stalled-near-end").Body(
            "listgroups",
            ("^result", 1), ("result.0.NZBID", 29), ("result.0.NZBName", "Recorded.Stuck"), ("result.0.Status", status), ("result.0.ActiveDownloads", activeDownloads),
            ("result.0.FileSizeLo", 595073366L), ("result.0.FileSizeHi", 0), ("result.0.FileSizeMB", 567),
            ("result.0.RemainingSizeLo", (595073366L - (startedBytes ?? 594731554L))), ("result.0.RemainingSizeHi", 0), ("result.0.RemainingSizeMB", 0));
        var handler = new Handler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return body.Contains("\"listgroups\"", StringComparison.Ordinal) ? Json(list)
                : body.Contains("\"history\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("removed-with-data").Response("\"history\"", ("result", new System.Text.Json.Nodes.JsonArray()))
                : RecordedReplies.Nzbget("settings").Response("\"status\"", ("result.DownloadRate", clientRate), ("result.DownloadPaused", clientPaused));
        });

        var snapshot = await new NzbGetDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return snapshot!;
    }

    private static DownloadClientItem Client()
        => new("client-1", "NZBGet", "nzbget", "localhost", 6789, null, null, "http://localhost:6789/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
